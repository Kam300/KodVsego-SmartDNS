using System;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Diagnostics;

namespace HbAi.XboxDns
{
    static class AgCommonSearch
    {
        internal static AutoDnsResult[] Search(int[] providers,bool tls,int special,bool specialTls,CancellationToken cancel,Func<bool,IAutoDnsProbe> probes=null)
        {
            var specialProbe=probes==null?new AutoDnsProbe(specialTls,8000,true):probes(specialTls);
            // Preserve the special route, but require valid DNS answers for both API domains.
            foreach(string host in AntigravityDns.Domains){cancel.ThrowIfCancellationRequested();if(!specialProbe.Resolve(special,true,host,cancel).Any(AutoDnsProbe.PublicAddress))throw new IOException("Отдельный DNS Antigravity не вернул публичный адрес: "+host);}
            var results=new ConcurrentBag<AutoDnsResult>();
            Parallel.ForEach(providers.Distinct().Take(4),new ParallelOptions{MaxDegreeOfParallelism=2,CancellationToken=cancel},provider=>{
                var watch=Stopwatch.StartNew();var trace=new StringBuilder();bool useTls=tls&&DotDns.Available(provider);
                var result=new AutoDnsResult{Provider=provider,Tls=useTls,Encrypted=true};
                trace.AppendLine("Common="+Dns.Name[provider]+"; protocol="+(useTls?"DoT":"DoH")+"; Antigravity="+Dns.Name[special]+"; special protocol="+(specialTls?"DoT":"DoH"));
                using(var limit=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                    limit.CancelAfter(30000);
                    try{var probe=probes==null?new AutoDnsProbe(useTls,8000,true):probes(useTls);
                        foreach(string host in new[]{"accounts.google.com","oauth2.googleapis.com"}){
                            limit.Token.ThrowIfCancellationRequested();var addresses=probe.Resolve(provider,true,host,limit.Token);Exception last=null;bool ok=false;
                            trace.AppendLine("Bootstrap DNS "+host+" -> "+string.Join(",",addresses));
                            foreach(string address in addresses.Distinct().Take(2)){try{probe.Https(host,address,true,limit.Token);ok=true;trace.AppendLine("Bootstrap HTTPS OK "+host+" @ "+address);break;}catch(OperationCanceledException){throw;}catch(Exception error){last=error;}}
                            if(!ok)throw new IOException("Bootstrap "+host+": "+(last==null?"нет адреса":last.Message));
                        }
                    }catch(OperationCanceledException){if(cancel.IsCancellationRequested)throw;result.Error="Проверка основного DNS превысила 30 с";}
                    catch(Exception error){result.Error=error.Message;trace.AppendLine(error.ToString());}
                }
                result.Milliseconds=(int)watch.ElapsedMilliseconds;result.Trace=DiagnosticReport.Scrub(trace+"\nDNS/API authentication and model generation NOT verified; combination is a trial until a client answer.");results.Add(result);
            });
            return results.OrderBy(r=>r.Provider).ToArray();
        }
    }
    static class AgFailoverSearch
    {
        // Each result describes the exact encrypted transport later sent to the worker.
        internal static AutoDnsResult[] Search(int[] providers,int current,bool currentTls,CancellationToken cancel,Func<bool,IAutoDnsProbe> probes=null,int[] allowedRoutes=null)
        {
            int goal=AutoDns.GoalIndex("Antigravity");
            var pairs=providers.Distinct().Where(LocalDnsRouting.Supports).SelectMany(p=>new[]{false,true}.Where(t=>!t||DotDns.Available(p)).Select(t=>new{Provider=p,Tls=t})).Where(p=>(p.Provider!=current||p.Tls!=currentTls)&&(allowedRoutes==null||allowedRoutes.Contains(AgFailoverPolicy.Route(p.Provider,p.Tls)))).ToArray();
            var results=new ConcurrentBag<AutoDnsResult>();
            Parallel.ForEach(pairs,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=cancel},pair=>{
                using(var limit=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                    limit.CancelAfter(45000);
                    try{foreach(var item in AutoDns.Search(goal,true,probes==null?new AutoDnsProbe(pair.Tls,8000,true):probes(pair.Tls),limit.Token,null,pair.Provider,pair.Tls,new[]{pair.Provider}))results.Add(item);}
                    catch(OperationCanceledException){if(cancel.IsCancellationRequested)throw;results.Add(new AutoDnsResult{Provider=pair.Provider,Tls=pair.Tls,Encrypted=true,Error="Проверка варианта превысила 45 с",Trace="Provider="+Dns.Name[pair.Provider]+"; protocol="+(pair.Tls?"TLS":"HTTPS")+"; candidate timeout=45000 ms"});}
                }
            });
            return results.OrderBy(r=>r.Provider).ThenBy(r=>r.Tls).ToArray();
        }
    }
    partial class MainForm
    {
        readonly AgLogMonitor agLogs=new AgLogMonitor();readonly AgFailoverPolicy agFailover=new AgFailoverPolicy();
        bool agAutoDns=AgFailoverPreference.Read(),agWatch;int agWatchedProvider=-1,agAutomaticPending=-1;long agWatchEpoch;
        DateTime agWatchSince;CancellationTokenSource agSearch;string agAutoStatus="Автосмена ожидает ошибок клиента";
        internal Func<int[],CancellationToken,AutoDnsResult[]> AgFailoverProbe;
        internal Func<int[],CancellationToken,AutoDnsResult[]> AgCommonProbe;
        int agRouteChanges,pendingAgCommon=-1,agWorkingCommon=-1;bool pendingAgCommonTls;
        readonly System.Collections.Generic.HashSet<int> agTriedCommon=new System.Collections.Generic.HashSet<int>();
        bool AgAutoEligible {get{return agAutoDns&&localMode&&localAntigravity&&ManagedDns.Connected&&!ManagedDns.Stopping&&!busy;}}
        void ResetAgHealthWatch()
        {
            agWatch=false;agWatchEpoch++;agFailover.ResetSignal();if(agSearch!=null){agSearch.Cancel();agSearch=null;}
        }
        void TickAntigravityHealth()
        {
            if(!AgAutoEligible){if(agWatch||agSearch!=null)ResetAgHealthWatch();}
            else if(!agWatch||agWatchedProvider!=localAntigravityProvider){ResetAgHealthWatch();agWatch=true;agWatchedProvider=localAntigravityProvider;agWatchSince=DateTime.Now;}
            agLogs.Poll(items=>{if(IsDisposed||Disposing)return;try{BeginInvoke((Action)(()=>ReceiveAgHealth(items)));}catch(InvalidOperationException){}});
        }
        internal void ReceiveAgHealth(AgHealthEvent[] items)
        {
            if(IsDisposed||!AgAutoEligible||!agWatch)return;
            foreach(var item in items.OrderBy(i=>i.When)){
                if(item.When<agWatchSince)continue;
                if(item.Kind==AgHealthKind.Answer||item.Kind==AgHealthKind.Capacity||item.Kind==AgHealthKind.Quota||item.Kind==AgHealthKind.Authentication||item.Kind==AgHealthKind.Server){
                    ResetAgHealthWatch();if(item.Kind==AgHealthKind.Answer){agFailover.Answer(localAntigravityProvider,localAntigravityTls);agWorkingCommon=dnsSet;agTriedCommon.Remove(dnsSet);DiagnosticReport.Record("Связка Antigravity","Client stream started; common="+Dns.Name[dnsSet]+"; common protocol="+(tlsDns?"DoT":"DoH")+"; separate="+Dns.Name[localAntigravityProvider]+"; separate protocol="+(localAntigravityTls?"DoT":"DoH")+"; stream completion NOT verified");}
                    agAutoStatus=item.Kind==AgHealthKind.Answer?"Клиент начал получать ответ модели":item.Kind==AgHealthKind.Capacity?"Модель перегружена · DNS не меняем":item.Kind==AgHealthKind.Quota?"Лимит запросов · DNS не меняем":item.Kind==AgHealthKind.Authentication?"Ошибка входа · DNS не меняем":"Ошибка сервера · DNS не меняем";Invalidate();return;
                }
                if(agSearch==null&&agFailover.Observe(item,localAntigravityProvider,DateTime.Now,localAntigravityTls))StartAgFailover(item.Kind,item.When);
            }
        }
        void StartAgFailover(AgHealthKind reason,DateTime failureAt)
        {
            if(!AgAutoEligible||agSearch!=null)return;
            int current=localAntigravityProvider,goal=AutoDns.GoalIndex("Antigravity");long epoch=agWatchEpoch;
            var allowed=agFailover.Candidates(UiProviders,current,DateTime.Now);
            DiagnosticReport.Record("Автосмена Antigravity","Reason="+reason+"; previous="+Dns.Name[current]+"; candidates="+string.Join(",",allowed)+"; common DNS unchanged");
            if((allowed.Length==0&&!DotDns.Available(current))||goal<0){agAutoStatus="Автосмена: нет запасных DNS · повторите позже";Invalidate();return;}
            bool currentTls=localAntigravityTls;
            int common=dnsSet;bool commonTls=tlsDns;int changes=agRouteChanges;
            int workingCommon=agWorkingCommon;
            var commonCandidates=UiProviders.Where(id=>id!=common&&!agTriedCommon.Contains(id)&&Dns.IsAvailable(id)&&EncryptedDns.Templates[id]!=null&&RuntimeProviders.All[id].autoGoals.Contains("Gemini")).OrderBy(id=>id==workingCommon?0:1).ToArray();
            var allowedRoutes=allowed.Concat(new[]{current}).Distinct().SelectMany(id=>new[]{false,true}.Where(t=>(!t||DotDns.Available(id))&&agFailover.CanProbe(id,t,DateTime.Now)).Select(t=>AgFailoverPolicy.Route(id,t))).ToArray();
            var cancel=CancellationTokenSource.CreateLinkedTokenSource(autoLifetime.Token);agSearch=cancel;cancel.CancelAfter(120000);
            agAutoStatus="Автосмена: проверяем запасные DNS…";Invalidate();
            ThreadPool.QueueUserWorkItem(_=>{
                AutoDnsResult[] results=null;AutoDnsResult commonBest=null;string failure=null;
                try{results=AgFailoverProbe==null?AgFailoverSearch.Search(allowed.Concat(new[]{current}).ToArray(),current,currentTls,cancel.Token,null,allowedRoutes):AgFailoverProbe(allowed,cancel.Token);foreach(var result in results)DiagnosticReport.Record("Подбор Antigravity","Provider="+Dns.Name[result.Provider]+"; protocol="+(result.Tls?"DoT":"DoH")+"; result="+(result.Ok?"reachable":result.Error)+"; elapsed="+result.Milliseconds+" ms\n"+AutoDns.Diagnostic(goal,new[]{result}));}
                catch(OperationCanceledException){failure="Проверка отменена или истекли 120 с";}catch(Exception problem){failure=problem.GetType().Name;}
                if(!cancel.IsCancellationRequested&&(AgFailoverProbe==null||AgCommonProbe!=null)&&(changes>0||!(results??new AutoDnsResult[0]).Any(r=>r.Ok&&!r.NeedsBrowser))&&commonCandidates.Length>0){
                    try{var combinations=AgCommonProbe==null?AgCommonSearch.Search(commonCandidates,commonTls,current,currentTls,cancel.Token):AgCommonProbe(commonCandidates,cancel.Token);
                        foreach(var result in combinations)DiagnosticReport.Record("Связка Antigravity","Common="+Dns.Name[result.Provider]+"; Antigravity="+Dns.Name[current]+"; result="+(result.Ok?"bootstrap reachable; client not verified":result.Error)+"\n"+result.Trace);
                        commonBest=combinations.Where(r=>r.Ok&&!r.NeedsBrowser).OrderBy(r=>r.Provider==workingCommon?0:1).ThenBy(r=>r.Milliseconds).FirstOrDefault();
                    }catch(OperationCanceledException){failure="Подбор связки отменён";}catch(Exception problem){failure=problem.Message;DiagnosticReport.Record("Связка Antigravity",DiagnosticReport.Scrub(problem.ToString()));}
                }
                if(profilePreferences&&!cancel.IsCancellationRequested)try{agLogs.Collect();}catch(Exception problem){DiagnosticReport.Record("Журнал Antigravity",problem.GetType().Name);}
                if(IsDisposed||Disposing){cancel.Dispose();return;}
                try{BeginInvoke((Action)(()=>{
                    try{
                        if(agSearch!=cancel||epoch!=agWatchEpoch||!AgAutoEligible||localAntigravityProvider!=current||dnsSet!=common||tlsDns!=commonTls)return;
                        var stop=agLogs.StopEventAfter(failureAt);if(stop!=null&&stop.When>=agWatchSince){ReceiveAgHealth(new[]{stop});return;}
                        agSearch=null;
                        if(!cancel.IsCancellationRequested&&commonBest!=null&&TryAgCommon(commonBest,current,currentTls))return;
                        foreach(var result in results??new AutoDnsResult[0])if(!result.Ok||result.NeedsBrowser)agFailover.Failed(result.Provider,DateTime.Now,result.Tls);
                        var best=agFailover.Best((results??new AutoDnsResult[0]).Where(r=>UiProviders.Contains(r.Provider)&&LocalDnsRouting.Supports(r.Provider)));
                        if(cancel.IsCancellationRequested||best==null){agAutoStatus="Автосмена: запасной DNS не найден";DiagnosticReport.Record("Автосмена Antigravity",failure??"No reachable replacement; previous route retained");return;}
                        if(!SwitchLocalAntigravity(best.Provider,best.Tls)){agAutoStatus="Автосмена отменена: подключение изменилось";return;}
                        agAutomaticPending=best.Provider;agAutoStatus="Автосмена: ждём подтверждения "+Dns.Name[best.Provider];
                    }finally{if(agSearch==cancel)agSearch=null;cancel.Dispose();Invalidate();}
                }));}catch(InvalidOperationException){cancel.Dispose();}
            });
        }
        void CompleteAgFailover(int requested,bool success)
        {
            if(agAutomaticPending!=requested)return;agAutomaticPending=-1;
            agAutoStatus=success?"Автосмена: "+Dns.Name[requested]+" · "+(pendingAntigravityTls?"DoT":"DoH")+" · перезапустите Antigravity":"Автосмена не применена · прежний DNS сохранён";
            if(!success)agFailover.Failed(requested,DateTime.Now,pendingAntigravityTls);
            else agRouteChanges++;
            DiagnosticReport.Record("Автосмена Antigravity","Requested="+Dns.Name[requested]+"; acknowledged="+success+"; common DNS unchanged; model NOT verified");
        }
        internal bool TryAgCommon(AutoDnsResult result,int special,bool specialTls)
        {
            var session=localSession as ILocalDnsSwitchSession;
            if(!AgAutoEligible||session==null||session.Switching||result==null||!result.Ok||!result.Encrypted||result.NeedsBrowser||!UiProviders.Contains(result.Provider)||!Dns.IsAvailable(result.Provider)||result.Provider==dnsSet||localAntigravityProvider!=special||localAntigravityTls!=specialTls||result.Tls&&!DotDns.Available(result.Provider))return false;
            ResetAgHealthWatch();error=null;
            try{session.SwitchProvider(result.Provider,result.Tls);pendingAgCommon=result.Provider;pendingAgCommonTls=result.Tls;agTriedCommon.Add(result.Provider);busy=true;}
            catch(Exception problem){error=TunnelDiagnostics.Error(problem);return false;}
            agAutoStatus="Авто: проверяем связку · основной "+Dns.Name[result.Provider]+" + Antigravity "+Dns.Name[special];
            DiagnosticReport.Record("Связка Antigravity","Requested common="+Dns.Name[result.Provider]+"; protocol="+(result.Tls?"DoT":"DoH")+"; separate route preserved="+Dns.Name[special]+"; awaiting worker ACK; client not verified");Invalidate();return true;
        }
        void CompleteAgCommon(ILocalDnsSwitchSession session)
        {
            if(pendingAgCommon<0||session==null||session.Switching&&active&&!ManagedDns.Stopping)return;
            int requested=pendingAgCommon;pendingAgCommon=-1;
            bool ok=active&&ManagedDns.Connected&&!ManagedDns.Stopping&&string.IsNullOrEmpty(ManagedDns.LastError)&&session.CurrentProvider==requested&&session.CurrentTls==pendingAgCommonTls;
            agAutoStatus=ok?"Связка: "+Dns.Name[requested]+" + "+Dns.Name[localAntigravityProvider]+" · ожидаем ответ клиента":"Связка не применена · прежний DNS сохранён";
            DiagnosticReport.Record("Связка Antigravity","Common="+Dns.Name[requested]+"; acknowledged="+ok+"; separate route="+Dns.Name[localAntigravityProvider]+"; client not verified; no restart or dialog");
        }
        void SaveAgAutoPreference(bool enabled)
        {
            if(profilePreferences)using(var key=Registry.CurrentUser.CreateSubKey(CustomDns.RegistryPath))key.SetValue(AgFailoverPreference.Value,enabled?1:0,RegistryValueKind.DWord);
            if(agAutoDns!=enabled){ResetAgHealthWatch();agAutoDns=enabled;agAutoStatus="Автосмена ожидает ошибок клиента";}
        }
    }
}
