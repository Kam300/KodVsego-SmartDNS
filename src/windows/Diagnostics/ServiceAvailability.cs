using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HbAi.XboxDns
{
    enum ServiceReply { Response, Login, BrowserCheck, Restricted, Direct, RateLimited, ServerError, Unknown, Failed }
    sealed class ServiceEndpoint
    {
        internal string Name, Host, Path;
        internal bool Api, WebSocket, InspectHtml;
    }
    sealed class ServiceTarget
    {
        internal string Name;
        internal ServiceEndpoint[] Endpoints;
    }
    sealed class ServiceHttpReply
    {
        internal int Status;
        internal Dictionary<string,string> Headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal string Body;
        internal string Header(string name) { string value; return Headers.TryGetValue(name,out value)?value:""; }
    }
    sealed class ServiceEndpointResult
    {
        internal ServiceEndpoint Endpoint;
        internal bool Dns, Connection, Tls, Suppressed, RegionSuppressed;
        internal string Address;
        internal int Status, Milliseconds;
        internal ServiceReply Reply = ServiceReply.Failed;
        internal string Error, Trace;
        internal string Caption {
            get {
                if(Error!=null)return Error;
                switch(Reply) {
                    case ServiceReply.Response: return Endpoint.Api?"Ответ получен · авторизация не проверена":"Ответ получен · вход не проверен";
                    case ServiceReply.Login: return Endpoint.Api?"Запрос без ключа: требуется авторизация":"Требуется вход";
                    case ServiceReply.BrowserCheck:return "Требуется проверка в браузере";
                    case ServiceReply.Restricted:return "Сервис сообщил об ограничении региона";
                    case ServiceReply.Direct:return "Без подмены DNS · прямой адрес сервиса";
                    case ServiceReply.RateLimited:return "Сервис ограничил частоту запросов";
                    case ServiceReply.ServerError:return "Ошибка сервера сервиса";
                    default:return "Ответ есть · доступ не подтверждён";
                }
            }
        }
        internal bool Attention {get{return Reply!=ServiceReply.Response&&Reply!=ServiceReply.Login;}}
    }
    sealed class ServiceAvailabilityResult
    {
        internal string Name;
        internal ServiceEndpointResult[] Endpoints;
        internal bool Attention {get{return Endpoints.Any(e=>e.Attention);}}
        internal bool CanRecommend {get{return Endpoints.All(e=>e.Reply==ServiceReply.Response||e.Reply==ServiceReply.Login||e.Reply==ServiceReply.BrowserCheck);}}
        internal string EndpointSummary {get{return string.Join("; ",Endpoints.Select(e=>e.Endpoint.Name+": "+e.Caption));}}
        internal bool OfferReport {get{return Endpoints.Any(e=>e.Reply==ServiceReply.Failed||e.Reply==ServiceReply.Unknown||e.Reply==ServiceReply.ServerError);}}
        internal string Summary {get{return string.Join("\n",Endpoints.Select(e=>e.Endpoint.Name+": "+e.Caption))+(Endpoints.Any(e=>e.Endpoint.Api)?"\n\nБез ключа и входа. Генерация не проверялась.":"\n\nВход в аккаунт и генерация не проверялись.");}}
        internal string Report {get{return string.Join("\n\n",Endpoints.Select(e=>e.Endpoint.Name+" · "+e.Endpoint.Host+e.Endpoint.Path+"\n"+e.Caption+
                    "\nDNS: "+(e.Dns?"ответ есть":"нет ответа")+" · соединение: "+(e.Connection?"есть":"не установлено")+" · TLS: "+(e.Tls?"сертификат проверен":"не подтверждён")+(e.Address==null?"":" · IP "+e.Address)+
            (e.Status>0?" · HTTP "+e.Status:"")+" · "+e.Milliseconds+" мс"))+
            (Endpoints.Any(e=>e.Endpoint.Api)?"\n\nВ этой проверке ключ не передавался. Ответ API 401/403 с запросом авторизации не означает отказ сохранённого ключа.":"")+"\n\nАдреса получены от выбранного DNS; HTTPS проверяется с исходным именем и сертификатом. Системные настройки не менялись. DNS браузера, аккаунт и генерация не проверялись."+(Endpoints.Any(e=>e.Endpoint.Api)?" API и сайт могут иметь разные ограничения.":"");}}
        internal string Diagnostic {get{return DiagnosticReport.Scrub(string.Join("\n\n",Endpoints.Select(e=>e.Trace)));}}
    }
    sealed class ServiceTransportException : IOException
    {
        internal readonly string Stage;
        internal ServiceTransportException(string stage,Exception inner):base(stage,inner){Stage=stage;}
    }
    interface IServiceAvailabilityProbe
    {
        string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel);
        ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel);
    }
    interface IDirectAddressGuard
    {
        bool IsDirectAddress(string host,string address,CancellationToken cancel);
    }
    static class DirectDnsGuard
    {
        sealed class Entry { internal HashSet<string> Prefixes; internal long ExpiresAt; }
        static readonly object gate=new object();
        static readonly Stopwatch Clock=Stopwatch.StartNew();
        static readonly Dictionary<string,Entry> Cache=new Dictionary<string,Entry>(StringComparer.Ordinal);
        static long generation;
        internal static void Reset() { lock(gate) { Cache.Clear(); generation++; } }
        static uint Ip4(byte a,byte b,byte c,byte d) { return ((uint)a<<24)|((uint)b<<16)|((uint)c<<8)|d; }
        static bool InCidr(uint value,uint network,int prefix) { uint mask=prefix==0?0u:0xffffffffu<<(32-prefix); return (value&mask)==(network&mask); }
        internal static bool KnownDirectRange(string address)
        {
            IPAddress ip;
            if(!IPAddress.TryParse(address,out ip)||ip.AddressFamily!=AddressFamily.InterNetwork)return false;
            byte[] b=ip.GetAddressBytes();
            uint v=((uint)b[0]<<24)|((uint)b[1]<<16)|((uint)b[2]<<8)|b[3];
            return InCidr(v,Ip4(142,250,0,0),15)||
                InCidr(v,Ip4(172,217,0,0),16)||
                InCidr(v,Ip4(216,58,192,0),19)||
                InCidr(v,Ip4(173,194,0,0),16)||
                InCidr(v,Ip4(74,125,0,0),16)||
                InCidr(v,Ip4(104,16,0,0),12)||
                InCidr(v,Ip4(172,64,0,0),13)||
                InCidr(v,Ip4(162,158,0,0),15)||
                InCidr(v,Ip4(188,114,96,0),20)||
                InCidr(v,Ip4(198,41,128,0),17)||
                InCidr(v,Ip4(141,101,64,0),18)||
                InCidr(v,Ip4(108,162,192,0),18)||
                InCidr(v,Ip4(160,79,104,0),22)||
                InCidr(v,Ip4(151,101,0,0),16)||
                InCidr(v,Ip4(199,232,0,0),16);
        }
        static string Prefix24(string address)
        {
            IPAddress ip;
            if(!IPAddress.TryParse(address,out ip)||ip.AddressFamily!=AddressFamily.InterNetwork)return null;
            byte[] b=ip.GetAddressBytes();
            return b[0]+"."+b[1]+"."+b[2];
        }
        internal static bool IsDirect(string host,string address,CancellationToken cancel)
        {
            if(!AutoDnsProbe.PublicAddress(address))return false;
            if(KnownDirectRange(address))return true;
            string key=(host??"").Trim().ToLowerInvariant();
            string prefix=Prefix24(address);
            if(key.Length==0||prefix==null)return false;
            long version;
            lock(gate) {
                version=generation;
                Entry cached;
                if(Cache.TryGetValue(key,out cached)&&cached.ExpiresAt>Clock.ElapsedMilliseconds)
                    return cached.Prefixes.Contains(prefix);
            }
            var baseline=new HashSet<string>(StringComparer.Ordinal);
            bool resolved=false;
            var q=DnsProbe.Query(key);
            foreach(string server in new[]{"94.140.14.14","94.140.15.15"}) {
                cancel.ThrowIfCancellationRequested();
                try {
                    var resp=DohHttp2.Query("https://dns.adguard-dns.com/dns-query",q,2000,server);
                    DnsProbe.Validate(q,resp);
                    foreach(string ip in DnsProbe.Addresses(q,resp)) {
                        string p24=Prefix24(ip);
                        if(p24!=null)baseline.Add(p24);
                    }
                    resolved=true;
                    break;
                }catch(OperationCanceledException){throw;}catch{}
            }
            long ttl=resolved?300000L:30000L;
            lock(gate) {
                if(version==generation) {
                    if(Cache.Count>=64)Cache.Clear();
                    Cache[key]=new Entry{Prefixes=baseline,ExpiresAt=Clock.ElapsedMilliseconds+ttl};
                }
            }
            return baseline.Contains(prefix);
        }
    }
    static class ServiceAvailability
    {
        // Public, read-only API routes. No credentials, browser profiles or generation requests.
        internal static readonly ServiceTarget[] Targets = {
            new ServiceTarget { Name = "Gemini", Endpoints = new[] { new ServiceEndpoint { Name = "Сайт", Host = "gemini.google.com", Path = "/" } } },
            Target("ChatGPT","chatgpt.com","api.openai.com","/v1/models"),
            Target("Claude","claude.ai","api.anthropic.com","/v1/models"),
            Target("ElevenLabs","elevenlabs.io","api.elevenlabs.io","/v1/user")
        };
        static ServiceTarget Target(string name,string web,string api,string path)
        {return new ServiceTarget{Name=name,Endpoints=new[]{new ServiceEndpoint{Name="Сайт",Host=web,Path="/"},new ServiceEndpoint{Name="API",Host=api,Path=path,Api=true}}};}
        internal static ServiceAvailabilityResult Check(int target,int provider,bool encrypted,IServiceAvailabilityProbe probe,CancellationToken cancel,int parallelism=2,EndpointAddressHealth health=null)
        {
            if(target<0||target>=Targets.Length)throw new ArgumentOutOfRangeException("target");
            return Check(Targets[target],provider,encrypted,probe,cancel,parallelism,health);
        }
        internal static ServiceAvailabilityResult Check(ServiceTarget target,int provider,bool encrypted,IServiceAvailabilityProbe probe,CancellationToken cancel,int parallelism=2,EndpointAddressHealth health=null)
        {
            var endpoints=target.Endpoints;
            var results=new ServiceEndpointResult[endpoints.Length];
            Parallel.For(0,endpoints.Length,new ParallelOptions{MaxDegreeOfParallelism=parallelism,CancellationToken=cancel},i=> {
                var endpoint=endpoints[i];var result=new ServiceEndpointResult{Endpoint=endpoint};var clock=Stopwatch.StartNew();
                var trace=new StringBuilder("Availability: "+target.Name+"; provider="+Dns.Name[provider]+"; host="+endpoint.Host+endpoint.Path+"; UTC="+DateTime.UtcNow.ToString("O")+"\n");
                string stage="DNS";
                try {
                    cancel.ThrowIfCancellationRequested();
                    var addresses=probe.Resolve(provider,encrypted,endpoint.Host,cancel).Where(AutoDnsProbe.PublicAddress).Distinct().Take(16).ToArray();
                    if(addresses.Length==0)throw new IOException("Нет публичного IPv4-адреса");
                    result.Dns=true;trace.AppendLine("DNS -> "+string.Join(", ",addresses));
                    Exception last=null;ServiceHttpReply reply=null;stage="соединение";long healthVersion=0;
                    var ready=health==null?addresses.Take(8).ToArray():health.Ready(endpoint.Host,addresses,out healthVersion);
                    trace.AppendLine("HTTPS plan="+string.Join(",",ready)+"; skipped="+string.Join(",",addresses.Except(ready)));
                    if(ready.Length==0){result.Suppressed=true;result.RegionSuppressed=health!=null&&health.OnlyRegionBlocked(endpoint.Host,addresses);throw new IOException("Все полученные IP временно исключены после предыдущих ошибок; повторите позже.");}
                    var directGuard=CustomDns.IsCustom(provider)?null:probe as IDirectAddressGuard;
                    bool sawDirect=false;string directAddress=null;
                    foreach(string address in ready) {
                        cancel.ThrowIfCancellationRequested();
                        if(directGuard!=null&&directGuard.IsDirectAddress(endpoint.Host,address,cancel)) {
                            sawDirect=true;if(directAddress==null)directAddress=address;
                            trace.AppendLine("Direct origin/CDN IP skipped: "+address+" (no Smart DNS substitution)");
                            continue;
                        }
                        try {
                            reply=probe.Request(endpoint,address,cancel);result.Address=address;result.Connection=true;result.Tls=true;trace.AppendLine("HTTPS @ "+address+"; HTTP="+reply.Status);
                            var classification=Classify(endpoint,reply);
                            if(classification==ServiceReply.Restricted){if(health!=null)health.RegionFailed(endpoint.Host,address,healthVersion);trace.AppendLine("Region refusal: this host/IP excluded for 5 min; trying another address from the same provider");continue;}
                            if(health!=null&&(classification==ServiceReply.Response||classification==ServiceReply.Login||classification==ServiceReply.BrowserCheck))health.Reachable(endpoint.Host,address,healthVersion);break;
                        }
                        catch(OperationCanceledException){throw;}
                        catch(Exception error){
                            last=error;var transport=error as ServiceTransportException;
                            stage=transport==null?"соединение":transport.Stage;
                            if(stage=="TLS"||stage=="HTTP")result.Connection=true;
                            if(stage=="HTTP")result.Tls=true;
                            if(health!=null&&(stage=="TLS"||stage=="соединение"))health.TransportFailed(endpoint.Host,address,healthVersion);
                            trace.AppendLine(stage+" @ "+address+": "+error.GetType().Name+"; "+(error.InnerException??error).Message);
                        }
                    }
                    if(reply==null) {
                        if(sawDirect&&last==null) {
                            result.Address=directAddress;result.Reply=ServiceReply.Direct;
                            trace.AppendLine("Classification=Direct; resolver returned unspoofed origin/CDN IP");
                        } else throw last??new IOException("Нет ответа HTTPS");
                    } else {
                        result.Status=reply.Status;result.Reply=Classify(endpoint,reply);
                        trace.AppendLine("Classification="+result.Reply+"; no authentication or generation attempted");
                    }
                }
                catch(OperationCanceledException){throw;}
                catch(Exception error){result.Error=result.Suppressed?"IP сервиса временно исключены после отказов":stage+": "+(stage=="DNS"?"нет ответа или ошибка адреса":stage=="TLS"?"защищённое соединение не подтверждено":stage=="HTTP"?"нет корректного ответа сервиса":"не удалось подключиться");trace.AppendLine(error.GetType().Name+": "+error.Message);}
                result.Milliseconds=(int)clock.ElapsedMilliseconds;trace.AppendLine("Endpoint total="+result.Milliseconds+" ms");result.Trace=trace.ToString();results[i]=result;
            });
            cancel.ThrowIfCancellationRequested();return new ServiceAvailabilityResult{Name=target.Name,Endpoints=results};
        }
        static void ErrorStrings(object value,List<string> output,int depth)
        {
            if(depth>6||value==null)return;
            var text=value as string;if(text!=null){output.Add(text);return;}
            var map=value as Dictionary<string,object>;
            if(map!=null)foreach(var pair in map) {
                if(new[]{"error","detail","details","message","code","status","reason","type"}.Contains(pair.Key))ErrorStrings(pair.Value,output,depth+1);
            }
            var list=value as object[];if(list!=null)foreach(var item in list)ErrorStrings(item,output,depth+1);
        }
        internal static ServiceReply Classify(ServiceEndpoint endpoint,ServiceHttpReply response)
        {
            if(response.Header("cf-mitigated").Equals("challenge",StringComparison.OrdinalIgnoreCase))return ServiceReply.BrowserCheck;
            string error="";
            if(endpoint.Api&&response.Header("Content-Type").IndexOf("json",StringComparison.OrdinalIgnoreCase)>=0&&!string.IsNullOrEmpty(response.Body)) {
                try {var strings=new List<string>();ErrorStrings(new JavaScriptSerializer{MaxJsonLength=65536}.DeserializeObject(response.Body),strings,0);error=string.Join(" ",strings).ToLowerInvariant();}catch(ArgumentException){}catch(InvalidOperationException){}
            }
            // Only explicit API error fields qualify. A marketing page mentioning a country is not a block.
            if(new[]{"unsupported_country","unsupported_region","country_not_supported","region_not_supported","location is not supported","location is not allowed","not available in your country","not available in your region","access from your region is not supported"}.Any(error.Contains))return ServiceReply.Restricted;
            if(response.Status==429)return ServiceReply.RateLimited;
            if(response.Status>=500)return ServiceReply.ServerError;
            string location=response.Header("Location");
            Uri redirect;bool login=Uri.TryCreate(new Uri("https://"+endpoint.Host),location,out redirect)&&
                (redirect.Host=="accounts.google.com"||redirect.AbsolutePath.IndexOf("login",StringComparison.OrdinalIgnoreCase)>=0||redirect.AbsolutePath.IndexOf("signin",StringComparison.OrdinalIgnoreCase)>=0);
            if(response.Status>=300&&response.Status<400)return login?ServiceReply.Login:ServiceReply.Unknown;
            if(response.Status==401)return ServiceReply.Login;
            if(endpoint.Api&&(response.Status==400||response.Status==403)&&new[]{"api key","api_key","api-key","unauthenticated","authentication","missing bearer","not authenticated","unregistered callers"}.Any(error.Contains))return ServiceReply.Login;
            if(response.Status>=200&&response.Status<300) {
                // An HTML 200 at an API route can be a substituted landing/challenge page.
                if(endpoint.Api) {
                    if(response.Header("Content-Type").IndexOf("json",StringComparison.OrdinalIgnoreCase)<0||string.IsNullOrWhiteSpace(response.Body)||error.Length>0)return ServiceReply.Unknown;
                    try {var body=new JavaScriptSerializer{MaxJsonLength=65536}.DeserializeObject(response.Body) as Dictionary<string,object>;
                        if(body==null||!(body.ContainsKey("models")||body.ContainsKey("data")||body.ContainsKey("user_id")))return ServiceReply.Unknown;
                    }catch(ArgumentException){return ServiceReply.Unknown;}catch(InvalidOperationException){return ServiceReply.Unknown;}
                }
                return ServiceReply.Response;
            }
            return ServiceReply.Unknown;
        }
    }
    sealed class ServiceAvailabilityProbe : IServiceAvailabilityProbe, IDirectAddressGuard
    {
        readonly AutoDnsProbe dns;
        internal ServiceAvailabilityProbe(bool tls){dns=new AutoDnsProbe(tls);}
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel){return dns.Resolve(provider,encrypted,host,cancel);}
        public bool IsDirectAddress(string host,string address,CancellationToken cancel){return DirectDnsGuard.IsDirect(host,address,cancel);}
        public ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel)
        {
            if(!AutoDnsProbe.PublicAddress(address))throw new IOException("Некорректный публичный адрес");
            string stage="соединение";
            try {
                using(var tcp=new TcpClient(AddressFamily.InterNetwork))
                using(var deadline=new Timer(_=>tcp.Close(),null,12000,Timeout.Infinite))
                using(cancel.Register(()=>tcp.Close())) {
                    var pending=tcp.BeginConnect(IPAddress.Parse(address),443,null,null);
                    using(var wait=pending.AsyncWaitHandle){if(!wait.WaitOne(3500))throw new TimeoutException("Время подключения истекло");tcp.EndConnect(pending);}
                    cancel.ThrowIfCancellationRequested();stage="TLS";
                    using(var ssl=new SslStream(tcp.GetStream(),false)) {
                        ssl.ReadTimeout=3500;ssl.WriteTimeout=3500;
                        var auth=ssl.BeginAuthenticateAsClient(endpoint.Host,null,SslProtocols.Tls12,true,null,null);
                        using(var wait=auth.AsyncWaitHandle){if(!wait.WaitOne(3500))throw new TimeoutException("Время TLS истекло");ssl.EndAuthenticateAsClient(auth);}
                        cancel.ThrowIfCancellationRequested();stage="HTTP";
                        string key=endpoint.WebSocket?Convert.ToBase64String(Guid.NewGuid().ToByteArray()):null;
                        byte[] request=Encoding.ASCII.GetBytes("GET "+endpoint.Path+" HTTP/1.1\r\nHost: "+endpoint.Host+"\r\nUser-Agent: KodVsego-SmartDNS\r\nAccept: "+(endpoint.Api?"application/json":"text/html")+"\r\nAccept-Encoding: identity\r\n"+(endpoint.Host=="api.anthropic.com"?"anthropic-version: 2023-06-01\r\n":"")+(endpoint.WebSocket?"Connection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: "+key+"\r\n":"Connection: close\r\n")+"\r\n");
                        ssl.Write(request,0,request.Length);
                        var reply=ReadResponse(ssl,endpoint.Api||endpoint.InspectHtml,cancel,endpoint.WebSocket,endpoint.InspectHtml);
                        if(reply.Status==101)ValidateUpgrade(reply,key);
                        return reply;
                    }
                }
            }catch(OperationCanceledException){throw;}catch(Exception error){cancel.ThrowIfCancellationRequested();throw new ServiceTransportException(stage,error);}
        }
        static string Line(Stream input,CancellationToken cancel,ref int budget)
        {
            var line=new StringBuilder();while(budget-->0){cancel.ThrowIfCancellationRequested();int c=input.ReadByte();if(c<0)throw new IOException("Неполный HTTP-ответ");if(c=='\n'){if(line.Length==0||line[line.Length-1]!='\r')throw new IOException("Некорректный HTTP-ответ");return line.ToString(0,line.Length-1);}line.Append((char)c);}throw new IOException("Слишком большой заголовок HTTP");
        }
        internal static void ValidateUpgrade(ServiceHttpReply reply,string key)
        {
            string expected;using(var sha=SHA1.Create())expected=Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            if(!reply.Header("Upgrade").Equals("websocket",StringComparison.OrdinalIgnoreCase)||!reply.Header("Connection").Split(',').Any(t=>t.Trim().Equals("upgrade",StringComparison.OrdinalIgnoreCase))||reply.Header("Sec-WebSocket-Accept")!=expected)throw new IOException("Некорректное подтверждение WebSocket.");
        }
        internal static ServiceHttpReply ReadResponse(Stream input,bool api,CancellationToken cancel,bool websocket=false,bool inspectHtml=false)
        {
            var result=new ServiceHttpReply();int budget=65536;var first=Line(input,cancel,ref budget).Split(' ');int status;
            if(first.Length<2||!(first[0]=="HTTP/1.1"||first[0]=="HTTP/1.0")||!int.TryParse(first[1],out status)||(status<200&&!(websocket&&status==101))||status>599)throw new IOException("Некорректный статус HTTP");
            result.Status=status;string line;
            while((line=Line(input,cancel,ref budget))!=""){
                int colon=line.IndexOf(':');if(colon<=0)throw new IOException("Некорректный заголовок HTTP");
                string key=line.Substring(0,colon).Trim();
                if(result.Headers.ContainsKey(key)){if(new[]{"Content-Length","Transfer-Encoding","Content-Encoding","Location","cf-mitigated","Upgrade","Sec-WebSocket-Accept"}.Contains(key,StringComparer.OrdinalIgnoreCase))throw new IOException("Повторный заголовок HTTP");continue;}
                result.Headers[key]=line.Substring(colon+1).Trim();
            }
            // Websites need no page download. Only bounded JSON API errors are inspected; bodies are never logged.
            if(status==101||!api||(!inspectHtml&&result.Header("Content-Type").IndexOf("json",StringComparison.OrdinalIgnoreCase)<0)||(!inspectHtml&&result.Header("cf-mitigated")=="challenge"))return result;
            string encoding=result.Header("Content-Encoding");if(encoding!=""&&!encoding.Equals("identity",StringComparison.OrdinalIgnoreCase))return result;
            string transfer=result.Header("Transfer-Encoding"),lengthText=result.Header("Content-Length");long length=-1;
            if(transfer!=""&&(!transfer.Equals("chunked",StringComparison.OrdinalIgnoreCase)||lengthText!=""))throw new IOException("Неоднозначная длина HTTP");
            if(lengthText!=""&&(!long.TryParse(lengthText,out length)||length<0))throw new IOException("Некорректная длина HTTP");
            using(var body=new MemoryStream()) {
                var buffer=new byte[4096];bool chunked=transfer!="";
                while(body.Length<32768) {
                    long remaining=length;
                    if(chunked){budget=128;string chunk=Line(input,cancel,ref budget).Split(';')[0];if(!long.TryParse(chunk,System.Globalization.NumberStyles.AllowHexSpecifier,System.Globalization.CultureInfo.InvariantCulture,out remaining)||remaining<0)throw new IOException("Некорректный блок HTTP");if(remaining==0)break;}
                    while(remaining!=0&&body.Length<32768) {
                        cancel.ThrowIfCancellationRequested();int wanted=(int)Math.Min(buffer.Length,32768-body.Length);if(remaining>0)wanted=(int)Math.Min(wanted,remaining);
                        int count=input.Read(buffer,0,wanted);if(count==0){if(remaining>0)throw new IOException("Неполное тело HTTP");remaining=0;break;}body.Write(buffer,0,count);if(remaining>0)remaining-=count;
                    }
                    if(!chunked||body.Length>=32768)break;
                    budget=2;if(Line(input,cancel,ref budget)!="")throw new IOException("Некорректный конец блока HTTP");
                }
                result.Body=Encoding.UTF8.GetString(body.ToArray());return result;
            }
        }
    }
}
