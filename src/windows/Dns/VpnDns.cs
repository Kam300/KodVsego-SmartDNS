using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace HbAi.XboxDns {
    // One reversible, narrowly scoped rule. Never replace a VPN's adapter DNS or routes.
    static class VpnDnsPolicy {
        internal const string Tag="KodVsego.SmartDns.VpnDns.v1";
        internal static readonly string[] Names={".chatgpt.com",".openai.com",".claude.ai",".anthropic.com",".gemini.google.com",".generativelanguage.googleapis.com",".elevenlabs.io",".spotify.com",".scdn.co",".xbox.com",".xboxlive.com",".cloudcode-pa.googleapis.com",".daily-cloudcode-pa.googleapis.com",".example.com"};
        internal static bool Owned(AgRule rule){Guid id;return Guid.TryParse(rule.Id,out id)&&id!=Guid.Empty&&rule.Local&&rule.Comment==Tag&&rule.DisplayName==Tag&&rule.Names!=null&&rule.Names.SequenceEqual(Names)&&rule.Servers!=null&&rule.Servers.SequenceEqual(new[]{"127.0.0.1"});}
        internal static bool Overlap(string a,string b){a=(a??"").TrimStart('.').ToLowerInvariant();b=(b??"").TrimStart('.').ToLowerInvariant();return a.Length==0||b.Length==0||a==b||a.EndsWith("."+b,StringComparison.Ordinal)||b.EndsWith("."+a,StringComparison.Ordinal);}
        internal static void Validate(AgRule[] rules){if(rules.Any(r=>!Owned(r)&&r.Names.Any(n=>Names.Any(own=>Overlap(n,own)))))throw new IOException("Правила DNS VPN или организации пересекаются с доменами сервисов. Они сохранены; совместный режим не включён.");}
        internal static void Apply(){
            Validate(NrptRegistryReader.Read(true));
            if(NrptRegistryReader.Read(false).Any(r=>r.Comment==Tag))throw new IOException("Осталось правило совместного режима. Сначала восстановите DNS.");
            using(WindowsAgRules.Invoke("PS_DnsClientNrptRule","Add",p=>{p["Namespace"]=Names;p["NameServers"]=new[]{"127.0.0.1"};p["Comment"]=Tag;p["DisplayName"]=Tag;})){}
            if(NrptRegistryReader.Read(false).Count(Owned)!=1)throw new IOException("Windows не подтвердила правило DNS для VPN.");
            new NativeDnsSystem().Flush();
        }
        internal static string Fingerprint(){return string.Join("|",NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up).OrderBy(n=>n.Id).Select(n=>n.Id+":"+string.Join(",",n.GetIPProperties().UnicastAddresses.Select(a=>a.Address.ToString()).OrderBy(a=>a))));}
        internal static void Remove(){Remove(()=>NrptRegistryReader.Read(false),id=>{using(WindowsAgRules.Invoke("PS_DnsClientNrptRule","Remove",p=>{p["Name"]=id;p["Force"]=true;})){}},()=>new NativeDnsSystem().Flush());}
        internal static void Remove(Func<AgRule[]> read,Action<string> remove,Action flush){
            bool removed=false;
            foreach(var rule in read().Where(r=>r.Comment==Tag||r.DisplayName==Tag)){
                if(!Owned(rule))throw new IOException("Правило совместного DNS изменилось. Автоматическое удаление отменено; восстановление остаётся ожидающим.");
                var current=read().SingleOrDefault(r=>r.Id==rule.Id);if(current==null)continue;
                if(!Owned(current))throw new IOException("Правило DNS изменено другой программой.");
                remove(rule.Id);
                removed=true;
                if(read().Any(r=>r.Id==rule.Id))throw new IOException("Windows не подтвердила удаление правила DNS для VPN.");
            }
            if(removed)flush();
        }
        internal static void Verify(LocalDnsProxy proxy){
            string host="vpn-check-"+Guid.NewGuid().ToString("N")+".example.com";
            proxy.WatchHost(host);
            try{var query=Task.Factory.StartNew(()=>Query(host));if(!query.Wait(15000))throw new IOException("Проверка DNS через VPN превысила 15 секунд.");int result=query.Result;
                if((result!=0&&result!=9003&&result!=9501)||!proxy.HostObserved)throw new IOException("VPN не передал проверочный DNS-запрос локальному серверу. Правило будет удалено. Проверьте настройки DNS в VPN-клиенте.");
            }finally{proxy.WatchHost(null);}
        }
        static int Query(string host){IntPtr records=IntPtr.Zero;try{return DnsQuery(host,1,0x100|0x40|0x08,IntPtr.Zero,out records,IntPtr.Zero);}finally{if(records!=IntPtr.Zero)DnsRecordListFree(records,1);}}
        [DllImport("dnsapi.dll",EntryPoint="DnsQuery_W",CharSet=CharSet.Unicode,ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern int DnsQuery(string name,ushort type,uint flags,IntPtr extra,out IntPtr records,IntPtr reserved);
        [DllImport("dnsapi.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern void DnsRecordListFree(IntPtr records,int kind);
    }
    static class VpnDnsPreference {
        internal const string Value="VpnDnsCompatibility";
        internal static bool Load(){try{using(var key=Registry.CurrentUser.OpenSubKey(CustomDns.RegistryPath))return key!=null&&object.Equals(key.GetValue(Value),1);}catch{return false;}}
        internal static bool Blocks(VpnStatus status,bool local,bool enabled){return status.Failed||status.NeedsAttention&&!(local&&enabled);}
    }
}
