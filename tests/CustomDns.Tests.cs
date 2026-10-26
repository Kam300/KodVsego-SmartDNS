using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;
using HbAi.XboxDns;

static class CustomDnsTests
{
    static int count;static readonly BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    static void Reject(Action action,string name){bool rejected=false;try{action();}catch(IOException){rejected=true;}Check(rejected,name);}
    static ProviderEntry Entry(int id,string name="My DNS"){return CustomDns.Create(id,name,"203.0.113.53, 203.0.113.54; 2001:db8::53","https://resolver.example/dns-query","tls://resolver.example",true,true);}
    sealed class Session : ITunnelSession
    {
        public bool Running{get{return false;}}public bool Connected{get{return false;}}public bool Stopping{get{return false;}}
        public string LastError{get{return null;}}public string Statistics{get{return "0 0";}}public event Action Changed{add{}remove{}}
        public void Start(int provider,bool tls){throw new Exception("Unexpected worker launch");}public void Stop(){}public void Dispose(){}
    }
    [STAThread]static void Main()
    {
        int first=CustomDns.First;var original=RuntimeProviders.Customs;
        try{
            RuntimeProviders.Replace(new ProviderEntry[0]);var item=Entry(first);
            Check(item.ipv4.Length==2&&item.ipv6.Length==1&&item.dot=="resolver.example"&&item.autoGoals.Contains("Antigravity"),"Custom fields normalize IPv4/IPv6, DoH, DoT and explicit Antigravity support");
            foreach(string name in new[]{"",new string('x',49),"bad\nname"})Reject(()=>CustomDns.Create(first,name,"203.0.113.53",null,null,false,false),"Reject invalid custom name");
            foreach(string ips in new[]{"","resolver.example","0.0.0.0","::","224.0.0.1","255.255.255.255","2001:db8::53","1.2","123"})Reject(()=>CustomDns.Create(first,"DNS",ips,null,null,false,false),"Reject invalid or unsupported server list "+ips);
            foreach(string url in new[]{"http://dns.example/dns-query","https://user:pass@dns.example/dns-query","https://dns.example/dns-query?key=secret","https://dns.example/#fragment","https://dns.example:8443/dns-query"})Reject(()=>CustomDns.Create(first,"DNS","203.0.113.53",url,null,false,false),"Reject unsupported or credential-bearing DoH endpoint");
            foreach(string dot in new[]{"dns.example/path","dns.example:853","https://dns.example"})Reject(()=>CustomDns.Create(first,"DNS","203.0.113.53",null,dot,false,false),"Reject malformed DoT hostname");
            Reject(()=>CustomDns.Create(first,"DNS","203.0.113.53",null,null,false,true),"Separate Antigravity requires DoH");
            Reject(()=>CustomDns.Validate(new[]{Entry(first+1)}),"Out-of-order custom IDs cannot shift stored provider identities");
            Reject(()=>CustomDns.Validate(Enumerable.Range(first,33).Select(id=>Entry(id))),"Bound the custom catalog below elevation command encoding ranges");
            string keyPath=@"SOFTWARE\KodVsego\SmartDns-CustomTest-"+Guid.NewGuid().ToString("N");
            try{using(var key=Registry.CurrentUser.CreateSubKey(keyPath)){
                CustomDns.Save(key,new[]{item});var loaded=CustomDns.Read(key);
                Check(loaded.Length==1&&loaded[0].id==first&&loaded[0].doh==item.doh&&loaded[0].ipv6.SequenceEqual(item.ipv6),"Custom providers round-trip through an isolated registry value");
                key.SetValue(CustomDns.Value,"broken");bool rejected=false;try{CustomDns.Read(key);}catch{rejected=true;}Check(rejected,"Corrupt storage does not produce a partially initialized provider");
            }}finally{Registry.CurrentUser.DeleteSubKeyTree(keyPath,false);}
            using(var form=new MainForm(new Session(),false,new Session())){
                foreach(string field in new[]{"initializing","busy","active","agLoading"})typeof(MainForm).GetField(field,Flags).SetValue(form,false);
                using(var settings=form.CreateSettingsDialog()){
                    Check(settings.StageCustomProvider(item)&&settings.CustomProvidersChanged,"Adding a custom provider stages a draft in settings");
                    Check(Dns.Name.Length==ProviderCatalog.All.Length&&!Dns.IsAvailable(first),"Draft additions leave the runtime and network untouched until Save");
                    settings.SelectSettingsTab(true);settings.Show();Application.DoEvents();
                    var rows=(Panel)typeof(StyledDialog).GetField("providerRows",Flags).GetValue(settings);
                    Check(rows.Controls.OfType<CheckBox>().Any(c=>c.Text=="My DNS"),"Draft custom DNS appears in the provider settings list");
                    using(var bitmap=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-custom-settings.png"));}
                    var edit=Entry(first,"Renamed DNS");edit.ipv4=new[]{"203.0.113.55"};settings.CustomDnsLocked=id=>id==first;
                    Check(!settings.StageCustomProvider(edit),"Editing or deleting a protected active custom provider is refused");
                    settings.CustomDnsLocked=id=>false;Check(settings.StageCustomProvider(edit)&&settings.CustomProviders[0].legacyIpv4.Any(v=>v.SequenceEqual(item.ipv4)),"Editing retains previous DNS addresses for recovery recognition");
                    settings.Close();
                }
                Check(Dns.Name.Length==ProviderCatalog.All.Length,"Closing settings without Save discards custom drafts");
                form.RefreshCustomProviders(new[]{item});
                Check(Dns.IsAvailable(first)&&Dns.Name[first]=="My DNS"&&EncryptedDns.Templates[first]==item.doh&&DotDns.Host(first)==item.dot,"New provider is available in the main runtime without restarting the app");
                Check(Dns.AvailableProviders.Contains(first)&&AutoDns.Candidates(AutoDns.GoalIndex("Gemini"),true,false).Contains(first)&&LocalDnsRouting.Supports(first),"Custom DNS participates in visible selection, Auto and declared separate Antigravity");
                Check(DnsRequest.Supported(true,first)&&DnsRequest.Supported(true,200+first)&&DnsRequest.Supported(true,400+first),"Plain, HTTPS and TLS custom IDs use existing authenticated elevation commands");
                Check(((Rectangle[])typeof(MainForm).GetField("rSeg",Flags).GetValue(form)).Length==Dns.Name.Length+1,"Main provider and Auto tiles resize together after adding custom DNS");
                var duplicate=CustomDns.Create(first,"Shared IP profile",string.Join(" ",Dns.V4[13]),"https://other.example/dns-query",null,true,false);
                form.RefreshCustomProviders(new[]{duplicate});
                var state=new FamilyBackup{Servers=duplicate.ipv4,DohCaptured=true,Doh=EncryptedDns.Strict(duplicate.ipv4.Length,duplicate.doh)};
                Check(Dns.MatchConfiguration(state,first)==first,"A custom DoH profile sharing a built-in server IP is identified by its template");
                duplicate.enabled=false;form.RefreshCustomProviders(new[]{duplicate,Entry(first+1,"Second DNS")});
                Check(!Dns.IsAvailable(first)&&Dns.IsAvailable(first+1)&&Dns.Name[first+1]=="Second DNS"&&Dns.KnownServers.Contains(duplicate.ipv4[0]),"Deletion hides its tombstone without renumbering later providers or forgetting old addresses");
                using(var settings=form.CreateSettingsDialog())Check(!settings.StageCustomProvider(Entry(first+2,"Extra"))||settings.CustomProviders.Length==3,"New additions retain the catalog order after deleted records");
            }
        }finally{RuntimeProviders.Replace(original);}
        Console.WriteLine("All "+count+" custom DNS checks passed; drafts, fake configuration and isolated registry only; no adapter changes.");
    }
}
