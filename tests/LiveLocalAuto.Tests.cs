using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HbAi.XboxDns;

static class LiveLocalAutoTests
{
    static int passed;static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Set(MainForm form,string name,object value){typeof(MainForm).GetField(name,Fields).SetValue(form,value);}
    static T Get<T>(MainForm form,string name){return (T)typeof(MainForm).GetField(name,Fields).GetValue(form);}
    sealed class Session : ITunnelSession,ILocalDnsSwitchSession,ILocalDnsConfiguration
    {
        public bool Running{get;private set;}public bool Connected{get;private set;}public bool Stopping{get;private set;}
        public string LastError{get;private set;}public string Statistics{get{return "0 0";}}
        public bool Switching{get;private set;}public int CurrentProvider{get;private set;}public bool CurrentTls{get;private set;}
        public event Action Changed;internal int Starts,Stops,Switches,Requested,Special=-1;internal bool RequestedTls,ThrowSwitch;
        void Notify(){if(Changed!=null)Changed();}
        public void Start(int provider,bool tls){Starts++;CurrentProvider=provider;CurrentTls=tls;Running=Connected=true;Notify();}
        public void Stop(){Stops++;Running=Connected=Switching=false;Notify();}
        public void SwitchProvider(int provider,bool tls){Switches++;if(ThrowSwitch)throw new IOException("fixture control failure");Requested=provider;RequestedTls=tls;Switching=true;LastError=null;Notify();}
        internal void Complete(bool success){if(success){CurrentProvider=Requested;CurrentTls=RequestedTls;}LastError=success?null:"fixture failed candidate";Switching=false;Notify();}
        public void SetAntigravity(int provider){Special=provider;}public void Dispose(){}
    }
    static MainForm Connected(Session local)
    {
        var form=new MainForm(new Session(),false,local);foreach(string field in new[]{"initializing","agLoading","busy","active"})Set(form,field,false);Set(form,"defaultGoal",-1);var handle=form.Handle;
        form.SelectLocalMode(true);Set(form,"dnsSet",5);form.SelectLocalAntigravity(true,6);
        typeof(MainForm).GetMethod("Toggle",Fields).Invoke(form,null);Application.DoEvents();return form;
    }
    static AutoDnsResult Candidate(int provider){return new AutoDnsResult{Provider=provider,Encrypted=true};}
    static bool Apply(MainForm form,int provider,int goal)
    {return form.SwitchLocalAuto(Candidate(provider),goal,Get<int>(form,"resolverRevision"),Get<int>(form,"dnsSet"),DnsTransport.Https);}
    [STAThread]static void Main()
    {
        int gemini=AutoDns.GoalIndex("Gemini"),chatgpt=AutoDns.GoalIndex("ChatGPT");var local=new Session();
        using(var form=Connected(local)){
            Check(form.CanOpenAutoDns&&Get<bool>(form,"active")&&local.Starts==1,"Auto can open while the application DNS worker is connected");
            Check(form.LiveAutoContextCurrent(Get<int>(form,"resolverRevision"),5,DnsTransport.Https),"Search captures the currently active provider and protocol");
            int forms=Application.OpenForms.Count;
            Check(Apply(form,6,gemini)&&local.Starts==1&&local.Stops==0&&local.Switches==1&&local.Requested==6,"Chosen Auto candidate uses the running worker without stopping or relaunching it");
            Check(Get<int>(form,"autoGoal")==-1&&Get<int>(form,"dnsSet")==5&&Get<bool>(form,"active"),"Old provider and manual selection remain until the switch is acknowledged");
            Check(!form.CanOpenAutoDns&&!Apply(form,7,gemini),"Pending Auto application cannot trigger a second search or switch");
            local.Complete(true);Application.DoEvents();
            Check(Get<int>(form,"dnsSet")==6&&Get<int>(form,"autoGoal")==gemini&&!Get<bool>(form,"manualSelection")&&!Get<bool>(form,"busy")&&form.ConnectionLabel=="Авто · Gemini","Worker acknowledgement commits the provider and Auto goal together");
            Check(form.LocalAntigravity&&form.LocalAntigravityProvider==6&&local.Special==6&&Application.OpenForms.Count==forms,"Live Auto preserves the separately selected Antigravity route and adds no worker window");
            Check(Apply(form,6,chatgpt)&&local.Switches==2&&Get<int>(form,"autoGoal")==gemini,"Even an unchanged provider waits for worker confirmation before changing Auto target");
            local.Complete(true);Application.DoEvents();Check(Get<int>(form,"autoGoal")==chatgpt&&local.Starts==1,"Confirmed same-provider choice updates only the Auto target");
            Check(Apply(form,7,gemini),"A running Auto connection can select another candidate");local.Complete(false);Application.DoEvents();
            Check(Get<int>(form,"dnsSet")==6&&Get<int>(form,"autoGoal")==chatgpt&&!Get<bool>(form,"manualSelection")&&Get<bool>(form,"active"),"Failed candidate restores the old Auto target and retains the working provider");
            typeof(MainForm).GetMethod("PickSet",Fields).Invoke(form,new object[]{6});Check(Get<int>(form,"autoGoal")==-1&&Get<bool>(form,"manualSelection"),"Selecting the active provider explicitly returns to manual mode");
            Check(Apply(form,7,gemini),"Manual local DNS can enter Auto without disabling DNS");local.Complete(false);Application.DoEvents();
            Check(Get<int>(form,"dnsSet")==6&&Get<int>(form,"autoGoal")==-1&&Get<bool>(form,"manualSelection")&&!Get<bool>(form,"busy"),"Failed transition into Auto restores the previous manual mode");
            int revision=Get<int>(form,"resolverRevision"),switches=local.Switches;
            Check(!form.SwitchLocalAuto(Candidate(7),gemini,revision-1,6,DnsTransport.Https),"Stale search result cannot apply after the connection revision changes");
            Check(!form.SwitchLocalAuto(Candidate(7),gemini,revision,5,DnsTransport.Https),"Search from a different active provider cannot apply");
            Check(!form.SwitchLocalAuto(Candidate(7),gemini,revision,6,DnsTransport.Tls),"Search from a different protocol cannot apply");
            Check(!form.SwitchLocalAuto(new AutoDnsResult{Provider=7,Encrypted=false},gemini,revision,6,DnsTransport.Https)&&!form.SwitchLocalAuto(new AutoDnsResult{Provider=7,Encrypted=true,Error="failed"},gemini,revision,6,DnsTransport.Https),"Plaintext and failed candidates cannot activate through live Auto");
            Check(!Apply(form,3,gemini)&&!Apply(form,7,-1)&&!Apply(form,7,AutoDns.Goals.Length),"Disabled providers and invalid goals cannot activate");
            form.SetProviderVisibility(new[]{7});Check(!Apply(form,7,gemini),"Hidden providers remain excluded from live Auto");form.SetProviderVisibility(new int[0]);
            Check(!Apply(form,6,AutoDns.GoalIndex("Antigravity")),"Live Auto cannot substitute the main route for pinned Antigravity");
            Check(local.Switches==switches&&local.Starts==1&&local.Stops==0,"Rejected candidates do not reach the worker");
            Check(Apply(form,7,gemini),"A valid pending Auto request can be cancelled by stopping DNS");local.Stop();Application.DoEvents();
            Check(!Get<bool>(form,"active")&&Get<int>(form,"autoGoal")==-1&&Get<bool>(form,"manualSelection")&&local.Starts==1,"Stopping discards the pending Auto selection and never reconnects automatically");
            local.Complete(true);Application.DoEvents();Check(Get<int>(form,"autoGoal")==-1&&!Get<bool>(form,"active")&&!Apply(form,7,gemini),"Late acknowledgement cannot revive a stopped Auto transition");
        }
        local=new Session{ThrowSwitch=true};
        using(var form=Connected(local)){
            Set(form,"autoGoal",chatgpt);Set(form,"manualSelection",false);
            Check(!Apply(form,6,gemini)&&Get<bool>(form,"active")&&!Get<bool>(form,"busy")&&Get<int>(form,"autoGoal")==chatgpt&&!Get<bool>(form,"manualSelection")&&Get<int>(form,"dnsSet")==5,"Control-write failure preserves the previous Auto goal and active provider");
            Check(local.Starts==1&&local.Stops==0&&Get<string>(form,"error").Contains("fixture control failure"),"Failed control write is retryable without stopping the old worker");
        }
        Console.WriteLine("All "+passed+" live local Auto checks passed; fake results and workers only, no real DNS, registry changes or elevation.");
    }
}
