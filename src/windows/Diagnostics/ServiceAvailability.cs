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
    enum ServiceReply { Response, Login, BrowserCheck, Restricted, RateLimited, ServerError, Unknown, Failed }
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
        internal bool Dns, Connection, Tls;
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
        internal string Summary {get{return string.Join("\n",Endpoints.Select(e=>e.Endpoint.Name+": "+e.Caption))+"\n\nБез ключа и входа. Генерация не проверялась.";}}
        internal string Report {get{return string.Join("\n\n",Endpoints.Select(e=>e.Endpoint.Name+" · "+e.Endpoint.Host+e.Endpoint.Path+"\n"+e.Caption+
            "\nDNS: "+(e.Dns?"ответ есть":"нет ответа")+" · соединение: "+(e.Connection?"есть":"не установлено")+" · TLS: "+(e.Tls?"сертификат проверен":"не подтверждён")+
            (e.Status>0?" · HTTP "+e.Status:"")+" · "+e.Milliseconds+" мс"))+
            "\n\nВ этой проверке ключ не передавался. Ответ API 401/403 с запросом авторизации не означает отказ сохранённого ключа.\nАдреса получены от выбранного DNS; HTTPS проверяется с исходным именем и сертификатом. Системные настройки не менялись. DNS браузера, аккаунт и генерация не проверялись. API и сайт могут иметь разные ограничения.";}}
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
    static class ServiceAvailability
    {
        // Public, read-only API routes. No credentials, browser profiles or generation requests.
        internal static readonly ServiceTarget[] Targets = {
            Target("Gemini","gemini.google.com","generativelanguage.googleapis.com","/v1beta/models"),
            Target("ChatGPT","chatgpt.com","api.openai.com","/v1/models"),
            Target("Claude","claude.ai","api.anthropic.com","/v1/models"),
            Target("ElevenLabs","elevenlabs.io","api.elevenlabs.io","/v1/user")
        };
        static ServiceTarget Target(string name,string web,string api,string path)
        {return new ServiceTarget{Name=name,Endpoints=new[]{new ServiceEndpoint{Name="Сайт",Host=web,Path="/"},new ServiceEndpoint{Name="API",Host=api,Path=path,Api=true}}};}
        internal static ServiceAvailabilityResult Check(int target,int provider,bool encrypted,IServiceAvailabilityProbe probe,CancellationToken cancel,int parallelism=2)
        {
            if(target<0||target>=Targets.Length)throw new ArgumentOutOfRangeException("target");
            var endpoints=Targets[target].Endpoints;
            var results=new ServiceEndpointResult[endpoints.Length];
            Parallel.For(0,endpoints.Length,new ParallelOptions{MaxDegreeOfParallelism=parallelism,CancellationToken=cancel},i=> {
                var endpoint=endpoints[i];var result=new ServiceEndpointResult{Endpoint=endpoint};var clock=Stopwatch.StartNew();
                var trace=new StringBuilder("Availability: "+Targets[target].Name+"; provider="+Dns.Name[provider]+"; host="+endpoint.Host+endpoint.Path+"; UTC="+DateTime.UtcNow.ToString("O")+"\n");
                string stage="DNS";
                try {
                    cancel.ThrowIfCancellationRequested();
                    var addresses=probe.Resolve(provider,encrypted,endpoint.Host,cancel).Where(AutoDnsProbe.PublicAddress).Distinct().Take(3).ToArray();
                    if(addresses.Length==0)throw new IOException("Нет публичного IPv4-адреса");
                    result.Dns=true;trace.AppendLine("DNS -> "+string.Join(", ",addresses));
                    Exception last=null;ServiceHttpReply reply=null;stage="соединение";
                    foreach(string address in addresses) {
                        cancel.ThrowIfCancellationRequested();
                        try {reply=probe.Request(endpoint,address,cancel);result.Connection=true;result.Tls=true;trace.AppendLine("HTTPS @ "+address+"; HTTP="+reply.Status);break;}
                        catch(OperationCanceledException){throw;}
                        catch(Exception error){
                            last=error;var transport=error as ServiceTransportException;
                            stage=transport==null?"соединение":transport.Stage;
                            if(stage=="TLS"||stage=="HTTP")result.Connection=true;
                            if(stage=="HTTP")result.Tls=true;
                            trace.AppendLine(stage+" @ "+address+": "+error.GetType().Name+"; "+(error.InnerException??error).Message);
                        }
                    }
                    if(reply==null)throw last??new IOException("Нет ответа HTTPS");
                    result.Status=reply.Status;result.Reply=Classify(endpoint,reply);
                    trace.AppendLine("Classification="+result.Reply+"; no authentication or generation attempted");
                }
                catch(OperationCanceledException){throw;}
                catch(Exception error){result.Error=stage+": "+(stage=="DNS"?"нет ответа или ошибка адреса":stage=="TLS"?"защищённое соединение не подтверждено":stage=="HTTP"?"нет корректного ответа сервиса":"не удалось подключиться");trace.AppendLine(error.GetType().Name+": "+error.Message);}
                result.Milliseconds=(int)clock.ElapsedMilliseconds;trace.AppendLine("Endpoint total="+result.Milliseconds+" ms");result.Trace=trace.ToString();results[i]=result;
            });
            cancel.ThrowIfCancellationRequested();return new ServiceAvailabilityResult{Name=Targets[target].Name,Endpoints=results};
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
            if(endpoint.Api&&(response.Status==400||response.Status==403)&&new[]{"api key","api_key","api-key","unauthenticated","authentication","missing bearer","not authenticated"}.Any(error.Contains))return ServiceReply.Login;
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
    sealed class ServiceAvailabilityProbe : IServiceAvailabilityProbe
    {
        readonly AutoDnsProbe dns;
        internal ServiceAvailabilityProbe(bool tls){dns=new AutoDnsProbe(tls);}
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel){return dns.Resolve(provider,encrypted,host,cancel);}
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
