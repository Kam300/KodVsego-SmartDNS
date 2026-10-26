using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using HbAi.XboxDns;

static class ProviderOptionsTests
{
    static int passed;static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Set(object item,string name,object value){item.GetType().GetField(name,Fields).SetValue(item,value);}
    static T Get<T>(object item,string name){return (T)item.GetType().GetField(name,Fields).GetValue(item);}
    sealed class Session : ITunnelSession,ILocalDnsSwitchSession,ILocalDnsConfiguration,ILocalAntigravitySwitchSession
    {
        public bool Running{get;private set;}public bool Connected{get;private set;}public bool Stopping{get;private set;}
        public string LastError{get;private set;}public string Statistics{get{return "0 0";}}
        public bool Switching{get;private set;}public int CurrentProvider{get;private set;}public bool CurrentTls{get;private set;}
        public event Action Changed;internal int Starts,Stops,Switches,Requested,Special=-1;internal bool RequestedTls;
        bool specialRequest;public int CurrentAntigravity{get{return Special;}}
        public void SwitchAntigravity(int provider){Requested=provider;specialRequest=true;Switching=true;Notify();}
        void Notify(){if(Changed!=null)Changed();}
        public void Start(int provider,bool tls){Starts++;CurrentProvider=provider;CurrentTls=tls;Running=Connected=true;Notify();}
        public void Stop(){Stops++;Running=Connected=Switching=false;Notify();}
        public void SwitchProvider(int provider,bool tls){Switches++;Requested=provider;RequestedTls=tls;specialRequest=false;Switching=true;Notify();}
        internal void Complete(bool success){if(success){if(specialRequest)Special=Requested;else{CurrentProvider=Requested;CurrentTls=RequestedTls;}}LastError=success?null:"Failed candidate; old route retained";Switching=false;Notify();}
        public void SetAntigravity(int provider){Special=provider;}public void Dispose(){}
    }
    sealed class Probe : IAutoDnsProbe
    {
        internal int Calls;internal bool Encrypted,Tls,Fail;
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel){cancel.ThrowIfCancellationRequested();Calls++;Encrypted=encrypted;if(Fail)throw new IOException();return new[]{"93.184.215.14"};}
        public void Https(string host,string address,bool api,CancellationToken cancel){}
        public ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel){var reply=new ServiceHttpReply{Status=endpoint.Api?401:200,Body=endpoint.Api?"{\"error\":{\"message\":\"Missing authentication\"}}":null};reply.Headers["Content-Type"]=endpoint.Api?"application/json":"text/html";return reply;}
    }
    static string Await(LocalDnsSwitchController controller,LocalDnsRouting routes,Action flush)
    {string reply=null;if(!SpinWait.SpinUntil(()=>{controller.Poll(routes,flush,line=>reply=line);return reply!=null;},3000))throw new Exception("Switch probe did not complete");return reply;}
    [STAThread]static void Main()
    {
        Check(ProviderPreferences.Normalize(new[]{0,0,-1,1,3,999,6}).SequenceEqual(new[]{0,6}),"Preferences retain stable enabled IDs only");
        string keyName=@"Software\KodVsego\Tests\ProviderOptions-"+Guid.NewGuid().ToString("N");
        try{using(var key=Registry.CurrentUser.CreateSubKey(keyName)){
            Check(ProviderPreferences.Read(key).Length==0,"Missing preference preserves the complete provider list");
            ProviderPreferences.Save(key,new[]{0,6});Check(ProviderPreferences.Read(key).SequenceEqual(new[]{0,6}),"Visibility survives a settings round trip in an isolated test key");
            bool refused=false;try{ProviderPreferences.Save(key,Dns.AvailableProviders);}catch(InvalidOperationException){refused=true;}
            Check(refused&&ProviderPreferences.Read(key).SequenceEqual(new[]{0,6}),"Hiding every provider is rejected without overwriting saved preferences");
        }}finally{Registry.CurrentUser.DeleteSubKey(keyName,false);}
        int goal=AutoDns.GoalIndex("Gemini");var probe=new Probe();
        Check(AutoDns.Candidates(goal,true,false,new[]{6}).SequenceEqual(new[]{6}),"Auto candidates respect the visible-provider snapshot");
        var result=AutoDns.Search(goal,true,probe,CancellationToken.None,null,null,false,new[]{6});
        Check(result.Length==1&&result[0].Provider==6&&probe.Calls==2,"Auto performs website and API checks only for allowed providers");
        Check(AutoDns.Search(goal,true,new Probe(),CancellationToken.None,null,null,false,new int[0]).Length==0,"Empty compatible selection starts no network probes");
        probe=new Probe();var latency=ProviderLatency.Measure(5,DnsTransport.Plain,CancellationToken.None,probe);
        Check(latency.Error==null&&latency.Protocol==DnsTransport.Https&&probe.Encrypted&&probe.Calls==1&&latency.MeasuredUtc!=default(DateTime),"Latency measures a DNS transaction and never downgrades encryption-only providers");
        Check(ProviderLatency.Measure(6,DnsTransport.Https,CancellationToken.None,new Probe{Fail=true}).Error!=null,"Latency failure cannot appear as a zero millisecond success");
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{ProviderLatency.Measure(6,DnsTransport.Https,cancel.Token,new Probe());}catch(OperationCanceledException){stopped=true;}Check(stopped,"Closed latency measurement honors cancellation");}
        foreach(string bad in new[]{"STOP","SWITCH 3 https","SWITCH 6 plaintext","SWITCH 999 tls","SWITCH 6 https extra","SWITCH -1 https"}){bool invalid=false;try{LocalDnsSwitchCommand.Parse(bad);}catch(IOException){invalid=true;}Check(invalid,"Reject invalid live switch command "+bad);}
        var request=LocalDnsSwitchCommand.Parse("SWITCH 6 https");var query=DnsProbe.Query("example.com");
        var routes=new LocalDnsRouting(q=>new byte[]{1},q=>new byte[]{9});var controller=new LocalDnsSwitchController();int flushes=0;
        controller.Begin(request,c=>q=>new byte[]{2});
        Check(Await(controller,routes,()=>flushes++).StartsWith("SWITCHED 6 https")&&routes.Query(query)[0]==2&&flushes==1,"A probed replacement commits once and flushes the Windows cache");
        Check(routes.Query(DnsProbe.Query(AntigravityDns.Domains[0]))[0]==9,"Main provider switching preserves the separate Antigravity route");
        controller.Begin(request,c=>{throw new IOException("failed upstream");});
        Check(Await(controller,routes,()=>flushes++).StartsWith("SWITCH_FAILED ")&&routes.Query(query)[0]==2&&flushes==1,"Failed replacement retains the old route and does not flush or touch settings");
        controller.Begin(request,c=>q=>new byte[]{3});int attempts=0;
        Check(Await(controller,routes,()=>{if(attempts++==0)throw new IOException();}).StartsWith("SWITCH_FAILED ")&&routes.Query(query)[0]==2,"Cache-flush failure rolls back the route before reporting failure");
        long time=0;controller=new LocalDnsSwitchController(()=>time);using(var wait=new ManualResetEventSlim()){
            controller.Begin(request,c=>{wait.Wait();return q=>new byte[]{4};});time=12001;string status=null;controller.Poll(routes,()=>flushes++,line=>status=line);
            Check(status.StartsWith("SWITCH_FAILED ")&&!controller.Pending&&routes.Query(query)[0]==2,"Deadline cannot apply an unfinished candidate later");
            Check(!controller.Begin(request,c=>q=>new byte[]{6}),"Repeated timeout clicks cannot accumulate abandoned network probes");wait.Set();
        }
        controller=new LocalDnsSwitchController();
        using(var wait=new ManualResetEventSlim()){
            controller.Begin(request,c=>{wait.Wait();return q=>new byte[]{5};});controller.Cancel();wait.Set();controller.Poll(routes,()=>flushes++,line=>{throw new Exception("Late commit after stop");});
            Check(!controller.Pending&&routes.Query(query)[0]==2,"Stop discards pending work without a delayed route change");
        }
        var specialCommand=LocalDnsSwitchCommand.Parse("AGSWITCH 5 https");
        Check(specialCommand.Antigravity&&specialCommand.Wire=="AGSWITCH 5 https","Separate route has a distinct authenticated worker command");
        foreach(string bad in new[]{"AGSWITCH 0 https","AGSWITCH 5 tls"}){bool rejected=false;try{LocalDnsSwitchCommand.Parse(bad);}catch(IOException){rejected=true;}Check(rejected,"Reject unsupported Antigravity command "+bad);}
        controller=new LocalDnsSwitchController();controller.Begin(specialCommand,c=>q=>new byte[]{8});
        Check(Await(controller,routes,()=>{})=="AGSWITCHED 5 https"&&routes.Query(DnsProbe.Query(AntigravityDns.Domains[0]))[0]==8&&routes.Query(query)[0]==2,"Live Antigravity replacement changes only the two API routes and acknowledges separately");
        controller.Begin(specialCommand,c=>q=>new byte[]{7});
        Check(Await(controller,routes,()=>{throw new IOException();}).StartsWith("SWITCH_FAILED ")&&routes.Query(DnsProbe.Query(AntigravityDns.Domains[0]))[0]==8&&routes.Query(query)[0]==2,"Flush failure rolls back Antigravity without changing the common route");
        var google=new Session();var local=new Session();
        using(var form=new MainForm(google,false,local)){
            foreach(string field in new[]{"initializing","agLoading","active","busy"})Set(form,field,false);Set(form,"defaultGoal",-1);var handle=form.Handle;
            form.SelectLocalMode(true);Set(form,"dnsSet",5);form.SelectLocalAntigravity(true,6);
            typeof(MainForm).GetMethod("Toggle",Fields).Invoke(form,null);Application.DoEvents();
            Check(local.Starts==1&&Get<bool>(form,"active"),"Fake local worker begins connected");
            Check(form.SwitchLocalAntigravity(5)&&form.LocalAntigravityProvider==6&&local.Special==6,"Antigravity selection stays unchanged while the replacement is pending");
            Application.DoEvents();Check(!form.SwitchLocalAntigravity(9),"A pending Antigravity switch cannot be submitted twice");
            local.Complete(true);Application.DoEvents();Check(form.LocalAntigravityProvider==5&&Get<int>(form,"dnsSet")==5&&local.Starts==1&&local.Stops==0,"Acknowledged Antigravity switch uses the existing worker and preserves the common DNS");
            form.SwitchLocalAntigravity(6);local.Complete(false);Application.DoEvents();Check(form.LocalAntigravityProvider==5&&Get<bool>(form,"active")&&!Get<bool>(form,"busy"),"Rejected Antigravity candidate leaves the current route and connection active");
            form.SwitchLocalAntigravity(6);local.Complete(true);Application.DoEvents();
            Check(form.SwitchLocalProvider(7)&&local.Switches==1&&local.Starts==1&&local.Stops==0&&Get<int>(form,"dnsSet")==5,"Selecting a new local provider uses the existing worker and retains old selection until acknowledgement");
            Application.DoEvents();Check(!form.SwitchLocalProvider(6),"A pending live switch cannot be submitted twice");
            local.Complete(true);Application.DoEvents();Check(Get<int>(form,"dnsSet")==7&&!Get<bool>(form,"busy")&&Get<bool>(form,"active")&&local.Special==6,"Acknowledged switch updates the selected provider and preserves Antigravity");
            form.SwitchLocalProvider(6);local.Complete(false);Application.DoEvents();Check(Get<int>(form,"dnsSet")==7&&Get<bool>(form,"active")&&!Get<bool>(form,"busy"),"Failed live switch leaves the original connection selectable and active");
            form.SetProviderVisibility(new[]{7});Check(!form.UiProviders.Contains(7)&&Get<int>(form,"dnsSet")==7&&local.Stops==0,"Hiding the active provider does not disconnect or silently replace it");
            Check(!form.SwitchLocalProvider(7),"Hidden provider cannot be selected for a new live switch");
            foreach(bool dark in new[]{false,true}){
                typeof(MainForm).GetMethod("SetTheme",Fields).Invoke(form,new object[]{dark,false});
                using(var settings=form.CreateSettingsDialog()){
                    settings.StartPosition=FormStartPosition.Manual;settings.Location=new Point(-10000,-10000);settings.ShowInTaskbar=false;settings.Show();settings.SelectSettingsTab(true);Application.DoEvents();
                    var checks=Get<Dictionary<int,CheckBox>>(settings,"providerChecks");Check(checks.Count==Dns.AvailableProviders.Length&&!checks[7].Checked,"Provider tab reflects per-provider saved visibility, dark="+dark);
                    var panel=Get<Panel>(settings,"providerPanel");var footer=Get<Button>(settings,"primaryAction");Check(panel.Visible&&panel.Bottom<footer.Top&&checks.Values.All(c=>c.Width>0),"Provider list scrolls above the shared Save footer, dark="+dark);
                    foreach(var box in checks.Values)box.Checked=false;Check(checks.Values.Count(c=>c.Checked)==1,"Last visible provider cannot be unchecked, dark="+dark);
                    settings.MeasureProviders(DnsTransport.Https,(id,protocol,cancel)=>new ProviderLatency{Provider=id,Protocol=protocol,Milliseconds=20+id,MeasuredUtc=DateTime.UtcNow,Error=id==6?"fixture failure":null});
                    var refresh=Get<Button>(settings,"providerRefresh");
                    Check(SpinWait.SpinUntil(()=>{Application.DoEvents();return refresh.Enabled;},3000),"Latency measurement completes asynchronously without blocking settings, dark="+dark);
                    var pings=Get<Dictionary<int,Label>>(settings,"providerPings");Check(pings[0].Text.Contains("20 мс")&&pings[6].Text.Contains("Нет ответа"),"Per-provider latency and failures remain distinct, dark="+dark);
                    using(var image=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-provider-options-"+dark+".png"));}
                    settings.SelectSettingsTab(false);Application.DoEvents();Check(!panel.Visible&&Get<Panel>(settings,"settingsConnectionPanel").Visible,"General settings remain accessible in the same window, dark="+dark);
                    Check(Get<CheckBox>(settings,"settingsResolverCheck").Bottom<footer.Top,"General options fit above Save after adding provider tabs, dark="+dark);
                    using(var image=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-general-options-"+dark+".png"));}
                    settings.DialogResult=DialogResult.Cancel;settings.Close();Check(form.UiProviders.Contains(0)&&!form.UiProviders.Contains(7),"Closing without Save preserves the main visibility snapshot, dark="+dark);
                }
            }
        }
        Console.WriteLine("All "+passed+" provider options and live-switch checks passed; fake upstreams/workers, loopback-free, isolated test registry only.");
    }
}
