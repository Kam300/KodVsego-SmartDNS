using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using HbAi.XboxDns;

static class CheckerStatusTests
{
    static int passed;static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Set(MainForm form,string name,object value){typeof(MainForm).GetField(name,Fields).SetValue(form,value);}
    static T Get<T>(MainForm form,string name){return (T)typeof(MainForm).GetField(name,Fields).GetValue(form);}
    static ResolverCheckResult Result(ProviderCheckState state)
    {return new ResolverCheckResult{Provider="Comss.one",Official=new ProviderCheckResult{Provider="Comss.one",Source="comss.ru/page.php?id=7315",State=state}};}
    [STAThread]static void Main()
    {
        // Never show MainForm: no startup polling, DNS writes or real checker traffic.
        using(var form=new MainForm(new TunnelSession(),false)){
            Check(form.CheckerCaption.Contains("ожидаем состояние")&&!form.CheckerConfirmed,"Startup never claims external confirmation before Windows state is read");
            Set(form,"initializing",false);Set(form,"busy",false);Set(form,"agLoading",false);Set(form,"active",false);Set(form,"defaultGoal",-1);
            Check(form.CheckerCaption.Contains("DNS выключен")&&!form.CheckerConfirmed,"Disabled DNS has a neutral checker row");
            Set(form,"active",true);Set(form,"dnsSet",0);Set(form,"secureDns",true);Set(form,"tlsDns",false);Set(form,"hasBackup",true);Set(form,"dnsConfirmed",true);Set(form,"networkAvailable",true);
            Check(form.CheckerCaption.Contains("ещё не проверен")&&!form.CheckerConfirmed,"Enabled DNS alone is not an official checker confirmation");
            Set(form,"resolverProvider",0);Set(form,"resolverTransport",DnsTransport.Https);Set(form,"resolverGoogle",false);Set(form,"resolverLocal",false);Set(form,"resolverChecking",true);
            Check(form.CheckerCaption.Contains("проверяем Comss.one")&&!form.CheckerConfirmed,"Pending checker names the selected provider without green success");
            Set(form,"resolverChecking",false);Set(form,"resolverResult",Result(ProviderCheckState.Confirmed));
            Check(form.CheckerConfirmed&&form.CheckerCaption=="Чекер сайта: Comss.one подтверждён"&&!form.CheckerRejected,"Only a current exact official confirmation turns the checker row positive");
            Check(form.CheckerTip.Contains("comss.ru")&&form.CheckerTip.Contains("результат от")&&form.CheckerTip.Contains("Собственный DoH браузера"),"Tooltip states source, observation time and Windows/browser scope");
            Set(form,"resolverResult",Result(ProviderCheckState.NotConfirmed));Check(form.CheckerRejected&&!form.CheckerConfirmed&&form.CheckerCaption.Contains("Comss.one не подтверждён"),"Explicit negative marker names the provider and cannot remain green");
            Set(form,"resolverResult",Result(ProviderCheckState.Inconclusive));Check(!form.CheckerRejected&&!form.CheckerConfirmed&&form.CheckerCaption.Contains("не удалось проверить"),"Checker outage stays neutral rather than asserting disconnected DNS");
            Set(form,"resolverResult",Result(ProviderCheckState.ProbePassed));Check(!form.CheckerConfirmed&&form.CheckerCaption.Contains("Malw отвечает")&&form.CheckerTip.Contains("Cloudflare")&&form.CheckerTip.Contains("не различает"),"Shared Malw probe cannot claim profile confirmation");
            Set(form,"resolverResult",Result(ProviderCheckState.NetworkChanged));Check(!form.CheckerConfirmed&&form.CheckerCaption.Contains("сеть изменилась"),"Network change invalidates the checker row");
            Set(form,"resolverResult",new ResolverCheckResult{Comparison=ResolverComparison.Shared});Check(!form.CheckerConfirmed&&form.CheckerCaption.Contains("нет подтверждения провайдера"),"Matching generic nodes cannot impersonate an official site result");
            Set(form,"resolverResult",Result(ProviderCheckState.Confirmed));
            Set(form,"dnsSet",7);Check(!form.CheckerConfirmed&&!form.CheckerCaption.Contains("подтверждён"),"Changing provider hides the previous confirmation");Set(form,"dnsSet",0);
            Set(form,"tlsDns",true);Check(!form.CheckerConfirmed,"Changing protocol hides the previous confirmation");Set(form,"tlsDns",false);
            Set(form,"localMode",true);Check(!form.CheckerConfirmed,"Changing connection method hides the previous confirmation");Set(form,"localMode",false);
            Set(form,"busy",true);Check(!form.CheckerConfirmed&&form.CheckerCaption.Contains("ждём применения"),"Starting/switching never displays old green confirmation");Set(form,"busy",false);
            Set(form,"active",false);Check(!form.CheckerConfirmed&&form.CheckerCaption.Contains("DNS выключен"),"Stopping removes a previously positive checker result");Set(form,"active",true);
            typeof(MainForm).GetMethod("CancelResolverCheck",Fields).Invoke(form,null);Set(form,"autoResolverCheck",false);
            Check(form.CheckerCaption.Contains("автопроверка выключена")&&!form.CheckerConfirmed,"Disabled automatic checks are clearly shown");
            int revision=Get<int>(form,"resolverRevision");
            Check(!form.AcceptManualResolverResult(7,DnsTransport.Https,revision,Result(ProviderCheckState.Confirmed)),"Testing a different provider cannot overwrite the main checker row");
            Check(!form.AcceptManualResolverResult(0,DnsTransport.Tls,revision,Result(ProviderCheckState.Confirmed)),"Testing a different protocol cannot overwrite the main checker row");
            Check(!form.AcceptManualResolverResult(0,DnsTransport.Https,revision-1,Result(ProviderCheckState.Confirmed)),"Cancelled or stale manual result cannot overwrite current state");
            Check(form.AcceptManualResolverResult(0,DnsTransport.Https,revision,Result(ProviderCheckState.Confirmed))&&form.CheckerConfirmed,"A manual check of the active provider also updates the main checker row when auto is disabled");
            foreach(bool dark in new[]{false,true}){
                typeof(MainForm).GetMethod("SetTheme",Fields).Invoke(form,new object[]{dark,false});Set(form,"themeMix",dark?1f:0f);Set(form,"powerMix",1f);
                foreach(int growth in new[]{0,180}){
                    form.ClientSize=new Size(form.MinimumSize.Width+growth,form.MinimumSize.Height+growth);
                    using(var preview=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)){
                        form.DrawToBitmap(preview,form.ClientRectangle);preview.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-checker-row-"+dark+"-"+growth+".png"));
                    }
                    var row=form.CheckerStatusBounds;var bar=(Rectangle)typeof(MainForm).GetProperty("ProviderBar",Fields).GetValue(form,null);
                    Check(row.Top>=bar.Top-28&&row.Bottom<bar.Top&&row.Left>=28,"Checker row fits below the subtitle and above provider choices, dark="+dark+" growth="+growth);
                }
            }
        }
        Console.WriteLine("All "+passed+" main checker-row checks passed; fake results only, no real checker requests or DNS changes.");
    }
}
