using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    // Entries retain their positions after deletion so saved provider IDs never change.
    static class CustomDns
    {
        internal const string RegistryPath=@"SOFTWARE\KodVsego\SmartDns", Value="CustomDnsProviders";
        internal const int Limit=32;
        internal const int First=64;
        internal static bool IsCustom(int id){return id>=First;}
        internal static ProviderEntry Clone(ProviderEntry entry){var json=new JavaScriptSerializer();return json.Deserialize<ProviderEntry>(json.Serialize(entry));}
        internal static ProviderEntry Create(int id,string name,string addresses,string doh,string dot,bool auto,bool antigravity)
        {
            name=(name??"").Trim();if(name.Length==0||name.Length>48||name.Any(char.IsControl))throw new IOException("Название DNS: от 1 до 48 символов.");
            var ips=(addresses??"").Split(new[]{' ',',',';','\r','\n','\t'},StringSplitOptions.RemoveEmptyEntries).Select(s=>{
                IPAddress ip;if(!IPAddress.TryParse(s,out ip)||s.Contains('%')||ip.Equals(IPAddress.Any)||ip.Equals(IPAddress.IPv6Any)||ip.Equals(IPAddress.Broadcast)||ip.IsIPv6Multicast||ip.AddressFamily==AddressFamily.InterNetwork&&ip.GetAddressBytes()[0]>=224)throw new IOException("Некорректный IP-адрес DNS: "+s);
                if(ip.AddressFamily==AddressFamily.InterNetwork&&s!=ip.ToString())throw new IOException("Используйте полный IPv4-адрес, например 1.1.1.1.");
                return ip.ToString();}).Distinct().ToArray();
            var v4=ips.Where(s=>IPAddress.Parse(s).AddressFamily==AddressFamily.InterNetwork).ToArray();var v6=ips.Except(v4).ToArray();
            if(v4.Length==0||v4.Length>4||v6.Length>4)throw new IOException("Укажите 1–4 IPv4-адреса. Дополнительно можно указать до 4 IPv6-адресов.");
            doh=(doh??"").Trim();Uri uri=null;
            if(doh.Length>0&&(!Uri.TryCreate(doh,UriKind.Absolute,out uri)||uri.Scheme!="https"||string.IsNullOrEmpty(uri.Host)||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.Port!=443))throw new IOException("DoH: нужен HTTPS-адрес без пароля, параметров и фрагмента, порт 443. Например https://dns.example/dns-query");
            dot=(dot??"").Trim();if(dot.StartsWith("tls://",StringComparison.OrdinalIgnoreCase))dot=dot.Substring(6);
            if(dot.Length>0&&(Uri.CheckHostName(dot)!=UriHostNameType.Dns||dot.Contains('/')||dot.Contains(':')))throw new IOException("DoT: укажите имя сервера без пути и порта, например dns.example (TLS, порт 853).");
            if(antigravity&&doh.Length==0)throw new IOException("Для отдельного Antigravity нужен адрес DoH.");
            var goals=auto?new List<string>{"ChatGPT","Claude","Gemini","ElevenLabs","Spotify"}:new List<string>();if(antigravity)goals.Add("Antigravity");
            return new ProviderEntry{id=id,key="custom-"+(id-First),name=name,enabled=true,ipv4=v4,ipv6=v6,doh=doh.Length==0?null:doh,dot=dot.Length==0?null:dot,windowsDohServers=v4,windowsDotServers=dot.Length==0?new string[0]:v4,services=new string[0],autoGoals=goals.ToArray(),testHosts=new[]{"example.com"},legacyIpv4=new string[0][],encryptedOnly=false,description="Пользовательский DNS · доступ к сервисам не проверен",site="Свой DNS",url=null};
        }
        internal static ProviderEntry[] Validate(IEnumerable<ProviderEntry> entries)
        {
            var list=(entries??new ProviderEntry[0]).ToArray();if(list.Length>Limit)throw new IOException("Можно сохранить до 32 своих DNS-записей.");
            var result=new List<ProviderEntry>();
            for(int n=0;n<list.Length;n++){
                var item=list[n];if(item==null||item.id!=First+n)throw new IOException("Нарушены идентификаторы своих DNS.");
                var next=Create(item.id,item.name,string.Join(" ",(item.ipv4??new string[0]).Concat(item.ipv6??new string[0])),item.doh,item.dot,(item.autoGoals??new string[0]).Any(g=>g!="Antigravity"),(item.autoGoals??new string[0]).Contains("Antigravity"));
                next.enabled=item.enabled;
                next.legacyIpv4=(item.legacyIpv4??new string[0][]).Take(16).Select(group=>Create(item.id,item.name,string.Join(" ",group),null,null,false,false).ipv4).ToArray();result.Add(next);
            }
            return result.ToArray();
        }
        internal static ProviderEntry[] Read(RegistryKey key)
        {
            string json=key==null?null:key.GetValue(Value) as string;if(string.IsNullOrWhiteSpace(json))return new ProviderEntry[0];
            if(json.Length>131072)throw new IOException("Список своих DNS слишком большой.");
            return Validate(new JavaScriptSerializer().Deserialize<ProviderEntry[]>(json));
        }
        internal static void Save(RegistryKey key,IEnumerable<ProviderEntry> entries){var valid=Validate(entries);key.SetValue(Value,new JavaScriptSerializer().Serialize(valid),RegistryValueKind.String);}
    }
    sealed class ProviderSnapshot
    {
        internal readonly ProviderEntry[] All;internal readonly string[] Names,Descriptions,Sites,Urls,Templates;internal readonly string[][] V4,V6;
        internal ProviderSnapshot(ProviderEntry[] all){All=all;Names=all.Select(p=>p.name).ToArray();Descriptions=all.Select(p=>p.description).ToArray();Sites=all.Select(p=>p.site).ToArray();Urls=all.Select(p=>p.url).ToArray();Templates=all.Select(p=>p.doh).ToArray();V4=all.Select(p=>p.ipv4).ToArray();V6=all.Select(p=>p.ipv6).ToArray();}
    }
    static class RuntimeProviders
    {
        static ProviderSnapshot current=Load();
        internal static ProviderSnapshot Snapshot {get{return Volatile.Read(ref current);}}
        internal static ProviderEntry[] All {get{return Snapshot.All;}}
        static ProviderEntry[] Merge(ProviderEntry[] custom)
        {
            if(custom.Length==0)return ProviderCatalog.All;
            if(ProviderCatalog.All.Length>CustomDns.First)throw new IOException("Конфликт идентификаторов DNS-каталога.");
            var reserved=Enumerable.Range(ProviderCatalog.All.Length,CustomDns.First-ProviderCatalog.All.Length).Select(id=>new ProviderEntry{id=id,key="reserved-"+id,name="Недоступен",enabled=false,ipv4=new string[0],ipv6=new string[0],services=new string[0],autoGoals=new string[0],testHosts=new string[0],windowsDohServers=new string[0],windowsDotServers=new string[0],legacyIpv4=new string[0][],description="",site="",url=null});
            return ProviderCatalog.All.Concat(reserved).Concat(custom).ToArray();
        }
        static ProviderSnapshot Load(){ProviderEntry[] custom=new ProviderEntry[0];try{using(var key=Registry.CurrentUser.OpenSubKey(CustomDns.RegistryPath))custom=CustomDns.Read(key);}catch(Exception error){DiagnosticReport.Record("Свои DNS","Не удалось загрузить список: "+error.GetType().Name);}return new ProviderSnapshot(Merge(custom));}
        internal static void Reload(){Interlocked.Exchange(ref current,Load());}
        internal static void Replace(ProviderEntry[] custom){Interlocked.Exchange(ref current,new ProviderSnapshot(Merge(CustomDns.Validate(custom))));}
        internal static ProviderEntry[] Customs {get{return All.Skip(CustomDns.First).Select(CustomDns.Clone).ToArray();}}
    }
}
