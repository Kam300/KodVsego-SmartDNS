using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace HbAi.XboxDns {
    sealed class CatalogNotPublishedException : IOException {}
    sealed class CatalogChecker { public string kind {get;set;} public string url {get;set;} public string control {get;set;} public string lookupSuffix {get;set;} }
    sealed class CatalogDocument {
        public int schema {get;set;} public long revision {get;set;} public string updatedUtc {get;set;}
        public ProviderEntry[] providers {get;set;} public Dictionary<string,CatalogChecker> checkers {get;set;}
        internal string Raw;
    }
    // Only data from the fixed project URL. No scripts, redirects, executable updates or network changes.
    static class CatalogUpdate {
        internal const int Limit=262144;
        static readonly object gate=new object();
        internal static string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KodVsego","SmartDns","catalog");
        static CatalogDocument active;
        static bool working;
        internal static string LastResult="Встроенный каталог";
        internal static string Root {get{return root;}}
        static string FileAt(string name){return Path.Combine(root,name);}
        internal static Func<string> Download=DownloadHttps;
        static string DownloadHttps(){try{ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;var request=(HttpWebRequest)WebRequest.Create(CatalogDefaults.Url);request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;request.AllowAutoRedirect=false;request.Proxy=null;request.Timeout=12000;request.ReadWriteTimeout=6000;request.UserAgent="KodVsego-SmartDNS-Catalog";using(var response=(HttpWebResponse)request.GetResponse()){Require(response.StatusCode==HttpStatusCode.OK);using(var input=response.GetResponseStream())using(var output=new MemoryStream()){var buffer=new byte[4096];int count;while((count=input.Read(buffer,0,buffer.Length))>0){Require(output.Length+count<=Limit);output.Write(buffer,0,count);}return Encoding.UTF8.GetString(output.ToArray());}}}catch(WebException error){var response=error.Response as HttpWebResponse;if(response!=null&&response.StatusCode==HttpStatusCode.NotFound){response.Dispose();throw new CatalogNotPublishedException();}throw;}}
        static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=Limit,RecursionLimit=24};}
        internal static bool PublicIp(string value) {
            IPAddress ip;if(string.IsNullOrEmpty(value)||value.Contains('%')||!IPAddress.TryParse(value,out ip)||IPAddress.IsLoopback(ip))return false;
            var b=ip.GetAddressBytes();if(b.Length==4)return ip.ToString()==value&&b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127)&&!(b[0]==198&&(b[1]==18||b[1]==19))&&!(b[0]==192&&b[1]==0&&(b[2]==0||b[2]==2))&&!(b[0]==198&&b[1]==51&&b[2]==100)&&!(b[0]==203&&b[1]==0&&b[2]==113);
            return (b[0]&224)==32&&!(b[0]==32&&b[1]==1&&b[2]==13&&b[3]==184)&&value.Contains(":")&&!ip.Equals(IPAddress.IPv6Any)&&!ip.IsIPv6LinkLocal&&!ip.IsIPv6Multicast&&(b[0]&254)!=252&&!ip.IsIPv4MappedToIPv6;
        }
        static bool Host(string value){return value!=null&&value.Length<=253&&Regex.IsMatch(value,@"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+\z")&&!Regex.IsMatch(value,@"\A[0-9.]+\z")&&!value.EndsWith(".local",StringComparison.OrdinalIgnoreCase)&&!value.EndsWith(".internal",StringComparison.OrdinalIgnoreCase);}
        static bool Https(string value,bool query){Uri u;return value!=null&&value.Length<=512&&Uri.TryCreate(value,UriKind.Absolute,out u)&&u.Scheme=="https"&&u.Port==443&&u.UserInfo==""&&u.Fragment==""&&(query||u.Query=="")&&(Host(u.Host)||PublicIp(u.Host));}
        static void Require(bool ok){if(!ok)throw new InvalidDataException("Каталог DNS не прошёл проверку формата или адресов.");}
        static void Text(string value,int limit,bool empty){Require(value!=null&&value.Length<=limit&&(empty||value.Length>0)&&!value.Any(char.IsControl));}
        static void Addresses(string[] values,bool required){Require(values!=null&&values.Length<=8&&(!required||values.Length>0)&&values.All(PublicIp));}
        internal static CatalogDocument Parse(string json,long minimum,string previous) {
            Require(json!=null&&Encoding.UTF8.GetByteCount(json)<=Limit);
            var serializer=Serializer();var raw=serializer.DeserializeObject(json) as Dictionary<string,object>;Require(raw!=null&&raw.ContainsKey("schema")&&raw["schema"] is int&&(int)raw["schema"]==1);
            Require(raw.ContainsKey("revision")&&(raw["revision"] is long||raw["revision"] is int));
            var doc=serializer.Deserialize<CatalogDocument>(json);Require(doc.revision>=minimum&&doc.revision<=99999999999999L&&doc.providers!=null&&doc.providers.Length>=ProviderCatalog.All.Length&&doc.providers.Length<=64&&doc.checkers!=null);
            DateTime date;Require(DateTime.TryParse(doc.updatedUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out date));
            if(doc.revision==CatalogDefaults.Revision)Require(json.Trim()==CatalogDefaults.Json.Trim());
            if(doc.revision==minimum&&previous!=null)Require(json.Trim()==previous.Trim());
            var prior=previous==null?null:serializer.Deserialize<CatalogDocument>(previous);Require(prior==null||doc.providers.Length>=prior.providers.Length);
            var entries=raw["providers"] as object[];Require(entries!=null&&entries.Length==doc.providers.Length);
            var keys=new HashSet<string>();bool any=false;
            for(int n=0;n<doc.providers.Length;n++) {
                var p=doc.providers[n];var r=entries[n] as Dictionary<string,object>;Require(p!=null&&r!=null&&r.ContainsKey("id")&&r["id"] is int&&(int)r["id"]==n&&r.ContainsKey("enabled")&&r["enabled"] is bool&&r.ContainsKey("encryptedOnly")&&r["encryptedOnly"] is bool);
                Require(p.id==n&&p.key!=null&&Regex.IsMatch(p.key,@"\A[a-z0-9-]{1,48}\z")&&keys.Add(p.key)&&(n>=ProviderCatalog.All.Length||p.key==ProviderCatalog.All[n].key));
                Require(prior==null||n>=prior.providers.Length||p.key==prior.providers[n].key);
                Require(!(n==1||n==2||n==3)||!p.enabled);any|=p.enabled;Text(p.name,48,false);Text(p.description,256,true);Text(p.site,80,true);
                Addresses(p.ipv4,true);Addresses(p.ipv6,false);Addresses(p.windowsDohServers,true);Addresses(p.windowsDotServers,false);Require(p.ipv4.All(x=>!x.Contains(":"))&&p.ipv6.All(x=>x.Contains(":"))&&p.windowsDohServers.All(x=>!x.Contains(":"))&&p.windowsDotServers.All(x=>!x.Contains(":")));
                Require(p.doh==null||Https(p.doh,false));Require(!p.encryptedOnly||p.doh!=null);Require(p.dot==null?p.windowsDotServers.Length==0:Host(p.dot)&&p.windowsDotServers.Length>0);Require(p.url==null||Https(p.url,true));
                Require(p.services!=null&&p.services.Length<=16&&p.autoGoals!=null&&p.autoGoals.Length<=8&&p.testHosts!=null&&p.testHosts.Length>0&&p.testHosts.Length<=32&&p.testHosts.All(Host));
                foreach(var service in p.services)Text(service,48,false);
                Require(p.autoGoals.All(g=>new[]{"ChatGPT","Claude","Gemini","ElevenLabs","Spotify","Xbox","Antigravity"}.Contains(g)));
                Require(p.legacyIpv4!=null&&p.legacyIpv4.Length<=32);foreach(var old in p.legacyIpv4)Addresses(old,true);
            }
            Require(any&&doc.checkers.Count<=64);
            foreach(var pair in doc.checkers) {var c=pair.Value;Require(keys.Contains(pair.Key)&&c!=null&&new[]{"comss","dns-ai","zapret","wlrus","webshift","malw"}.Contains(c.kind)&&Https(c.url,false));Require(c.control==null||Https(c.control,false));Require(c.kind!="dns-ai"||c.control!=null);Require(c.lookupSuffix==null||Host(c.lookupSuffix));Require(c.kind!="zapret"||c.lookupSuffix!=null);}
            doc.Raw=json.Trim();return doc;
        }
        static CatalogDocument Builtin(){return Parse(CatalogDefaults.Json,CatalogDefaults.Revision,null);}
        static string Read(string name){var file=FileAt(name);if(!File.Exists(file))return null;var info=new FileInfo(file);Require(info.Length<=Limit);return File.ReadAllText(file,Encoding.UTF8);}
        static void Write(string name,string text){Directory.CreateDirectory(root);string target=FileAt(name),temp=FileAt(Guid.NewGuid().ToString("N")+".tmp");try{File.WriteAllText(temp,text,new UTF8Encoding(false));if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);}finally{if(File.Exists(temp))File.Delete(temp);}}
        internal static ProviderEntry[] Builtins() {lock(gate){if(active==null){active=Builtin();try{string json=Read("active.json");if(json!=null)active=Parse(json,CatalogDefaults.Revision,null);}catch(Exception error){DiagnosticReport.Record("Каталог DNS","Локальная копия отклонена: "+error.GetType().Name);}InstallCheckerHooks();}return active.providers;}}
        internal static long Revision {get{Builtins();lock(gate)return active.revision;}}
        internal static bool Auto {get{try{return !File.Exists(FileAt("manual-only"));}catch{return true;}}set{lock(gate){if(value){if(File.Exists(FileAt("manual-only")))File.Delete(FileAt("manual-only"));}else Write("manual-only","1");}}}
        internal static string Summary {get{Builtins();lock(gate){if(File.Exists(FileAt("pending.json")))return "Новый каталог ожидает выключения DNS";if(LastResult.StartsWith("Онлайн-каталог"))return "Онлайн-каталог ещё не опубликован";if(LastResult.StartsWith("Не удалось"))return "Нет обновления · прежний каталог сохранён";return LastResult;}}}
        internal static string Status {get{Builtins();lock(gate){string checkedAt="не проверялся";try{checkedAt=File.GetLastWriteTime(FileAt("checked")).ToString("g");if(!File.Exists(FileAt("checked")))checkedAt="не проверялся";}catch{}return "Каталог: "+active.revision+"\nПроверка: "+checkedAt+"\n"+LastResult+(File.Exists(FileAt("pending.json"))?"\nНовые данные ожидают выключения DNS и восстановления копий.":"");}}}
        internal static void Check(bool manual,Action finished) {
            lock(gate){if(working){if(finished!=null)ThreadPool.QueueUserWorkItem(_=>finished());return;}if(!manual){if(!Auto)return;try{if(File.Exists(FileAt("checked"))&&DateTime.UtcNow-File.GetLastWriteTimeUtc(FileAt("checked"))<TimeSpan.FromDays(1))return;}catch{}}working=true;}
            ThreadPool.QueueUserWorkItem(_=>{try{Builtins();Write("checked",DateTime.UtcNow.ToString("O"));long minimum;string previous;lock(gate){minimum=active.revision;previous=active.Raw;try{var pending=Read("pending.json");if(pending!=null){var parsed=Parse(pending,minimum,null);if(parsed.revision>minimum){minimum=parsed.revision;previous=parsed.Raw;}}}catch{}}
                    string body=Download();
                    var candidate=Parse(body,minimum,previous);lock(gate){if(candidate.revision>active.revision){Write("pending.json",candidate.Raw);LastResult="Новая версия каталога скачана и проверена.";}else LastResult="Каталог актуален.";}
                }catch(Exception error){lock(gate)LastResult=error is CatalogNotPublishedException?"Онлайн-каталог ещё не опубликован (HTTP 404). Используется встроенная или сохранённая копия.":"Не удалось обновить каталог. Сохранённая копия остаётся рабочей.";DiagnosticReport.Record("Каталог DNS",error.GetType().Name);}finally{lock(gate)working=false;if(finished!=null)finished();}});
        }
        internal static bool Promote(bool idle) {if(!idle)return false;lock(gate){Builtins();string json=Read("pending.json");if(json==null)return false;var doc=Parse(json,active.revision,active.Raw);if(doc.revision<=active.revision){File.Delete(FileAt("pending.json"));return false;}Write("active.json",doc.Raw);active=doc;InstallCheckerHooks();File.Delete(FileAt("pending.json"));LastResult="Каталог применён.";return true;}}
        static CatalogChecker Checker(int id){Builtins();return id>=0&&id<active.providers.Length&&active.checkers.ContainsKey(active.providers[id].key)?active.checkers[active.providers[id].key]:null;}
        static void InstallCheckerHooks(){
            ProviderCheck.CatalogKind=id=>{var c=Checker(id);return c==null?null:c.kind;};
            ProviderCheck.CatalogUrl=(id,field,fallback)=>{var c=Checker(id);return c==null?fallback:field=="control"?c.control??fallback:field=="lookupSuffix"?c.lookupSuffix??fallback:c.url;};
            ProviderCheckIo.CatalogAllowed=url=>{Uri u;if(!Uri.TryCreate(url,UriKind.Absolute,out u)||u.UserInfo!=""||u.Fragment!=""||u.Port!=443||u.Scheme!="https")return false;string endpoint=u.GetLeftPart(UriPartial.Path);return active.checkers.Values.Any(c=>c.url==endpoint||c.control==endpoint);};
            ProviderCheckIo.CatalogLookup=host=>active.checkers.Values.Any(c=>c.kind=="zapret"&&Regex.IsMatch(host??"",@"\A[a-f0-9]{16}\."+Regex.Escape(c.lookupSuffix)+@"\z"));
        }
    }
}
