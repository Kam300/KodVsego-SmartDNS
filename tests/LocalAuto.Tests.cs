using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HbAi.XboxDns;

static class LocalAutoTests
{
    static int passed;
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);passed++;Console.WriteLine("PASS "+text);}
    static void Set(MainForm form,string name,object value){typeof(MainForm).GetField(name,Fields).SetValue(form,value);}
    static T Get<T>(MainForm form,string name){var field=typeof(MainForm).GetField(name,Fields);return (T)(field!=null?field.GetValue(form):typeof(MainForm).GetProperty(name,Fields).GetValue(form,null));}
    sealed class Session : ITunnelSession,ILocalDnsConfiguration
    {
        public bool Running{get;private set;}public bool Connected{get;private set;}public bool Stopping{get;private set;}
        public string LastError{get;private set;}public string Statistics{get{return "0 0";}}
        public event Action Changed;internal int Starts,Stops,Provider,Antigravity=-1;internal bool Tls,CancelStart;
        void Notify(){if(Changed!=null)Changed();}
        public void Start(int provider,bool tls){Starts++;Provider=provider;Tls=tls;if(CancelStart)throw new System.ComponentModel.Win32Exception(1223);Running=true;Notify();}
        public void Stop(){if(!Running)return;Stops++;Stopping=true;Notify();}
        public void SetAntigravity(int provider){Antigravity=provider;}
        internal void Connect(){Connected=true;Notify();}
        internal void Finish(){Running=Connected=Stopping=false;Notify();}
        public void Dispose(){}
    }
    sealed class Probe : IAutoDnsProbe
    {
        internal int Winner=6;internal bool FailAll;internal int PlainQueries;
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel){cancel.ThrowIfCancellationRequested();if(!encrypted)Interlocked.Increment(ref PlainQueries);if(FailAll||provider!=Winner)throw new IOException("simulated failure");return new[]{"93.184.215.14"};}
        public void Https(string host,string address,bool api,CancellationToken cancel){cancel.ThrowIfCancellationRequested();}
        public ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();var reply=new ServiceHttpReply{Status=endpoint.Api?401:200,Body=endpoint.Api?"{\"error\":{\"message\":\"Missing authentication\"}}":null};reply.Headers["Content-Type"]=endpoint.Api?"application/json":"text/html";return reply;
        }
    }
    static void Idle(MainForm form){foreach(string field in new[]{"initializing","agLoading","active","busy"})Set(form,field,false);Set(form,"defaultGoal",-1);var handle=form.Handle;}
    [STAThread]static void Main()
    {
        int goal=AutoDns.GoalIndex("ChatGPT");var probe=new Probe();
        var results=AutoDns.Search(goal,true,probe,CancellationToken.None);var best=AutoDns.Best(results);
        Check(best!=null&&best.Provider==6&&best.Encrypted&&!best.Tls&&probe.PlainQueries==0,"Local-compatible search finds a successful encrypted candidate without plaintext queries");
        var google=new Session();var local=new Session();
        using(var form=new MainForm(google,false,local)){
            Idle(form);Set(form,"defaultGoal",goal);form.SelectLocalMode(true);
            var segments=Get<System.Drawing.Rectangle[]>(form,"rSeg");
            Check(!segments[Dns.Name.Length].IsEmpty&&segments[Dns.Name.Length].Left==58,"Application mode offers Auto in the first provider slot");
            Check(Get<int>(form,"SelectedSegment")==Dns.Name.Length&&form.ConnectionLabel=="Авто · ChatGPT"&&local.Starts==0,"Saved default service selects local Auto without starting on mode selection");
            typeof(MainForm).GetMethod("PickSet",Fields).Invoke(form,new object[]{5});
            Check(Get<int>(form,"SelectedSegment")==5&&Get<int>(form,"defaultGoal")==goal,"Manual local provider overrides Auto in the session and preserves the default service");
            form.SelectLocalAntigravity(true,9);int forms=Application.OpenForms.Count;
            Check(!form.StartLocalAuto(new AutoDnsResult{Provider=7,Encrypted=true},AutoDns.GoalIndex("Antigravity")),"Antigravity Auto cannot use a provider without a supported separate route");
            Check(!form.StartLocalAuto(null,goal)&&!form.StartLocalAuto(new AutoDnsResult{Provider=6,Encrypted=false},goal)&&!form.StartLocalAuto(new AutoDnsResult{Provider=6,Encrypted=true,Error="failed"},goal),"Missing, plaintext and failed results cannot start local DNS");
            Check(!form.StartLocalAuto(new AutoDnsResult{Provider=1,Encrypted=true},goal)&&!form.StartLocalAuto(best,-1)&&!form.StartLocalAuto(best,AutoDns.Goals.Length),"Disabled providers and unknown goals cannot activate from Auto");
            Check(local.Starts==0&&google.Starts==0,"Rejected results never reach either worker");
            Check(form.StartLocalAuto(best,goal)&&local.Starts==1&&google.Starts==0&&local.Provider==6&&!local.Tls,"Auto starts the selected provider through the local worker, never the Google tunnel");
            Application.DoEvents();Check(!Get<bool>(form,"active")&&Get<bool>(form,"busy"),"Starting Auto does not claim connection before worker confirmation");
            Check(local.Antigravity==9&&form.LocalAntigravityProvider==9&&form.LocalAntigravity,"Auto preserves the separate Antigravity provider");
            Check(Application.OpenForms.Count==forms&&Get<int>(form,"SelectedSegment")==Dns.Name.Length,"Applying the Auto choice keeps the main Auto tile selected without another window");
            Check(!form.StartLocalAuto(best,goal)&&local.Starts==1,"An outstanding worker start cannot be repeated");
            local.Connect();Application.DoEvents();
            Check(Get<bool>(form,"active")&&form.ConnectionLabel=="Авто · ChatGPT","Confirmed local Auto shows the selected target on the main screen");
            typeof(MainForm).GetMethod("ShowAutoDns",Fields).Invoke(form,new object[]{false});
            Check(local.Starts==1&&Application.OpenForms.Count==forms&&!form.StartLocalAuto(best,goal),"Running local Auto cannot launch a competing search or worker");
            typeof(MainForm).GetMethod("Toggle",Fields).Invoke(form,null);Application.DoEvents();
            Check(local.Stops==1&&local.Stopping&&form.ConnectionLabel.Contains("Восстанавливаем"),"Power stops Auto through the normal local restoration path");
            local.Finish();Application.DoEvents();
            Check(!Get<bool>(form,"active")&&!Get<bool>(form,"busy")&&local.Starts==1&&Get<int>(form,"autoGoal")==goal,"Stopping retains the selected Auto target and never reconnects automatically");
            probe=new Probe{Winner=9};best=AutoDns.Best(AutoDns.Search(goal,true,probe,CancellationToken.None,null,null,true));
            Check(best!=null&&best.Tls&&form.StartLocalAuto(best,goal)&&local.Tls&&local.Provider==9,"TLS Auto uses the selected TLS provider through the same local worker");
            local.Finish();Application.DoEvents();
            form.SelectGoogleMode(true);Check(!form.StartLocalAuto(best,goal),"Local Auto cannot apply after switching connection methods");
        }
        google=new Session();local=new Session{CancelStart=true};
        using(var form=new MainForm(google,false,local)){
            Idle(form);form.SelectLocalMode(true);form.StartLocalAuto(best,goal);Application.DoEvents();
            Check(local.Starts==1&&!local.Running&&!Get<bool>(form,"active")&&!Get<bool>(form,"busy")&&!string.IsNullOrEmpty(Get<string>(form,"error")),"Cancelled elevation leaves local Auto disconnected with a retryable error");
        }
        probe=new Probe{FailAll=true};Check(AutoDns.Best(AutoDns.Search(goal,true,probe,CancellationToken.None))==null&&probe.PlainQueries==0,"Failed encrypted candidates yield no winner and never fall back to plaintext");
        Console.WriteLine("All "+passed+" local Auto checks passed; fake HTTP probes and workers only, no DNS or registry changes.");
    }
}
