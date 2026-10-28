using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using HbAi.XboxDns;
static class CatalogUpdateTests {
    static int count;static readonly JavaScriptSerializer serializer=new JavaScriptSerializer();
    static void Check(bool v,string name){if(!v)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    static CatalogDocument Next(){var d=serializer.Deserialize<CatalogDocument>(CatalogDefaults.Json);d.revision++;return d;}
    static string Json(CatalogDocument d){return serializer.Serialize(d);}
    static void Reject(Action action,string name){try{action();}catch{Check(true,name);return;}throw new Exception("Accepted: "+name);}
    static void Download(){using(var done=new ManualResetEvent(false)){CatalogUpdate.Check(true,()=>done.Set());Check(done.WaitOne(5000),"Bounded fake catalog request completes");}}
    sealed class Session : ITunnelSession {
        public bool Running{get{return false;}}public bool Connected{get{return false;}}public bool Stopping{get{return false;}}
        public string LastError{get{return null;}}public string Statistics{get{return "0 0";}}public event Action Changed{add{}remove{}}
        public void Start(int provider,bool tls){throw new Exception("Unexpected worker");}public void Stop(){}public void Dispose(){}
    }
    [STAThread]static void Main(){
        string root=CatalogUpdate.root;var fetch=CatalogUpdate.Download;
        string isolated=Path.Combine(Path.GetTempPath(),"SmartDNS-catalog-"+Guid.NewGuid().ToString("N"));CatalogUpdate.root=isolated;
        try{
            var builtin=CatalogUpdate.Parse(CatalogDefaults.Json,CatalogDefaults.Revision,null);Check(builtin.providers.Length==ProviderCatalog.All.Length,"Bundled catalog validates; baseline identities preserved");
            Check(CatalogUpdate.Builtins().Length==builtin.providers.Length,"Offline startup uses bundled copy");
            foreach(string ip in new[]{"127.0.0.1","10.0.0.1","169.254.1.1","100.64.0.1","224.0.0.1","::1","fc00::1","fe80::1","fec0::1","2001:db8::1","203.0.113.53","resolver.example","123","1.2"})Check(!CatalogUpdate.PublicIp(ip),"Reject nonpublic or nonnumeric IP "+ip);
            foreach(string url in new[]{"http://resolver.example/dns-query","https://127.0.0.1/dns-query","https://10.0.0.1/dns-query","https://user:pass@resolver.example/dns-query","https://resolver.example:444/dns-query","https://resolver.example/dns-query?secret=x","https://resolver.example/#fragment"}){var d=Next();d.providers[0].doh=url;Reject(()=>CatalogUpdate.Parse(Json(d),builtin.revision,builtin.Raw),"Reject unsafe endpoint");}
            var changed=Next();changed.providers[0].id=2;Reject(()=>CatalogUpdate.Parse(Json(changed),builtin.revision,null),"Reject reordered IDs");
            changed=Next();changed.providers[0].key="different";Reject(()=>CatalogUpdate.Parse(Json(changed),builtin.revision,null),"Reject changed identity");
            changed=Next();changed.providers[3].enabled=true;Reject(()=>CatalogUpdate.Parse(Json(changed),builtin.revision,null),"Do not reactivate retired Xbox");
            changed=Next();changed.checkers["comss"].kind="execute";Reject(()=>CatalogUpdate.Parse(Json(changed),builtin.revision,null),"Reject unknown checker behavior");
            Reject(()=>CatalogUpdate.Parse(new string(' ',CatalogUpdate.Limit+1),builtin.revision,null),"Bound catalog size");
            changed=Next();changed.revision--;changed.providers[0].name="Changed";Reject(()=>CatalogUpdate.Parse(Json(changed),builtin.revision,builtin.Raw),"Same revision cannot change body");
            changed=Next();changed.revision-=2;Reject(()=>CatalogUpdate.Parse(Json(changed),builtin.revision,null),"Reject rollback");
            var next=Next();next.providers[0].name="Updated DNS";next.providers[6].legacyIpv4=next.providers[6].legacyIpv4.Concat(new[]{next.providers[6].ipv4}).ToArray();next.providers[6].ipv4=new[]{"1.1.1.1"};next.checkers["comss"].url="https://newcheck.example/";string candidate=Json(next);int calls=0;
            CatalogUpdate.Download=()=>{Interlocked.Increment(ref calls);return candidate;};Download();
            Check(CatalogUpdate.Revision==builtin.revision&&File.Exists(Path.Combine(isolated,"pending.json")),"Valid download is staged without altering active DNS data");
            CatalogUpdate.Check(false,null);Check(calls==1,"Automatic checks bounded to once a day");
            Check(!CatalogUpdate.Promote(false)&&CatalogUpdate.Revision==builtin.revision,"Busy or active caller cannot promote");
            var custom=CustomDns.Create(64,"Own DNS","1.1.1.1",null,null,false,false);RuntimeProviders.Replace(new[]{custom});
            Check(CatalogUpdate.Promote(true),"Promote while idle");RuntimeProviders.Replace(new[]{custom});
            Check(RuntimeProviders.All[0].name=="Updated DNS"&&RuntimeProviders.All[64].name=="Own DNS","Custom DNS survives built-in catalog update");
            Check(AntigravityDns.ServerSets[AntigravityDns.ProviderForMainSet(6)].SequenceEqual(new[]{"1.1.1.1"}),"Existing Antigravity slot uses refreshed addresses without renumbering");
            Check(ProviderCheckIo.CatalogAllowed("https://newcheck.example/?c=1")&&!ProviderCheckIo.CatalogAllowed("https://newcheck.example/other"),"Checker endpoints updated with exact path allowlist");
            CatalogUpdate.Download=()=>{throw new IOException("offline");};Download();Check(CatalogUpdate.Revision==next.revision,"Network failure retains active revision");
            CatalogUpdate.Download=()=>candidate;Download();Check(!CatalogUpdate.Promote(true),"Identical catalog causes no reload");
            CatalogUpdate.Download=()=>CatalogDefaults.Json;Download();Check(CatalogUpdate.Revision==next.revision&&!File.Exists(Path.Combine(isolated,"pending.json")),"Remote rollback never replaces accepted copy");
            CatalogUpdate.Auto=false;Check(!CatalogUpdate.Auto,"Manual-only preference persists");
            var active=typeof(CatalogUpdate).GetField("active",BindingFlags.Static|BindingFlags.NonPublic);active.SetValue(null,null);
            Check(CatalogUpdate.Builtins()[0].name=="Updated DNS","Accepted disk copy survives restart");
            File.WriteAllText(Path.Combine(isolated,"active.json"),"broken");active.SetValue(null,null);Check(CatalogUpdate.Builtins()[0].name==builtin.providers[0].name,"Corrupt disk cache falls back to bundled data");
            using(var form=new MainForm(new Session(),false,new Session()))using(var settings=form.CreateSettingsDialog()){
                settings.Show();Application.DoEvents();
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var link=(Panel)typeof(StyledDialog).GetField("settingsCatalog",flags).GetValue(settings);
                var toggle=(CheckBox)typeof(StyledDialog).GetField("settingsCatalogAuto",flags).GetValue(settings);
                Check(!toggle.Checked,"Inline checkbox reads the persisted preference");toggle.Checked=true;
                Check(settings.AutoCatalogUpdate&&!CatalogUpdate.Auto,"Checkbox is a draft until Save");
                var save=(Button)typeof(StyledDialog).GetField("primaryAction",flags).GetValue(settings);
                Check(link.Visible&&link.Bottom<=save.Top,"Inline catalog controls remain above the footer");
                ((DialogModeOption)typeof(StyledDialog).GetField("settingsAutoMode",flags).GetValue(settings)).Checked=true;
                typeof(StyledDialog).GetMethod("UpdateDialogLayout",flags).Invoke(settings,null);
                Check(link.Bottom<=save.Top,"Auto mode also leaves catalog controls above the footer");
                CatalogUpdate.Download=()=>{throw new CatalogNotPublishedException();};int windows=Application.OpenForms.Count;
                var refresh=(LinkLabel)typeof(StyledDialog).GetField("settingsCatalogRefresh",flags).GetValue(settings);
                typeof(LinkLabel).GetMethod("OnLinkClicked",flags).Invoke(refresh,new object[]{new LinkLabelLinkClickedEventArgs(null)});
                var deadline=DateTime.UtcNow.AddSeconds(3);while(!refresh.Enabled&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
                Check(refresh.Enabled&&CatalogUpdate.Summary.Contains("ещё не опубликован"),"Unpublished catalog has an explicit inline status");
                Check(Application.OpenForms.Count==windows,"Manual check opens no additional window");
                using(var image=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-catalog-settings.png"));}
                settings.SelectSettingsTab(true);Check(!link.Visible,"Catalog controls hidden on provider tab");settings.Close();Check(!CatalogUpdate.Auto,"Cancel does not save the auto-update draft");
            }
            Console.WriteLine("All "+count+" catalog checks passed; fake HTTP, isolated files, no network changes.");
        }finally{CatalogUpdate.root=root;CatalogUpdate.Download=fetch;Directory.Delete(isolated,true);}
    }
}
