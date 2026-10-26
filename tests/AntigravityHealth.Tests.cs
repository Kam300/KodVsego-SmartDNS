using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using HbAi.XboxDns;

class AntigravityHealthTests
{
    static int passed;static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly DateTime Now=new DateTime(2026,10,26,20,0,0);
    static string host=AntigravityDns.Domains[1];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
    static string Line(DateTime at,int attempt=1,string problem="EOF")
    {return "I"+at.ToString("MMdd HH:mm:ss.ffffff")+" 123 run.go:396] Run: attempt "+attempt+" failed (request failed: Post \"https://"+host+"/v1internal:streamGenerateContent?alt=sse\": "+problem+"), retrying in 2s";}
    static AgHealthEvent Parse(string text,DateTime? time=null,string source="language_server.log")
    {string previous=host;return AgHealthParser.Parse(text,source,time??Now,ref previous);}
    static void Set(object obj,string name,object value){obj.GetType().GetField(name,Fields).SetValue(obj,value);}
    static T Get<T>(object obj,string name){return (T)obj.GetType().GetField(name,Fields).GetValue(obj);}
    sealed class Session : ITunnelSession,ILocalDnsConfiguration,ILocalDnsSwitchSession,ILocalAntigravityTransportSession
    {
        public event Action Changed;public bool Running{get;private set;}public bool Connected{get;private set;}public bool Stopping{get{return false;}}public string LastError{get;private set;}public string Statistics{get{return "";}}
        public bool Switching{get;private set;}public int CurrentProvider{get;private set;}public bool CurrentTls{get;private set;}public int CurrentAntigravity{get;private set;}
        internal int Requested=-1,Switches,Stops,CommonSwitches;bool commonSwitch;
        bool requestedTls;public bool CurrentAntigravityTls{get;private set;}
        public void SetAntigravity(int p,bool tls){SetAntigravity(p);CurrentAntigravityTls=tls;}
        public void SwitchAntigravity(int p,bool tls){requestedTls=tls;SwitchAntigravity(p);}
        public void Start(int p,bool tls){CurrentProvider=p;Connected=Running=true;}
        public void Stop(){Stops++;Connected=Running=false;}
        public void SetAntigravity(int p){CurrentAntigravity=p;}
        public void SwitchProvider(int p,bool tls){commonSwitch=true;requestedTls=tls;Requested=p;CommonSwitches++;Switching=true;if(Changed!=null)Changed();}
        public void SwitchAntigravity(int p){commonSwitch=false;Requested=p;Switches++;Switching=true;if(Changed!=null)Changed();}
        internal void Complete(bool success){if(success){if(commonSwitch){CurrentProvider=Requested;CurrentTls=requestedTls;}else{CurrentAntigravity=Requested;CurrentAntigravityTls=requestedTls;}}LastError=success?null:"fixture replacement failed";Switching=false;if(Changed!=null)Changed();}
        public void Dispose(){}
    }
    static MainForm Form(Session local)
    {
        local.Start(7,false);local.SetAntigravity(6);var form=new MainForm(new Session(),false,local);
        foreach(string name in new[]{"initializing","agLoading","busy"})Set(form,name,false);
        Set(form,"active",true);Set(form,"localMode",true);Set(form,"localAntigravity",true);Set(form,"localAntigravityProvider",6);Set(form,"dnsSet",7);Set(form,"agAutoDns",true);Set(form,"agWatch",true);Set(form,"agWatchSince",DateTime.Now.AddSeconds(-10));var handle=form.Handle;return form;
    }
    static AgHealthEvent[] Pair(){var n=DateTime.Now;return new[]{Parse(Line(n.AddSeconds(-2),1),n),Parse(Line(n.AddSeconds(-1),2),n)};}
    static string Startup(DateTime at,string method,int port)
    {return "E"+at.ToString("MMdd HH:mm:ss.ffffff")+" 110 errorreport.go:224] Post \"https://"+host+"/v1internal:"+method+"\": read tcp 192.0.2.10:"+port+"->198.51.100.20:443: wsarecv: A connection attempt failed because the connected party did not properly respond";}
    sealed class TransportProbe : IAutoDnsProbe
    {
        readonly bool tls;internal TransportProbe(bool tls){this.tls=tls;}
        public string[] Resolve(int p,bool encrypted,string host,CancellationToken c){c.ThrowIfCancellationRequested();if(!tls)throw new IOException("fixture DoH timeout");return new[]{"198.51.100.20"};}
        public void Https(string h,string a,bool api,CancellationToken c){c.ThrowIfCancellationRequested();}
        public ServiceHttpReply Request(ServiceEndpoint e,string a,CancellationToken c){throw new Exception("Unexpected service probe");}
    }
    sealed class CommonProbe : IAutoDnsProbe
    {
        internal readonly System.Collections.Concurrent.ConcurrentBag<string> Hosts=new System.Collections.Concurrent.ConcurrentBag<string>();internal bool Fail;
        public string[] Resolve(int p,bool e,string h,CancellationToken c){c.ThrowIfCancellationRequested();Hosts.Add("DNS:"+h);return new[]{"198.51.100.20"};}
        public void Https(string h,string a,bool api,CancellationToken c){c.ThrowIfCancellationRequested();Hosts.Add("HTTPS:"+h);if(Fail)throw new IOException("fixture bootstrap timeout");}
        public ServiceHttpReply Request(ServiceEndpoint e,string a,CancellationToken c){throw new Exception("Unexpected authenticated request");}
    }
    [STAThread]static void Main()
    {
        var bootstrap=new CommonProbe();var profiles=AgCommonSearch.Search(new[]{0},false,6,false,CancellationToken.None,t=>bootstrap);
        Check(profiles.Single().Ok&&bootstrap.Hosts.Contains("DNS:"+host)&&bootstrap.Hosts.Contains("HTTPS:accounts.google.com")&&bootstrap.Hosts.Contains("HTTPS:oauth2.googleapis.com")&&!bootstrap.Hosts.Any(h=>h.StartsWith("HTTPS:cloudcode")||h.StartsWith("HTTPS:daily-")),"Combination checks bootstrap HTTPS and preserved API DNS without claiming account or model availability");
        bootstrap=new CommonProbe{Fail=true};Check(!AgCommonSearch.Search(new[]{0},false,6,false,CancellationToken.None,t=>bootstrap).Single().Ok,"Unreachable bootstrap DNS cannot become a candidate primary route");
        var variants=AgFailoverSearch.Search(new[]{5,6,13},5,false,CancellationToken.None,t=>new TransportProbe(t));
        Check(variants.Any(r=>r.Provider==5&&r.Tls&&r.Ok)&&!variants.Any(r=>r.Provider==5&&!r.Tls),"Failover retries the current provider through DoT but excludes the failed current transport");
        Check(variants.Any(r=>r.Provider==6&&!r.Tls&&!r.Ok)&&variants.Any(r=>r.Provider==6&&r.Tls&&r.Ok)&&!variants.Any(r=>r.Provider==13),"A failed DoH probe cannot hide a working DoT route; Malw CF stays excluded");
        Check(new AutoDnsProbe(true,8000,true).HttpsTimeout==8000,"Antigravity relay probes allow eight seconds per HTTPS stage");
        var transport=Parse(Line(Now.AddSeconds(-2),1));Check(transport.Kind==AgHealthKind.Transport&&transport.CanSwitch,"A generation EOF is a routable error with its exact API host");
        Check(Parse(Line(Now,2,"read tcp 192.0.2.1:123->203.0.113.2:443: wsarecv: timeout")).CanSwitch,"Generation socket timeouts can request a replacement");
        Check(!Parse(Line(Now,2,"write tcp 127.0.0.1:1->127.0.0.1:53129: use of closed network connection")).CanSwitch,"A different local proxy is never blamed on our DNS");
        Check(!Parse(Line(Now),null,"main.log").CanSwitch,"Copied main-log errors cannot double-count a language-server failure");
        Check(!Parse(Line(Now).Replace(host,"unrelated.example")).CanSwitch,"An unrelated API URL cannot inherit an old Antigravity host");
        foreach(var pair in new[]{new[]{"UNAVAILABLE (code 503): No capacity available", "Capacity"},new[]{"RESOURCE_EXHAUSTED (code 429): Individual quota reached","Quota"},new[]{"UNAUTHENTICATED (code 401)","Authentication"},new[]{"INTERNAL (code 500)","Server"}}){var e=Parse(Line(Now,1,pair[0]));Check(e.Kind.ToString()==pair[1]&&!e.CanSwitch,"Do not switch for "+pair[1]);}
        Check(Parse("I1026 20:00:00.000000 Model unavailable, retrying in 2s").Kind==AgHealthKind.Other,"Generic model-unavailable text alone cannot change DNS");
        Check(Parse("ERROR: logging before google.Init: I1026 20:00:00.000000 Normal startup") == null,"Google logging boilerplate is not an application error");
        Check(Parse("E1026 20:00:00.000000 Something else failed").Kind==AgHealthKind.Other,"Other application errors are still imported into the journal");
        foreach(string method in new[]{"loadCodeAssist","fetchAvailableModels","listExperiments"})Check(Parse(Startup(Now,method,60081)).CanSwitch,"Startup API timeout is routable before a model request: "+method);
        var startupPolicy=new AgFailoverPolicy();Check(!startupPolicy.Observe(Parse(Startup(Now.AddSeconds(-20),"loadCodeAssist",60081)),5,Now),"One startup timeout is not enough");
        Check(!startupPolicy.Observe(Parse(Startup(Now.AddSeconds(-20).AddMilliseconds(2),"loadCodeAssist",60081)),5,Now),"Cache and error-report wrappers around the same socket count once");
        Check(startupPolicy.Observe(Parse(Startup(Now.AddSeconds(-1),"loadCodeAssist",49852)),5,Now),"The repeated startup timeout from the report starts failover");
        Check(!Parse(Startup(Now,"loadCodeAssist",60081).Replace(host,"accounts.example")).CanSwitch,"Startup errors from unrelated APIs remain diagnostic only");
        Check(Parse("[2026-10-26 20:00:00.000] [error] Failed to load URL: https://127.0.0.1:61881/ with error: ERR_TIMED_OUT",Now,"main.log").Kind==AgHealthKind.Other,"A local black-window timeout does not by itself blame an external DNS");
        var region=Parse(Line(Now,1,"FAILED_PRECONDITION (code 400): User location is not supported"));Check(region.Kind==AgHealthKind.Region&&region.CanSwitch,"A precise region refusal can use another declared route");
        string sensitive="E1026 20:00:00.000000 failed https://example.com/private/chat/123?token=url-secret Trace: trace-secret api_key=key-secret prompt: private-user-text";
        string clean=AgHealthParser.SafeDetail(sensitive);Check(!clean.Contains("url-secret")&&!clean.Contains("trace-secret")&&!clean.Contains("key-secret")&&!clean.Contains("private-user-text")&&!clean.Contains("chat/123"),"Imported errors omit URLs with identifiers, credentials, traces and prompt payloads");
        Check(!AgHealthParser.SafeDetail("Authorization: Basic encoded-secret").Contains("encoded-secret")&&!AgHealthParser.SafeDetail("Cookie: session=private-cookie").Contains("private-cookie"),"Basic authorization and cookies cannot enter exported logs");
        string permission=AgHealthParser.SafeDetail("W1026 20:00:00.000 permission_grant_store.go:478] ignoring invalid allow entry command(private-project-code): invalid grant string");Check(!permission.Contains("private-project-code")&&permission.Contains("команды скрыт"),"Permission-store warnings retain the error but exclude embedded commands");
        Check(!Parse("W1026 20:00:00.000000 permission_grant_store.go:478] ignoring invalid allow entry command("+Startup(Now,"loadCodeAssist",60081)+")").CanSwitch,"Quoted commands inside permission warnings cannot trigger a route change");
        Check(!AgHealthParser.SafeDetail(@"failed C:\Users\PrivateUser\data and 11111111-2222-3333-4444-555555555555").Contains("PrivateUser")&&!AgHealthParser.SafeDetail(@"failed C:\\Users\\PrivateUser\\data").Contains("PrivateUser"),"Normal and escaped profile paths and conversation IDs are redacted");
        var policy=new AgFailoverPolicy();Check(!policy.Observe(transport,6,Now),"One transport failure does not switch");Check(!policy.Observe(transport,6,Now),"Duplicate retry lines do not switch");
        Check(policy.Observe(Parse(Line(Now.AddSeconds(-1),2)),6,Now),"Two distinct recent retries start a search");
        Check(!policy.Candidates(new[]{4,5,6,13},6,Now).Contains(6)&&!policy.Candidates(new[]{4,5,6,13},6,Now).Contains(13),"Failed current route and Malw CF are excluded");
        Check(!policy.Observe(Parse(Line(Now.AddSeconds(1),3)),5,Now.AddSeconds(1))&&!policy.Observe(Parse(Line(Now.AddSeconds(2),4)),5,Now.AddSeconds(2)),"Cooldown limits repeated searches");
        var revisit=new AgFailoverPolicy();revisit.Observe(Parse(Startup(Now.AddSeconds(-20),"loadCodeAssist",60080)),5,Now);revisit.Observe(Parse(Startup(Now.AddSeconds(-1),"loadCodeAssist",60081)),5,Now);
        Check(!revisit.CanProbe(5,false,Now)&&revisit.CanProbe(5,true,Now),"A DNS-AI DoH failure does not ban its independent DoT transport");
        Check(revisit.Candidates(new[]{4,5,6,9,13},6,Now.AddSeconds(69)).Contains(5)&&revisit.CanProbe(5,false,Now.AddSeconds(69)),"Report replay: after MagicDNS times out, DNS-AI DoH is eligible for the next search instead of a ten-minute ban");
        revisit.Failed(5,Now);Check(!revisit.CanProbe(5,false,Now.AddSeconds(29))&&revisit.CanProbe(5,false,Now.AddSeconds(31))&&revisit.CanProbe(5,true,Now),"Failed preflight backoff is brief and specific to its tested transport");
        var known=new AutoDnsResult{Provider=5,Encrypted=true,Milliseconds=900};var fast=new AutoDnsResult{Provider=6,Encrypted=true,Tls=true,Milliseconds=100};
        revisit.Answer(5);Check(revisit.Best(new[]{fast,known})==known,"A freshly rechecked route that delivered a client answer outranks a faster preflight-only candidate");
        known.Error="fixture relay unavailable";Check(revisit.Best(new[]{fast,known})==fast,"Previous client success cannot override a failed current preflight");
        policy=new AgFailoverPolicy();policy.Observe(transport,6,Now);policy.Observe(Parse(Line(Now,2,"UNAVAILABLE (code 503)")),6,Now);Check(!policy.Observe(Parse(Line(Now,3)),6,Now),"A non-routing server response resets the failure streak");
        policy=new AgFailoverPolicy();for(int n=0;n<4;n++){var at=Now.AddMinutes(2*n);policy.Observe(Parse(Line(at.AddSeconds(-2),1),at),6,at);Check(policy.Observe(Parse(Line(at.AddSeconds(-1),2),at),6,at)==(n<3),"At most three failover searches in ten minutes: "+n);}
        policy=new AgFailoverPolicy();policy.Observe(region,6,Now);Check(!policy.Observe(Parse(Line(Now.AddSeconds(1),2,"User location is not supported"),Now.AddSeconds(1)),6,Now.AddSeconds(1)),"Repeated reports of one region refusal do not count as two requests");
        Check(policy.Observe(Parse(Line(Now.AddSeconds(6),3,"User location is not supported"),Now.AddSeconds(6)),6,Now.AddSeconds(6)),"A later region refusal can trigger bounded failover");
        string folder=Path.Combine(Path.GetTempPath(),"SmartDNS-health-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string log=Path.Combine(folder,"language_server.log");File.WriteAllText(log,Line(Now)+"\n");var tail=new AgLogTail(log);Check(tail.Read(Now).Length==0,"Existing log history is not replayed");
        string next=Line(Now,2);File.AppendAllText(log,next.Substring(0,20));Check(tail.Read(Now).Length==0,"Partial lines wait for completion");File.AppendAllText(log,next.Substring(20)+"\n");Check(tail.Read(Now).Length==1&&tail.Read(Now).Length==0,"Each appended error is read exactly once");
        File.WriteAllText(log,"E1026 20:00:00.000000 error\n");Check(tail.Read(Now).Single().Kind==AgHealthKind.Other,"Truncation starts reading the new log safely");
        var unicode=System.Text.Encoding.UTF8.GetBytes("E1026 20:00:00.000000 ошибка\n");using(var file=new FileStream(log,FileMode.Append,FileAccess.Write,FileShare.ReadWrite)){file.Write(unicode,0,unicode.Length-2);}Check(tail.Read(Now).Length==0,"Incomplete UTF-8 log lines are retained as bytes");using(var file=new FileStream(log,FileMode.Append,FileAccess.Write,FileShare.ReadWrite)){file.Write(unicode,unicode.Length-2,2);}Check(tail.Read(Now).Single().Detail.Contains("ошибка"),"UTF-8 characters split across reads stay intact");
        using(var monitor=new AgLogMonitor(folder)){
            File.AppendAllText(log,"E"+DateTime.Now.ToString("MMdd HH:mm:ss.ffffff")+" journal-fixture-failure token=private-journal-token\n");int received=0;monitor.Poll(items=>Interlocked.Exchange(ref received,items.Length));
            Check(SpinWait.SpinUntil(()=>Volatile.Read(ref received)==1,3000),"Log import works independently of the disabled auto-switch setting");string journal=DiagnosticReport.Recent(200000);Check(journal.Contains("journal-fixture-failure")&&!journal.Contains("private-journal-token"),"Imported client errors appear in the program journal with secrets removed");
            string exported=DiagnosticReport.Export("health fixture",()=>"safe fixture snapshot");Check(exported.Contains("journal-fixture-failure")&&!exported.Contains("private-journal-token"),"Downloaded program report includes imported errors and no raw token");
            var at=DateTime.Now;File.AppendAllText(log,"I"+at.ToString("MMdd HH:mm:ss.ffffff")+" URL: https://"+host+"/v1internal:streamGenerateContent ResponseID: private-response-id\n");monitor.Collect();Check(monitor.StopEventAfter(at.AddSeconds(-1)).Kind==AgHealthKind.Answer&&monitor.StopEventAfter(at.AddSeconds(1))==null,"Final pre-switch observation sees a fresh answer even before UI polling delivers it");
        }
        string keyName=@"Software\KodVsego\Tests\AgAuto-"+Guid.NewGuid().ToString("N");try{using(var key=Registry.CurrentUser.CreateSubKey(keyName)){Check(!AgFailoverPreference.Read(key),"Automatic DNS switching is off by default");key.SetValue(AgFailoverPreference.Value,1);Check(AgFailoverPreference.Read(key),"Opt-in survives a preference round trip");key.SetValue(AgFailoverPreference.Value,0);Check(!AgFailoverPreference.Read(key),"Opt-out is honored");}}finally{Registry.CurrentUser.DeleteSubKey(keyName,false);}
        var local=new Session();using(var form=Form(local)){
            Check(form.ShouldOfferLocalAntigravityRestart(true)&&!form.ShouldOfferLocalAntigravityRestart(true,true),"Automatic DNS changes suppress restart dialogs; explicit manual changes can still offer restart");
            form.AgFailoverProbe=(ids,cancel)=>{Check(ids.Contains(5)&&!ids.Contains(6)&&!ids.Contains(13),"Search respects eligible alternate providers");return new[]{new AutoDnsResult{Provider=5,Encrypted=true,Milliseconds=10}};};form.ReceiveAgHealth(Pair());
            Check(SpinWait.SpinUntil(()=>{Application.DoEvents();return local.Switches==1;},3000),"Two real-log-style failures reach the authenticated separate-route switch");
            Check(form.LocalAntigravityProvider==6&&local.CurrentProvider==7&&local.Stops==0,"Selection waits for acknowledgement; common DNS stays active");
            local.Complete(true);Application.DoEvents();Check(form.LocalAntigravityProvider==5&&local.CurrentProvider==7,"Acknowledgement commits only Antigravity");
            Check(DiagnosticReport.Recent(20000).Contains("Авто: DNS применён без диалога и без перезапуска клиента"),"Automatic worker acknowledgement records silent application without restarting the client");
        }
        local=new Session();using(var form=Form(local)){
            form.AgFailoverProbe=(ids,cancel)=>new[]{new AutoDnsResult{Provider=5,Encrypted=true,Tls=true}};form.ReceiveAgHealth(Pair());
            Check(SpinWait.SpinUntil(()=>{Application.DoEvents();return local.Switches==1;},3000),"A healthy DoT candidate reaches the protocol-aware worker");
            Check(!Get<bool>(form,"localAntigravityTls")&&local.CurrentProvider==7,"Protocol is not committed before worker acknowledgement");
            local.Complete(true);Application.DoEvents();Check(form.LocalAntigravityProvider==5&&Get<bool>(form,"localAntigravityTls")&&local.CurrentAntigravityTls&&local.CurrentProvider==7,"Worker ACK commits the tested DoT transport without touching common DNS");
        }
        local=new Session();using(var form=Form(local))using(var waiting=new ManualResetEventSlim()){
            form.AgFailoverProbe=(ids,cancel)=>{waiting.Wait();return new[]{new AutoDnsResult{Provider=5,Encrypted=true}};};form.ReceiveAgHealth(Pair());
            Set(form,"agAutoDns",false);waiting.Set();Check(SpinWait.SpinUntil(()=>{Application.DoEvents();return Get<CancellationTokenSource>(form,"agSearch")==null;},3000)&&local.Switches==0,"Disabling the setting discards a pending replacement");
        }
        local=new Session();using(var form=Form(local))using(var waiting=new ManualResetEventSlim()){
            form.AgFailoverProbe=(ids,cancel)=>{waiting.Wait();return new[]{new AutoDnsResult{Provider=5,Encrypted=true}};};form.ReceiveAgHealth(Pair());
            var n=DateTime.Now;form.ReceiveAgHealth(new[]{Parse("I"+n.ToString("MMdd HH:mm:ss.ffffff")+" URL: https://"+host+"/v1internal:streamGenerateContent ResponseID: private-response",n)});waiting.Set();
            Application.DoEvents();Check(Get<CancellationTokenSource>(form,"agSearch")==null&&local.Switches==0,"A real answer marker cancels a pending replacement before it can switch");
        }
        local=new Session();using(var form=Form(local)){
            form.AgFailoverProbe=(ids,cancel)=>new[]{new AutoDnsResult{Provider=5,Encrypted=true,Error="fixture timeout"}};form.ReceiveAgHealth(Pair());
            Check(SpinWait.SpinUntil(()=>{Application.DoEvents();return Get<CancellationTokenSource>(form,"agSearch")==null;},3000)&&local.Switches==0&&form.LocalAntigravityProvider==6,"No healthy candidate leaves the old route intact");
            Set(form,"agAutoDns",false);using(var settings=form.CreateSettingsDialog()){Check(!settings.AntigravityAutoDnsEnabled,"Disabled state is displayed in the settings draft");settings.SetAntigravityAutoDns(true);Check(settings.AntigravityAutoDnsEnabled,"Automatic switching has a separate settings checkbox");}
        }
        local=new Session();using(var form=Form(local)){
            form.AgFailoverProbe=(ids,cancel)=>new AutoDnsResult[0];form.AgCommonProbe=(ids,cancel)=>{Check(ids.Contains(0)&&!ids.Contains(7),"Full-profile fallback checks another visible primary DNS");return new[]{new AutoDnsResult{Provider=0,Encrypted=true}};};
            form.ReceiveAgHealth(Pair());Check(SpinWait.SpinUntil(()=>{Application.DoEvents();return local.CommonSwitches==1;},3000),"Report reproduction: no alternate API route starts a primary-DNS trial");
            Check(local.CurrentProvider==7&&local.CurrentAntigravity==6&&form.LocalAntigravityProvider==6,"Old primary and separate route remain live before the ACK");local.Complete(true);Application.DoEvents();
            Check(local.CurrentProvider==0&&local.CurrentAntigravity==6&&Get<int>(form,"dnsSet")==0&&Get<string>(form,"agAutoStatus").Contains("ожидаем ответ клиента"),"ACK applies Comss plus preserved MagicDNS and waits for actual client evidence");
        }
        local=new Session();using(var form=Form(local)){
            Check(form.TryAgCommon(new AutoDnsResult{Provider=0,Encrypted=true},6,false),"A prepared combination can request the existing authenticated worker");local.Complete(false);Application.DoEvents();
            Check(local.CurrentProvider==7&&local.CurrentAntigravity==6&&Get<string>(form,"agAutoStatus").Contains("не применена"),"Failed primary ACK keeps both old routes");
        }
        local=new Session();using(var form=Form(local))using(var waiting=new ManualResetEventSlim()){
            form.AgFailoverProbe=(ids,cancel)=>new AutoDnsResult[0];form.AgCommonProbe=(ids,cancel)=>{waiting.Wait();return new[]{new AutoDnsResult{Provider=0,Encrypted=true}};};form.ReceiveAgHealth(Pair());Set(form,"agAutoDns",false);waiting.Set();
            Check(SpinWait.SpinUntil(()=>{Application.DoEvents();return Get<CancellationTokenSource>(form,"agSearch")==null;},3000)&&local.CommonSwitches==0,"Opt-out discards a full-profile result before changing the primary DNS");
        }
        Console.WriteLine("All "+passed+" Antigravity health checks passed; fake probes/workers, temporary logs and isolated registry only.");
    }
}
