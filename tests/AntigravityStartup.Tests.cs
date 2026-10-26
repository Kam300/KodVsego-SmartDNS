using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;
using HbAi.XboxDns;

static class AntigravityStartupTests
{
    sealed class Session : ITunnelSession
    {
        public bool Running{get{return false;}}public bool Connected{get{return false;}}public bool Stopping{get{return false;}}public string LastError{get{return null;}}public string Statistics{get{return "0 0";}}
        public event Action Changed{add{}remove{}}public void Start(int provider,bool tls){throw new Exception("Unexpected launch");}public void Stop(){}public void Dispose(){}
    }
    static int checks;
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    static StartupFrame Failed(string url="https://127.0.0.1:54321/"){return new StartupFrame{Url="chrome-error://chromewebdata/",Unreachable=url};}
    [STAThread]static void Main(string[] args)
    {
        if(args.Length==1&&args[0]=="--watch-live"){
            using(var watcher=new AntigravityStartupRecovery()){
                var timer=System.Diagnostics.Stopwatch.StartNew();string previous=null;bool failed=false;
                while(timer.ElapsedMilliseconds<180000){watcher.Tick(true);Thread.Sleep(1000);var state=watcher.Observation;
                    if(state!=previous){Console.WriteLine("t="+timer.ElapsedMilliseconds+"; "+state);previous=state;}
                    if(state=="Окно с ошибкой загрузки")failed=true;
                    if(failed&&state=="Страница загружена"){Console.WriteLine("LIVE RECOVERED");break;}
                }
                Console.WriteLine(DiagnosticReport.Recent(3000));
            }return;
        }
        if(args.Length==2&&args[0]=="--backend-readonly"){
            string image=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","antigravity","resources","bin","language_server.exe");
            Console.WriteLine("Verified local backend readiness="+AntigravityStartupRecovery.BackendReady(new Uri(args[1]),image,System.Diagnostics.Process.GetCurrentProcess().SessionId,Console.WriteLine));return;
        }
        if(args.Length==1&&args[0]=="--live-readonly"){
            using(var watcher=new AntigravityStartupRecovery()){watcher.Tick(true);Thread.Sleep(5500);Console.WriteLine("Live page: "+watcher.Observation);Console.WriteLine(DiagnosticReport.Recent(1500));}
            Console.WriteLine("Read-only live check completed; working documents must not be reloaded.");return;
        }
        foreach(string address in new[]{"https://remote.example/","http://127.0.0.1:4567/","https://localhost:4567/","https://127.0.0.1.evil.test/","https://name:password@127.0.0.1/","file:///C:/test"})Check(Failed(address).LocalPage(null)==null,"Reject untrusted or unsupported page destination");
        Check(Failed().LocalPage(null)!=null,"Only explicit local HTTPS pages can be recovered");
        Check(new StartupFrame{Url="https://127.0.0.1:54321/"}.LocalPage(null)==null,"An ordinary working local page is not an error");
        var policy=new StartupRecoveryPolicy();int reads=0,probes=0,reloads=0;bool cancel=false,ready=false;
        Func<StartupFrame> read=()=>{reads++;return Failed();};Func<Uri,bool> probe=uri=>{probes++;return ready;};Action reload=()=>reloads++;
        Check(!policy.Visit("A",0,read,probe,reload,null,()=>cancel)&&probes==0,"Allow a settling interval before contacting the backend");
        Check(!policy.Visit("A",5000,read,probe,reload,null,()=>cancel)&&reloads==0,"A listening but unready backend cannot trigger reload");
        ready=true;
        Check(!policy.Visit("A",10000,read,probe,reload,null,()=>cancel),"Require the first successful local response");
        Check(policy.Visit("A",15000,read,probe,reload,null,()=>cancel)&&reloads==1,"Recover a failed window after two successful backend checks");
        Check(!policy.Visit("A",20000,read,probe,reload,null,()=>cancel)&&reloads==1,"A cooldown prevents repeated immediate reloads");
        Check(!policy.Visit("A",35000,read,probe,reload,null,()=>cancel)&&policy.Visit("A",40000,read,probe,reload,null,()=>cancel)&&reloads==2,"One bounded retry remains if the application fails loading again");
        Check(!policy.Visit("A",100000,read,probe,reload,null,()=>cancel)&&reloads==2,"A persistently broken page cannot cause an endless reload loop");
        int before=reads;cancel=true;
        Check(!policy.Visit("B",0,read,probe,reload,null,()=>cancel)&&reads==before,"Disabling or disposing stops work before reading the page");cancel=false;
        policy=new StartupRecoveryPolicy();int step=0;
        Func<StartupFrame> racing=()=>++step==4?new StartupFrame{Url="https://127.0.0.1:54321/conversation"}:Failed();
        policy.Visit("C",0,racing,probe,reload,null,()=>false);policy.Visit("C",5000,racing,probe,reload,null,()=>false);
        Check(!policy.Visit("C",10000,racing,probe,reload,null,()=>false)&&reloads==2,"A user-restored conversation wins the race and is never reloaded");
        policy=new StartupRecoveryPolicy();policy.Visit("D",0,read,probe,reload,null,()=>false);policy.Visit("D",5000,read,probe,reload,null,()=>false);
        Func<Uri,bool> disableDuringProbe=uri=>{cancel=true;return true;};
        Check(!policy.Visit("D",10000,read,disableDuringProbe,reload,null,()=>cancel)&&reloads==2,"Cancellation during a readiness probe prevents the command");cancel=false;
        Check(!AntigravityStartupRecovery.BackendReady(new Uri("https://remote.example/"),"",0),"Remote TLS is never probed or given a certificate exception");
        var listener=new TcpListener(IPAddress.Loopback,0);try{
            listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
            Check(LoopbackTcpOwner.Find(port)==System.Diagnostics.Process.GetCurrentProcess().Id,"Native TCP ownership lookup recognizes this test's local listener");
            Check(!AntigravityStartupRecovery.BackendReady(new Uri("https://127.0.0.1:"+port),"not-the-owning-application.exe",System.Diagnostics.Process.GetCurrentProcess().SessionId),"A port owned by another executable cannot be used as the backend");
        }finally{listener.Stop();}
        string keyPath=@"SOFTWARE\KodVsego\StartupRecoveryTest-"+Guid.NewGuid().ToString("N");
        try{using(var key=Registry.CurrentUser.CreateSubKey(keyPath)){
            Check(AntigravityStartupRecovery.ReadEnabled(key),"Automatic error-page recovery is enabled by default");key.SetValue(AntigravityStartupRecovery.Preference,0);
            Check(!AntigravityStartupRecovery.ReadEnabled(key),"An explicit preference disables automatic recovery");key.SetValue(AntigravityStartupRecovery.Preference,1);
            Check(AntigravityStartupRecovery.ReadEnabled(key),"Recovery can be enabled again independently of restart dialogs");
        }}finally{Registry.CurrentUser.DeleteSubKeyTree(keyPath,false);}
        using(var form=new MainForm(new Session(),false,new Session())){
            var field=typeof(MainForm).GetField("startupRecoveryEnabled",BindingFlags.Instance|BindingFlags.NonPublic);field.SetValue(form,false);
            using(var settings=form.CreateSettingsDialog()){
                Check(!settings.StartupRecoveryEnabled,"Settings display the stored recovery preference");
                var control=(CheckBox)typeof(StyledDialog).GetField("settingsStartupRecovery",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(settings);
                settings.Show();Application.DoEvents();
                Check(control.Visible&&control.Bottom<settings.ClientSize.Height-40,"Recovery option is visible in General settings and fits above actions");
                using(var bitmap=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-startup-recovery-settings.png"));}
                settings.SelectSettingsTab(true);Check(!control.Visible,"The recovery preference stays hidden on the DNS providers tab");settings.SelectSettingsTab(false);
                control.Checked=true;settings.DialogResult=DialogResult.Cancel;
            }
            Check(!(bool)field.GetValue(form),"Closing settings without Save does not change the active recovery preference");
        }
        Console.WriteLine("All "+checks+" startup recovery checks passed; fake pages, loopback ownership and no application reloads.");
    }
}
