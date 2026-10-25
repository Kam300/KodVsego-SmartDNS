using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HbAi.XboxDns
{
    interface ITunnelSession : IDisposable
    {
        bool Running{get;} bool Connected{get;} bool Stopping{get;} string LastError{get;} string Statistics{get;}
        event Action Changed;
        void Start(int provider,bool tls);void Stop();
    }
    sealed class TunnelSession : ITunnelSession,ILocalDnsConfiguration
    {
        readonly Func<ProcessStartInfo,Process> launch;
        readonly bool local;
        readonly Func<bool> recoveryPending;
        int localAntigravity=-1;
        internal TunnelSession(Func<ProcessStartInfo,Process> launch=null,bool local=false,Func<bool> recoveryPending=null){this.launch=launch??Process.Start;this.local=local;this.recoveryPending=recoveryPending??(()=>DnsTunnel.Pending);}
        readonly object gate=new object();NamedPipeServerStream pipe;StreamWriter writer;
        volatile bool running,connected,stopping;volatile string failure,statistics="";
        public bool Running{get{return running;}}public bool Connected{get{return connected;}}public bool Stopping{get{return stopping;}}
        public string LastError{get{return failure;}}public string Statistics{get{return statistics;}}
        public event Action Changed;
        void Notify(){var handler=Changed;if(handler!=null)handler();}
        void Log(string line){DiagnosticReport.Record(local?"Локальный DNS":"Gemini / NotebookLM",TunnelDiagnostics.Line(line));}
        public void SetAntigravity(int provider){if(!local||running)throw new IOException("Сначала остановите локальный DNS.");if(provider!=-1&&!LocalDnsRouting.Supports(provider))throw new IOException("Провайдер Antigravity недоступен.");localAntigravity=provider;}
        public void Start(int provider,bool tls)
        {
            if(running)return;
            if(local?!EncryptedProvider(provider):!GoogleProvider(provider))throw new IOException("Выберите DNS с поддержкой шифрования.");
            running=true;connected=false;stopping=false;failure=null;statistics="";Notify();
            NamedPipeServerStream channel=null;Process child=null;IAsyncResult pending;
            try {
                Guid token=Guid.NewGuid();channel=DnsTunnel.Server(token);pending=channel.BeginWaitForConnection(null,null);pipe=channel;
                bool elevated=!DnsElevation.IsAdministrator();
                var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,(local?"--dns-local-worker ":"--dns-tunnel-worker ")+Process.GetCurrentProcess().Id+" "+token.ToString("N")+" "+provider+" "+(tls?"tls":"https")+(local?" "+localAntigravity:"")){UseShellExecute=elevated,CreateNoWindow=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)};
                if(elevated)start.Verb="runas";
                Log("START provider="+Dns.Name[provider]+"; protocol="+(tls?"DoT":"DoH")+"; scope=system DNS; separate window=false");
                child=launch(start);if(child==null)throw new IOException("Обработчик не запущен.");
            }catch(Exception error){if(channel!=null)channel.Dispose();pipe=null;if(child!=null)child.Dispose();running=false;failure=error is Win32Exception&&((Win32Exception)error).NativeErrorCode==1223?"Запрос прав отменён. DNS не изменён.":TunnelDiagnostics.Error(error);Log("ERROR "+failure);Notify();return;}
            var process=child;var connection=channel;
            ThreadPool.QueueUserWorkItem(_=>{
                var outcome=new TunnelSessionOutcome();string stage="authenticate";
                try {
                    if(!DnsTunnel.WaitForConnection(connection,pending,12000)||!DnsTunnel.ClientMatches(connection,process.Id))throw new IOException("Не удалось подтвердить обработчик туннеля.");
                    stage="read-status";
                    using(var output=new StreamWriter(connection,new UTF8Encoding(false),4096,true){AutoFlush=true})
                    using(var input=new StreamReader(connection,Encoding.UTF8,false,4096,true)) {
                        lock(gate){writer=output;if(stopping)output.WriteLine("STOP");}
                        string line;while((line=input.ReadLine())!=null){stage="validate-status";if(line.Length>8192)throw new IOException("Слишком длинный ответ обработчика.");
                            if(line.StartsWith("STATS ",StringComparison.Ordinal)){statistics=line.Substring(6);Notify();stage="read-status";continue;}
                            Log(line);
                            outcome.Accept(line);
                            if(line.StartsWith("CONNECTED ",StringComparison.Ordinal)){connected=true;Notify();}
                            stage="read-status";
                        }
                        stage="close-status-stream";
                    }
                }catch(Exception problem){Log("CONTROL stage="+stage+"; HRESULT=0x"+problem.HResult.ToString("X8")+"; "+TunnelDiagnostics.Error(problem));outcome.Fault(problem,stopping,stage=="close-status-stream",stage=="read-status");}
                finally {
                    bool requestedStop=stopping;connected=false;stopping=true;Notify();
                    lock(gate){writer=null;pipe=null;}
                    int exitCode=-1;bool pendingRestore=true;
                    try{connection.Dispose();process.WaitForExit();exitCode=process.ExitCode;
                        if(local)for(int retry=0;retry<40&&recoveryPending();retry++)Thread.Sleep(100);
                        pendingRestore=recoveryPending();
                    }catch(Exception problem){Log("CONTROL stage=completion; "+TunnelDiagnostics.Error(problem));outcome.Fault(problem,requestedStop);}
                    finally{process.Dispose();}
                    failure=outcome.Complete(exitCode,pendingRestore,requestedStop);running=false;stopping=false;
                    Log("CONTROL END; exit="+exitCode+"; terminal="+outcome.TerminalReceived+"; user-stop="+requestedStop+"; recovery-pending="+pendingRestore);
                    Log(failure==null?(outcome.StoppedReceived?"STOPPED; прежние DNS восстановлены":"STOPPED; запуск отменён"):"ERROR "+failure);Notify();
                }
            });
        }
        internal static bool GoogleProvider(int id){return Dns.IsAvailable(id)&&ProviderCatalog.All[id].services.Contains("Gemini")&&(DotDns.Available(id)||EncryptedDns.Templates[id]!=null);}
        internal static bool EncryptedProvider(int id){return Dns.IsAvailable(id)&&(DotDns.Available(id)||EncryptedDns.Templates[id]!=null);}
        public void Stop(){if(!running)return;stopping=true;Notify();lock(gate)try{if(writer!=null)writer.WriteLine("STOP");else if(pipe!=null)pipe.Dispose();}catch(IOException){}catch(ObjectDisposedException){}}
        public void Dispose(){Stop();}
    }
    // A valid final worker result takes precedence over a pipe error while the
    // StreamWriter flushes on disposal after the peer has already disconnected.
    sealed class TunnelSessionOutcome
    {
        internal string WorkerError,TransportError;
        internal bool TerminalReceived,StoppedReceived;
        internal void Accept(string line)
        {
            if(line=="STOPPED"){TerminalReceived=true;StoppedReceived=true;}
            else if(line.StartsWith("ERROR ",StringComparison.Ordinal)){TerminalReceived=true;if(WorkerError==null)WorkerError=string.IsNullOrWhiteSpace(line.Substring(6))?"Обработчик DNS завершился с ошибкой.":line.Substring(6);}
        }
        internal static bool ClosedPipe(Exception error)
        {var io=error as IOException;int code=error.HResult&65535;return error is ObjectDisposedException||io!=null&&(code==109||code==232||code==233);}
        internal void Fault(Exception error,bool requestedStop,bool disposing=false,bool reading=false)
        {
            if(WorkerError!=null||TransportError!=null||requestedStop||TerminalReceived&&(ClosedPipe(error)||disposing&&error is IOException))return;
            TransportError=ClosedPipe(error)||reading&&error is IOException?"Потеряна связь с обработчиком DNS. Повторное включение сначала проверит восстановление DNS.":TunnelDiagnostics.Error(error);
        }
        internal string Complete(int exitCode,bool pendingRestore,bool requestedStop)
        {
            string error=WorkerError??TransportError;
            if(pendingRestore)return (error==null?"":error+" ")+"Восстановление DNS не подтверждено. Резервные копии сохранены; повторное включение сначала проверит восстановление.";
            if(error!=null)return error;
            if(requestedStop)return null;
            if(!TerminalReceived)return "Обработчик DNS закрыл канал без результата (код "+exitCode+"). Повторное включение сначала проверит восстановление DNS.";
            return exitCode==0?null:"Обработчик DNS завершился с ошибкой (код "+exitCode+"). Подробности сохранены в журнале.";
        }
    }
    static class TunnelDiagnostics
    {
        internal const string WindowsNote="Windows может показывать «Без доступа» у DNS-адаптера: Интернет идёт через Wi-Fi/Ethernet, туннель обслуживает только DNS.";
        static readonly string[] LoggedHosts={"example.com","chatgpt.com","api.openai.com","auth.openai.com","ws.chatgpt.com","gemini.google.com","notebooklm.google.com","notebooklm.google","generativelanguage.googleapis.com","claude.ai","api.anthropic.com","elevenlabs.io","api.elevenlabs.io","cloudcode-pa.googleapis.com","daily-cloudcode-pa.googleapis.com"};
        internal static string Line(string message){string value=DiagnosticReport.Scrub(message??"").Replace('\r',' ').Replace('\n',' ');return value.Length<=2048?value:value.Substring(0,2048)+" [truncated]";}
        internal static string Error(Exception error){var parts=new List<string>();for(int i=0;error!=null&&i<4;i++,error=error.InnerException)parts.Add(error.GetType().Name+": "+error.Message);return Line(string.Join(" -> ",parts));}
        internal static string Rcode(int code){switch(code){case 0:return "NOERROR(0)";case 1:return "FORMERR(1)";case 2:return "SERVFAIL(2)";case 3:return "NXDOMAIN(3)";case 4:return "NOTIMP(4)";case 5:return "REFUSED(5)";default:return code.ToString();}}
        internal static string LogHost(byte[] query)
        {
            int end=TunnelWire.QuestionEnd(query),offset=12;var labels=new List<string>();while(offset<end-4){int length=query[offset++];if(length==0)break;labels.Add(Encoding.ASCII.GetString(query,offset,length));offset+=length;}
            string name=string.Join(".",labels).ToLowerInvariant();return LoggedHosts.Contains(name)?name:"<other>";
        }
        internal static void Snapshots(string stage,IEnumerable<AdapterBackup> records,Action<string> trace)
        {foreach(var record in records)foreach(var family in record.Families)trace(stage+" adapter="+record.Guid+"; family="+family.Family+"; manual="+family.Manual+"; DNS="+string.Join(",",family.Servers)+"; encrypted-metadata="+(family.Doh==null?0:family.Doh.Length));}
        internal static string Reply(ServiceEndpoint endpoint,ServiceHttpReply reply)
        {
            var classification=ServiceAvailability.Classify(endpoint,reply);
            string meaning=reply.Status==101?"WebSocket handshake accepted; generation NOT tested":reply.Status==401?"authorization required; account NOT tested":classification==ServiceReply.Restricted?"explicit regional restriction":reply.Status==403?"Forbidden; cause not established":classification.ToString();
            if(reply.Header("cf-mitigated").Equals("challenge",StringComparison.OrdinalIgnoreCase))meaning="Cloudflare browser challenge; generation NOT tested";
            string detail="";
            if(reply.Header("Content-Type").IndexOf("json",StringComparison.OrdinalIgnoreCase)>=0&&!string.IsNullOrEmpty(reply.Body)) {
                try{var root=new JavaScriptSerializer{MaxJsonLength=32768}.DeserializeObject(reply.Body) as Dictionary<string,object>;object error;if(root!=null&&root.TryGetValue("error",out error)){
                    var fields=error as Dictionary<string,object>;if(fields!=null)foreach(string key in new[]{"code","type","message"}){object value;if(fields.TryGetValue(key,out value)&&value is string)detail+="; "+key+"="+value;}
                }}catch(ArgumentException){}catch(InvalidOperationException){}
            }else if(!string.IsNullOrEmpty(reply.Body)) {
                var title=Regex.Match(reply.Body,@"<title\b[^>]*>(.*?)</title>",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromMilliseconds(100));
                if(title.Success)detail="; HTML title="+WebUtility.HtmlDecode(Regex.Replace(title.Groups[1].Value,"<[^>]*>",""));
            }
            return Line("HTTP="+reply.Status+"; classification="+meaning+"; server="+reply.Header("Server")+"; cf-ray="+reply.Header("cf-ray")+"; cf-mitigated="+reply.Header("cf-mitigated")+"; content-type="+reply.Header("Content-Type")+detail);
        }
        // Fixed, anonymous, read-only probes. Never read browser sessions, API keys or model inputs.
        internal static void Check(Func<byte[],byte[]> resolve,Func<ServiceEndpoint,string,CancellationToken,ServiceHttpReply> request,Action<string> trace,CancellationToken cancel,bool googleOnly=false)
        {
            trace("CHECK BEGIN UTC="+DateTime.UtcNow.ToString("O")+"; anonymous=true; model generation=false; proxy=none-explicit; OS routing still applies");
            var endpoints=new[]{
                new ServiceEndpoint{Name="Gemini",Host="gemini.google.com",Path="/",InspectHtml=true},
                new ServiceEndpoint{Name="ChatGPT",Host="chatgpt.com",Path="/",InspectHtml=true},
                new ServiceEndpoint{Name="OpenAI API",Host="api.openai.com",Path="/v1/models",Api=true,InspectHtml=true},
                new ServiceEndpoint{Name="Codex WebSocket",Host="chatgpt.com",Path="/backend-api/codex/responses",Api=true,WebSocket=true,InspectHtml=true}};
            if(googleOnly)endpoints=new[]{new ServiceEndpoint{Name="Gemini app",Host="gemini.google.com",Path="/",InspectHtml=true},new ServiceEndpoint{Name="NotebookLM",Host="notebooklm.google.com",Path="/",InspectHtml=true},new ServiceEndpoint{Name="NotebookLM entry",Host="notebooklm.google",Path="/",InspectHtml=true}};
            foreach(var endpoint in endpoints) {
                cancel.ThrowIfCancellationRequested();string[] addresses=new string[0];
                foreach(int type in new[]{1,28}) {
                    cancel.ThrowIfCancellationRequested();var query=DnsProbe.Query(endpoint.Host);TunnelPacket.Put16(query,query.Length-4,type);var clock=Stopwatch.StartNew();
                    try{var answer=resolve(query);TunnelWire.Validate(query,answer);int code=answer[3]&15;
                        if(type==1&&code==0)try{addresses=DnsProbe.Addresses(query,answer).Where(AutoDnsProbe.PublicAddress).Distinct().Take(2).ToArray();}catch(IOException){}
                        trace("CHECK DNS host="+endpoint.Host+"; qtype="+type+"; rcode="+Rcode(code)+"; answers="+TunnelPacket.U16(answer,6)+"; IPv4="+string.Join(",",addresses)+"; ms="+clock.ElapsedMilliseconds);
                    }catch(Exception error){cancel.ThrowIfCancellationRequested();trace("CHECK DNS host="+endpoint.Host+"; qtype="+type+"; ms="+clock.ElapsedMilliseconds+"; error="+Error(error));}
                }
                if(addresses.Length==0){trace("CHECK "+endpoint.Name+" skipped: no public IPv4 answer; IPv6 HTTPS not tested");continue;}
                foreach(string address in addresses) {
                    cancel.ThrowIfCancellationRequested();var clock=Stopwatch.StartNew();
                    try{var reply=request(endpoint,address,cancel);trace("CHECK "+endpoint.Name+" host="+endpoint.Host+endpoint.Path+"; address="+address+"; TLS=validated; ms="+clock.ElapsedMilliseconds+"; "+Reply(endpoint,reply));}
                    catch(Exception error){cancel.ThrowIfCancellationRequested();trace("CHECK "+endpoint.Name+"; address="+address+"; ms="+clock.ElapsedMilliseconds+"; error="+Error(error));}
                }
            }
            trace("CHECK END: DNS/TLS/HTTP diagnostics only; authentication, account and generation NOT verified; IPv6 HTTPS NOT tested");
        }
    }
    static class TunnelWire
    {
        internal static int QuestionEnd(byte[] query,bool response=false)
        {
            if(query==null||query.Length<17||query.Length>65535||(query[2]&0x78)!=0||((query[2]&128)!=0)!=response||TunnelPacket.U16(query,4)!=1)throw new IOException("Неверный DNS-пакет.");
            int end=12,length=0;
            for(int i=0;i<128;i++){if(end>=query.Length)break;int size=query[end++];if(size==0){if(end+4>query.Length)break;return end+4;}if(size>63||end+size>=query.Length||(length+=size+1)>255)break;end+=size;}
            throw new IOException("Неверный DNS-вопрос.");
        }
        internal static void Validate(byte[] query,byte[] answer)
        {
            int end=QuestionEnd(query),responseEnd=QuestionEnd(answer,true);
            if(answer[0]!=query[0]||answer[1]!=query[1]||end!=responseEnd||!query.Skip(12).Take(end-12).SequenceEqual(answer.Skip(12).Take(end-12)))throw new IOException("Ответ DNS не соответствует запросу.");
        }
        internal static byte[] Error(byte[] query,bool truncate=false)
        {int end=QuestionEnd(query);var answer=TunnelPacket.Slice(query,0,end);answer[2]=(byte)(128|(query[2]&1)|(truncate?2:0));answer[3]=(byte)(128|(truncate?0:2));Array.Clear(answer,6,6);return answer;}
    }
    sealed class TunnelUpstream
    {
        readonly int provider;readonly bool tls;
        readonly Action<string> trace;readonly Func<string,byte[],byte[]> exchange;
        int details;
        string preferred;
        internal TunnelUpstream(int provider,bool tls,Action<string> trace=null,Func<string,byte[],byte[]> exchange=null){if(!Dns.IsAvailable(provider)||(tls?!DotDns.Available(provider):EncryptedDns.Templates[provider]==null))throw new IOException("Для DNS нет выбранного шифрования.");this.provider=provider;this.tls=tls;this.trace=trace;this.exchange=exchange;}
        void Detail(string message){if(trace==null)return;int count=Interlocked.Increment(ref details);if(count<=500)trace(message);else if(count==501)trace("DNS details limit=500; aggregate counters continue");}
        internal byte[] Query(byte[] query)
        {
            TunnelWire.QuestionEnd(query);Exception last=null;string host=TunnelDiagnostics.LogHost(query);int type=TunnelPacket.U16(query,TunnelWire.QuestionEnd(query)-4);
            foreach(string address in (tls?DotDns.Servers(provider):EncryptedDns.ServersFor(provider)).OrderBy(a=>a==preferred?0:1).Take(2)) {
                var clock=Stopwatch.StartNew();string context="DNS host="+host+"; qtype="+type+"; upstream="+address+"; protocol="+(tls?"DoT":"DoH");
                try {
                    byte[] answer=exchange!=null?exchange(address,query):tls?DotDns.Exchange(address,DotDns.Host(provider),query):DohHttp2.Query(EncryptedDns.Templates[provider],query,5000,address);
                    TunnelWire.Validate(query,answer);int code=answer[3]&15;
                    if(host!="<other>"||code!=0)Detail(context+"; rcode="+TunnelDiagnostics.Rcode(code)+"; answers="+TunnelPacket.U16(answer,6)+"; bytes="+answer.Length+"; ms="+clock.ElapsedMilliseconds);
                    preferred=address;return answer;
                }catch(Exception error){last=error;Detail(context+"; ms="+clock.ElapsedMilliseconds+"; error="+TunnelDiagnostics.Error(error));}
            }
            throw new IOException("Шифрованный DNS не ответил. "+(last is DohHttp2.AddressPinningUnavailableException?"Выберите TLS.":"Проверьте сеть и провайдера."),last);
        }
    }
    // Restrict the snapshot to active physical uplinks. Never change the Wintun adapter,
    // disconnected interfaces, bindings, routes or the router's configuration.
    sealed class TunnelDnsSystem : IDnsSystem
    {
        readonly NativeDnsSystem inner=new NativeDnsSystem();
        readonly Guid[] selected;
        internal static NetworkInterface[] Uplinks(){return NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.Id.Trim('{','}')!=DnsTunnelNative.AdapterId.ToString()&&n.OperationalStatus==OperationalStatus.Up&&(n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211||n.NetworkInterfaceType==NetworkInterfaceType.Ethernet)&&n.GetIPProperties().GatewayAddresses.Any(g=>!g.Address.Equals(IPAddress.Any)&&!g.Address.Equals(IPAddress.IPv6Any))).ToArray();}
        internal static string Fingerprint(){return string.Join("|",Uplinks().OrderBy(n=>n.Id).Select(n=>n.Id+":"+string.Join(",",n.GetIPProperties().UnicastAddresses.Select(a=>a.Address.ToString()).OrderBy(a=>a))));}
        internal TunnelDnsSystem(){selected=Uplinks().Select(n=>new Guid(n.Id)).ToArray();if(selected.Length==0)throw new IOException("Нет активного Wi-Fi или Ethernet.");}
        public void EnsureSupported(){inner.EnsureSupported();}
        public DnsAdapter[] Adapters(bool activeOnly){return inner.Adapters(activeOnly).Where(a=>a.Id!=DnsTunnelNative.AdapterId&&(!activeOnly||selected.Contains(a.Id))).ToArray();}
        public FamilyBackup Read(Guid id,string family){var value=inner.Read(id,family);if(value.ProfilePresent)throw new IOException("Сначала отключите отдельную DNS-настройку профиля сети Windows.");return value;}
        public void Set(Guid id,FamilyBackup settings){inner.Set(id,settings);}
        public void Flush(){inner.Flush();}
    }
    // Session snapshots share the durable backup file, but own only their adapter IDs.
    // Old/offline adapter snapshots must survive every save, rollback and recovery.
    sealed class TunnelBackupStore : IDnsBackupStore
    {
        readonly IDnsBackupStore inner;
        readonly HashSet<Guid> scope;
        internal readonly string[] Ids;
        internal TunnelBackupStore(IDnsBackupStore inner,IEnumerable<Guid> adapters)
        {
            var ids=adapters.ToArray();
            if(ids.Length==0||ids.Length>64||ids.Any(id=>id==Guid.Empty||id==DnsTunnelNative.AdapterId)||ids.Distinct().Count()!=ids.Length)throw new IOException("Неверный список адаптеров туннеля.");
            this.inner=inner;scope=new HashSet<Guid>(ids);Ids=ids.Select(id=>id.ToString("B")).ToArray();
        }
        internal static Guid[] ParseIds(object value)
        {
            var ids=value as string[];
            if(ids==null||ids.Length==0||ids.Length>64)throw new IOException("Нет списка адаптеров прежнего туннеля. Восстановите DNS через основное окно; копии сохранены.");
            var result=new List<Guid>();
            foreach(string text in ids){Guid id;if(!Guid.TryParse(text,out id)||id==Guid.Empty||id==DnsTunnelNative.AdapterId||result.Contains(id))throw new IOException("Повреждён список адаптеров туннеля. Копии DNS сохранены.");result.Add(id);}
            return result.ToArray();
        }
        List<AdapterBackup> All(){var records=inner.Load();RegistryDnsBackupStore.Validate(records);return records;}
        public List<AdapterBackup> Load(){return All().Where(r=>scope.Contains(new Guid(r.Guid))).ToList();}
        public void Save(List<AdapterBackup> records)
        {
            RegistryDnsBackupStore.Validate(records);
            if(records.Any(r=>!scope.Contains(new Guid(r.Guid))))throw new IOException("Копия адаптера не относится к этому туннелю.");
            var retained=All().Where(r=>!scope.Contains(new Guid(r.Guid))).ToList();
            inner.Save(retained.Concat(records).ToList());
        }
        internal void EnsureReady(){if(Load().Count>0)throw new IOException("На текущем подключении осталась копия обычного DNS. Выключите его основной кнопкой и повторите запуск туннеля.");}
    }
    sealed class TunnelPump : IDisposable
    {
        readonly DnsTunnelNative native;readonly TunnelUpstream upstream;readonly TunnelTcp tcp;
        readonly SemaphoreSlim slots=new SemaphoreSlim(8);readonly CancellationTokenSource stop=new CancellationTokenSource();
        Task reading;int outstanding;internal long Answers,Failures,Refused,Servfail,Nxdomain,Rejected,Udp,Tcp,V4,V6;internal Exception Error;
        internal TunnelPump(DnsTunnelNative native,TunnelUpstream upstream){this.native=native;this.upstream=upstream;tcp=new TunnelTcp(native.Write,Resolve);}
        void Resolve(byte[] query,Action<byte[]> complete)
        {
            if(stop.IsCancellationRequested)return;
            if(!slots.Wait(0)){Interlocked.Increment(ref Rejected);complete(TunnelWire.Error(query));return;}Interlocked.Increment(ref outstanding);
            ThreadPool.QueueUserWorkItem(_=>{try{byte[] answer;try{answer=upstream.Query(query);Interlocked.Increment(ref Answers);switch(answer[3]&15){case 2:Interlocked.Increment(ref Servfail);break;case 3:Interlocked.Increment(ref Nxdomain);break;case 5:Interlocked.Increment(ref Refused);break;}}catch{answer=TunnelWire.Error(query);Interlocked.Increment(ref Failures);}if(!stop.IsCancellationRequested)complete(answer);}catch{}finally{slots.Release();Interlocked.Decrement(ref outstanding);}});
        }
        internal string Counters(){return "answers="+Interlocked.Read(ref Answers)+"; transport-errors="+Interlocked.Read(ref Failures)+"; SERVFAIL="+Interlocked.Read(ref Servfail)+"; NXDOMAIN="+Interlocked.Read(ref Nxdomain)+"; REFUSED="+Interlocked.Read(ref Refused)+"; queue-rejected="+Interlocked.Read(ref Rejected)+"; packets UDP="+Interlocked.Read(ref Udp)+" TCP="+Interlocked.Read(ref Tcp)+" IPv4="+Interlocked.Read(ref V4)+" IPv6="+Interlocked.Read(ref V6);}
        internal void Start(){reading=Task.Factory.StartNew(()=>{try{while(!stop.IsCancellationRequested){var bytes=native.Read();if(bytes==null){tcp.Tick(TunnelTcp.Now);stop.Token.WaitHandle.WaitOne(10);continue;}TunnelPacket packet;try{packet=TunnelPacket.Parse(bytes);}catch(IOException){continue;}
                    if(packet.Source.Length==16)Interlocked.Increment(ref V6);else Interlocked.Increment(ref V4);
                    if(packet.Protocol==6){Interlocked.Increment(ref Tcp);tcp.Accept(packet,TunnelTcp.Now);}else{Interlocked.Increment(ref Udp);try{TunnelWire.QuestionEnd(packet.Payload);}catch(IOException){continue;}Resolve(packet.Payload,answer=>native.Write(packet.Reply(answer.Length<=512?answer:TunnelWire.Error(packet.Payload,true))));}
                }}catch(Exception error){Error=error;stop.Cancel();}},TaskCreationOptions.LongRunning);}
        public void Dispose(){stop.Cancel();if(reading!=null)reading.Wait();tcp.Dispose();var wait=Stopwatch.StartNew();while(Interlocked.CompareExchange(ref outstanding,0,0)>0&&wait.ElapsedMilliseconds<25000)Thread.Sleep(20);/* Keep semaphore alive for a late bounded network completion. */}
    }
    static class DnsTunnel
    {
        internal const string MutexName=@"Global\HbAi.SmartDns.Settings";
        internal static bool TryWorker(string[] args,out int parent,out Guid token,out int provider,out bool tls)
        {return TryWorkerMode(args,"--dns-tunnel-worker",out parent,out token,out provider,out tls);}
        internal static bool TryWorkerMode(string[] args,string mode,out int parent,out Guid token,out int provider,out bool tls)
        {
            parent=0;token=Guid.Empty;provider=-1;tls=false;
            if(args==null||args.Length!=5||args[0]!=mode||!int.TryParse(args[1],out parent)||parent<=0||!Guid.TryParseExact(args[2],"N",out token)||token==Guid.Empty||!int.TryParse(args[3],out provider)||!Dns.IsAvailable(provider)||(args[4]!="tls"&&args[4]!="https"))return false;
            tls=args[4]=="tls";return tls?DotDns.Available(provider):EncryptedDns.Templates[provider]!=null;
        }
        internal static string PipeName(Guid token){return "KodVsego.SmartDNS.Tunnel."+token.ToString("N");}
        internal static bool Pending {get{using(var key=Registry.LocalMachine.OpenSubKey(RegistryDnsBackupStore.Key))return key!=null&&object.Equals(key.GetValue("TunnelPending"),1);}}
        internal static void Mark(bool enabled,string[] ids=null,string localOwner=null)
        {
            using(var key=Registry.LocalMachine.CreateSubKey(RegistryDnsBackupStore.Key)) {
                if(enabled){TunnelBackupStore.ParseIds(ids);if(localOwner==null)key.DeleteValue("LocalResolverOwner",false);else{Guid owner;if(!Guid.TryParseExact(localOwner,"N",out owner)||owner==Guid.Empty)throw new IOException("Неверный владелец локального DNS.");key.SetValue("LocalResolverOwner",localOwner,RegistryValueKind.String);}key.SetValue("TunnelAdapters",ids,RegistryValueKind.MultiString);key.Flush();key.SetValue("TunnelPending",1,RegistryValueKind.DWord);}
                else{key.DeleteValue("TunnelPending",false);key.Flush();key.DeleteValue("TunnelAdapters",false);key.DeleteValue("LocalResolverOwner",false);}
                key.Flush();
            }
        }
        static TunnelBackupStore RecoveryStore()
        {using(var key=Registry.LocalMachine.OpenSubKey(RegistryDnsBackupStore.Key))return new TunnelBackupStore(new RegistryDnsBackupStore(),TunnelBackupStore.ParseIds(key==null?null:key.GetValue("TunnelAdapters")));}
        internal static string RestoreSnapshot(DnsSettingsEngine engine,IDnsBackupStore store)
        {
            try{if(store.Load().Count>0)engine.Change(false,new string[0]);if(store.Load().Count==0){Mark(false);return null;}return "Копия недоступного адаптера этого туннеля сохранена. Повторите восстановление после подключения.";}
            catch(Exception error){return "Восстановление не завершено: "+DiagnosticReport.Scrub(error.Message).Replace('\n',' ');}
        }
        internal static string Recover()
        {
            using(var mutex=new Mutex(false,MutexName)){bool locked=false;try{
                try{locked=mutex.WaitOne(0);}catch(AbandonedMutexException){locked=true;}
                if(!locked)return "Туннель ещё завершает работу. Подождите и повторите восстановление.";
                if(!Pending)return null;
                if(new RegistryDnsBackupStore().Load().Count==0){Mark(false);return null;}
                var store=RecoveryStore();if(store.Load().Count>0)new DnsSettingsEngine(new NativeDnsSystem(),store).Change(false,new string[0]);
                if(store.Load().Count>0)return "Копия недоступного адаптера сохранена. Подключите его и повторите восстановление.";
                Mark(false);return null;
            }catch(Exception error){return DiagnosticReport.Scrub(error.Message);}finally{if(locked)mutex.ReleaseMutex();}}
        }
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle,out uint id);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle,out uint id);
        internal static NamedPipeServerStream Server(Guid token)
        {
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),PipeAccessRights.ReadWrite,AccessControlType.Allow));
            return new NamedPipeServerStream(PipeName(token),PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security);
        }
        internal static bool ClientMatches(NamedPipeServerStream pipe,int pid){uint actual;return GetNamedPipeClientProcessId(pipe.SafePipeHandle,out actual)&&actual==(uint)pid;}
        internal static bool ServerMatches(NamedPipeClientStream pipe,int pid){uint actual;return GetNamedPipeServerProcessId(pipe.SafePipeHandle,out actual)&&actual==(uint)pid&&string.Equals(ProcessSecurity.ImagePath(pid),Assembly.GetExecutingAssembly().Location,StringComparison.OrdinalIgnoreCase);}
        internal static bool WaitForConnection(NamedPipeServerStream pipe,IAsyncResult pending,int timeoutMilliseconds)
        {
            // Let the framework own the async handle through EndWaitForConnection,
            // including timeout and pipe-close races. The UI must never close it early.
            var connected=Task.Factory.FromAsync(pending,pipe.EndWaitForConnection);
            try {
                if(connected.Wait(timeoutMilliseconds))return true;
                pipe.Dispose();connected.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);return false;
            }catch(AggregateException error){throw error.GetBaseException();}
        }
        internal static int Worker(string[] args)
        {
            int parent,provider;Guid token;bool tls;
            if(!TryWorker(args,out parent,out token,out provider,out tls)||!DnsElevation.IsAdministrator())return 5;
            try{using(var pipe=new NamedPipeClientStream(".",PipeName(token),PipeDirection.InOut,PipeOptions.Asynchronous)) {
                pipe.Connect(10000);uint actual;if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out actual)||actual!=(uint)parent||!string.Equals(ProcessSecurity.ImagePath(parent),Assembly.GetExecutingAssembly().Location,StringComparison.OrdinalIgnoreCase))return 5;
                using(var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true))using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true}) {
                    var logGate=new object();var elapsed=Stopwatch.StartNew();string stage="initialization";
                    Action<string> trace=message=>{try{lock(logGate)writer.WriteLine("TRACE "+TunnelDiagnostics.Line(message));}catch(IOException){}catch(ObjectDisposedException){}};
                    Action<string> phase=name=>{stage=name;trace("STAGE="+name+"; session-ms="+elapsed.ElapsedMilliseconds);};
                    var command=reader.ReadLineAsync();bool locked=false,changed=false;DnsSettingsEngine engine=null;TunnelBackupStore snapshots=null;
                    command.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                    using(var mutex=new Mutex(false,MutexName))using(var native=new DnsTunnelNative()) {
                        string result=null;
                        try {
                            trace("SESSION UTC="+DateTime.UtcNow.ToString("O")+"; version="+MainForm.VersionLabel+"; OS="+Environment.OSVersion.VersionString+"; process64="+Environment.Is64BitProcess+"; provider="+Dns.Name[provider]+"; protocol="+(tls?"DoT":"DoH")+"; endpoint="+(tls?DotDns.Host(provider):EncryptedDns.Templates[provider]));
                            trace(TunnelDiagnostics.WindowsNote);phase("guard-and-snapshot");
                            try{locked=mutex.WaitOne(0);}catch(AbandonedMutexException){locked=true;}
                            if(!locked)throw new IOException("Другая операция DNS ещё выполняется.");
                            if(Pending){phase("recover-previous-tunnel");string recovery=Recover();if(recovery!=null)throw new IOException(recovery);trace("Previous tunnel DNS restored before new startup");}
                            var system=new TunnelDnsSystem();var allSnapshots=new RegistryDnsBackupStore();
                            snapshots=new TunnelBackupStore(allSnapshots,system.Adapters(true).Select(a=>a.Id));snapshots.EnsureReady();
                            foreach(var adapter in system.Adapters(true))trace("ACTIVE adapter="+adapter.Id);
                            foreach(var uplink in TunnelDnsSystem.Uplinks())trace("UPLINK type="+uplink.NetworkInterfaceType+"; original Windows DNS="+string.Join(",",uplink.GetIPProperties().DnsAddresses.Select(ip=>ip.ToString())));
                            int retained=allSnapshots.Load().Count-snapshots.Load().Count;
                            if(retained>0)writer.WriteLine("INFO Копии других адаптеров сохранены: "+retained+". Они не мешают текущему подключению.");
                            if(VpnCheck.Scan().NeedsAttention)throw new IOException("Сначала отключите другой VPN.");
                            string conflict=EncryptedDns.PolicyConflict(NrptRegistryReader.Read(true));if(conflict!=null)throw new IOException(conflict);
                            foreach(var n in NetworkInterface.GetAllNetworkInterfaces())if(n.GetIPProperties().UnicastAddresses.Any(a=>a.Address.ToString().StartsWith("198.18.254.",StringComparison.Ordinal)||a.Address.ToString().StartsWith("fd6b:7653:646e:",StringComparison.OrdinalIgnoreCase)))throw new IOException("Адрес DNS-туннеля уже занят.");
                            phase("encrypted-upstream-preflight");
                            var upstream=new TunnelUpstream(provider,tls,trace);var query=DnsProbe.Query("example.com");var preflight=upstream.Query(query);
                            if((preflight[3]&15)!=0)throw new IOException("Проверка example.com: "+TunnelDiagnostics.Rcode(preflight[3]&15)+". Сервер не подтвердил обычное разрешение DNS; настройки сети не изменены.");
                            DnsProbe.Validate(query,preflight);
                            if(command.IsCompleted)return 0;
                            string fingerprint=TunnelDnsSystem.Fingerprint();engine=new DnsSettingsEngine(system,snapshots);
                            phase("create-wintun");native.Open();trace("WINTUN ready; DNS IPv4="+DnsTunnelNative.V4+"; IPv6="+DnsTunnelNative.V6+"; default route=not added");
                            using(var pump=new TunnelPump(native,upstream)) {
                                pump.Start();try {phase("local-udp-tcp-ipv4-ipv6");ProbeLocal(trace);
                                if(command.IsCompleted)return 0;
                                if(fingerprint!=TunnelDnsSystem.Fingerprint())throw new IOException("Подключение изменилось. Повторите запуск туннеля.");
                                phase("apply-dns-and-verify");Mark(true,snapshots.Ids);changed=true;engine.ChangeVerified(new[]{DnsTunnelNative.V4,DnsTunnelNative.V6},null,false,()=>{ProbeLocal(trace);SystemDnsVerification.Verify();});
                                TunnelDiagnostics.Snapshots("ORIGINAL",snapshots.Load(),trace);phase("connected");
                                lock(logGate)writer.WriteLine("CONNECTED "+Dns.Name[provider]+" · "+(tls?"DoT":"DoH"));
                                int ticks=0;
                                while(!command.IsCompleted&&pump.Error==null){if(fingerprint!=TunnelDnsSystem.Fingerprint())throw new IOException("Сеть изменилась. Туннель выключен; включите его заново.");lock(logGate)writer.WriteLine("STATS "+Interlocked.Read(ref pump.Answers)+" "+(Interlocked.Read(ref pump.Failures)+Interlocked.Read(ref pump.Refused)+Interlocked.Read(ref pump.Servfail)+Interlocked.Read(ref pump.Rejected)));if(ticks++%10==0)trace("COUNTERS "+pump.Counters());Thread.Sleep(1000);}
                                if(pump.Error!=null)throw new IOException("Обработчик туннеля остановлен.");
                                }finally{trace("FINAL COUNTERS "+pump.Counters());if(changed){string previousStage=stage;phase("restore-original-dns");string restoration=RestoreSnapshot(engine,snapshots);trace("RESTORE="+(restoration??"complete")+"; own snapshots remaining="+snapshots.Load().Count);if(restoration!=null)result=restoration;changed=false;stage=previousStage;}}
                            }
                        }catch(Exception error){result=DiagnosticReport.Scrub(error.Message).Replace('\n',' ');trace("ERROR stage="+stage+"; session-ms="+elapsed.ElapsedMilliseconds+"; "+TunnelDiagnostics.Error(error));}
                        finally {
                            if(changed){string restoration=RestoreSnapshot(engine,snapshots);if(restoration!=null)result=restoration;}
                            if(locked)mutex.ReleaseMutex();
                        }
                        trace("SESSION END; ms="+elapsed.ElapsedMilliseconds+"; result="+(result??"stopped"));
                        foreach(string entry in DiagnosticReport.Recent(16000).Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))trace("WORKER "+entry);
                        try{lock(logGate)writer.WriteLine(result==null?"STOPPED":"ERROR "+result);}catch(IOException){}
                        return result==null?0:1;
                    }
                }
            }}catch{return 1;}
        }
        internal static void ProbeLocal(Action<string> trace=null)
        {
            foreach(string address in new[]{DnsTunnelNative.V4,DnsTunnelNative.V6}) {
                var ip=IPAddress.Parse(address);var query=DnsProbe.Query("example.com");
                var clock=Stopwatch.StartNew();
                if(trace!=null)trace("LOCAL UDP start; address="+address);
                using(var udp=new UdpClient(ip.AddressFamily)){udp.Client.ReceiveTimeout=7000;udp.Connect(ip,53);udp.Send(query,query.Length);IPEndPoint from=null;DnsProbe.Validate(query,udp.Receive(ref from));}
                if(trace!=null)trace("LOCAL UDP address="+address+"; ms="+clock.ElapsedMilliseconds+"; result=OK");clock.Restart();
                if(trace!=null)trace("LOCAL TCP start; address="+address);
                using(var tcp=new TcpClient(ip.AddressFamily)) {
                    var connecting=tcp.BeginConnect(ip,53,null,null);using(var wait=connecting.AsyncWaitHandle){if(!wait.WaitOne(5000))throw new IOException("Локальный TCP DNS не отвечает.");tcp.EndConnect(connecting);}
                    tcp.ReceiveTimeout=tcp.SendTimeout=7000;
                    using(var stream=tcp.GetStream()) {
                        var frame=new byte[query.Length+2];TunnelPacket.Put16(frame,0,query.Length);Buffer.BlockCopy(query,0,frame,2,query.Length);stream.Write(frame,0,frame.Length);
                        int hi=stream.ReadByte(),lo=stream.ReadByte();if(hi<0||lo<0||hi*256+lo<12)throw new IOException("Короткий ответ локального TCP DNS.");
                        var answer=new byte[hi*256+lo];int offset=0;while(offset<answer.Length){int count=stream.Read(answer,offset,answer.Length-offset);if(count==0)throw new IOException("Локальный TCP DNS закрыл соединение.");offset+=count;}DnsProbe.Validate(query,answer);
                    }
                }
                if(trace!=null)trace("LOCAL TCP address="+address+"; ms="+clock.ElapsedMilliseconds+"; result=OK");
            }
        }
    }
}
