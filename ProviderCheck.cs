using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HbAi.XboxDns
{
    enum ProviderCheckState { Inconclusive, Confirmed, NotConfirmed, ProbePassed, NetworkChanged }
    sealed class ProviderCheckReply { internal int Status; internal string Body; }
    sealed class ProviderCheckResult
    {
        internal ProviderCheckState State;
        internal string Provider, Source, Protocol, Detail;
        internal DateTime CheckedUtc=DateTime.UtcNow;
        internal bool Positive {get{return State==ProviderCheckState.Confirmed||State==ProviderCheckState.ProbePassed;}}
        internal string Summary {get{return State==ProviderCheckState.Confirmed?"DNS: чекер подтвердил":State==ProviderCheckState.NotConfirmed?"DNS: чекер не подтвердил":State==ProviderCheckState.ProbePassed?"DNS: Malw отвечает":State==ProviderCheckState.NetworkChanged?"DNS: сеть изменилась · повторите":"DNS: не удалось проверить";}}
        internal string Report {get{return Summary+"\n\nВыбранный провайдер: "+Provider+"\nОфициальная проверка: "+Source+"\nВремя UTC: "+CheckedUtc.ToString("yyyy-MM-dd HH:mm:ss")+
            (string.IsNullOrEmpty(Protocol)?"":"\nПротокол по данным чекера: "+Protocol)+"\n\n"+
            (State==ProviderCheckState.Confirmed?"Официальный чекер подтвердил подключение через свою инфраструктуру для текущего системного пути приложения.":
            State==ProviderCheckState.NotConfirmed?"Чекер вернул отрицательный результат. Использование выбранного DNS этим запросом не подтверждено.":
            State==ProviderCheckState.ProbePassed?"Проверочный адрес Malw ответил. У обычного Malw DNS и Malw CF (через Cloudflare) один чекер: он не различает эти два варианта. Поэтому этот результат не подтверждает, какой из них использует Windows.":
            State==ProviderCheckState.NetworkChanged?"Во время проверки изменились сеть или DNS. Повторите проверку текущего подключения.":"Не получен однозначный ответ чекера. Это не означает, что DNS не настроен или не работает.")+
            "\n\nЗапрос выполнен через текущий системный DNS Windows. Проверяемый DNS принудительно не подставлялся. Собственный DoH браузера, отдельный маршрут Antigravity, вход и ответ модели не проверялись. Настройки сети не изменялись.";}}
    }
    interface IProviderCheckIo
    {
        string Stamp();
        ProviderCheckReply Get(string url,CancellationToken cancel);
        void Lookup(string host,CancellationToken cancel);
        void Pause(CancellationToken cancel);
    }
    static class ProviderCheck
    {
        internal static bool Available(int provider)
        {return Dns.IsAvailable(provider)&&(provider==0||provider==5||provider==7||provider==9||provider==10||provider==11||provider==13);}
        static string Source(int provider)
        {return provider==0?"comss.ru/page.php?id=7315":provider==5?"dns-ai.ru/ip":provider==7?"dns.zapret.moe":provider==10?"site.dns.wlrus.cfd":provider==11?"dns.webshiftvpn.ru":"info.dns.malw.link";}
        internal static ProviderCheckResult Run(int provider,IProviderCheckIo io,CancellationToken cancel)
        {
            if(!Available(provider))throw new ArgumentOutOfRangeException("provider");
            var result=new ProviderCheckResult{Provider=Dns.Name[provider],Source=Source(provider)};
            var clock=Stopwatch.StartNew();var trace=new StringBuilder("Official provider check; provider="+provider+"; UTC="+DateTime.UtcNow.ToString("O")+"; path=system; proxy=none\n");string before=null;
            try {
                cancel.ThrowIfCancellationRequested();before=io.Stamp();string nonce=Nonce();
                if(provider==0) {
                    // Exact marker used by the official page, through Windows DNS only.
                    // A timeout or an unexpected body is unknown, never proof of misconfiguration.
                    var reply=Get(io,"https://check.comss.one/?c="+nonce,cancel,trace);
                    if(reply.Body==null||reply.Body.Length>32768||reply.Body.Trim()!="Comss.one DNS enabled")throw new InvalidDataException();
                    result.State=ProviderCheckState.Confirmed;
                } else if(provider==10) {
                    var data=Json(Get(io,"https://probe.dns.wlrus.cfd/whoami?c="+nonce,cancel,trace));object value;
                    if(!data.TryGetValue("via",out value)||!(value is string))throw new InvalidDataException();
                    if((string)value=="ours")result.State=ProviderCheckState.Confirmed;
                    else if((string)value=="public")result.State=ProviderCheckState.NotConfirmed;
                    else throw new InvalidDataException();
                } else if(provider==11) {
                    string stamp=((long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    result.State=Boolean(Json(Get(io,"https://dns.webshiftvpn.ru/api/dns-check?t="+stamp,cancel,trace)),"connected")?ProviderCheckState.Confirmed:ProviderCheckState.NotConfirmed;
                } else if(provider==7) {
                    // Fresh Windows lookup; never use the tested provider's direct DoH for this probe.
                    bool lookupTimedOut=false;
                    try {io.Lookup(nonce+".check.dns.zapret.moe",cancel);trace.AppendLine("Fresh system lookup completed");}
                    catch(OperationCanceledException){throw;}
                    catch(TimeoutException){lookupTimedOut=true;trace.AppendLine("Fresh system lookup: deadline reached");}
                    catch(Exception error){trace.AppendLine("Fresh system lookup: "+error.GetType().Name);}
                    for(int attempt=0;attempt<4;attempt++) {
                        cancel.ThrowIfCancellationRequested();io.Pause(cancel);
                        var data=Json(Get(io,"https://dns.zapret.moe/api/dns-check?token="+nonce,cancel,trace));
                        if(Boolean(data,"seen")) {
                            result.State=ProviderCheckState.Confirmed;object protocol;
                            if(data.TryGetValue("protocol",out protocol)&&protocol is string)result.Protocol=Protocol((string)protocol);break;
                        }
                        if(attempt==3)result.State=lookupTimedOut?ProviderCheckState.Inconclusive:ProviderCheckState.NotConfirmed;
                    }
                } else if(provider==5) {
                    Get(io,"https://dns-ai.ru/dnscheck?c="+nonce,cancel,trace,true);
                    Get(io,"https://check.dns-ai.ru/dnscheck?c="+nonce,cancel,trace,true);
                    result.State=ProviderCheckState.Confirmed;
                } else {
                    Get(io,"https://test.dns.malw.link/?c="+nonce,cancel,trace,true);
                    result.State=ProviderCheckState.ProbePassed;
                }
            }
            catch(OperationCanceledException){throw;}
            catch(Exception error){result.State=ProviderCheckState.Inconclusive;trace.AppendLine("Checker unavailable or invalid: "+error.GetType().Name);}
            cancel.ThrowIfCancellationRequested();
            try {if(before==null)result.State=ProviderCheckState.Inconclusive;else if(before!=io.Stamp())result.State=ProviderCheckState.NetworkChanged;}
            catch(Exception error){result.State=ProviderCheckState.Inconclusive;trace.AppendLine("Network snapshot unavailable: "+error.GetType().Name);}
            trace.AppendLine("Result="+result.State+"; elapsed="+clock.ElapsedMilliseconds+" ms; no network settings changed");result.Detail=trace.ToString();return result;
        }
        static string Protocol(string value)
        {value=value.ToUpperInvariant();return value.Contains("HTTPS")?"DoH":value.Contains("TLS")?"DoT":value=="UDP"||value=="TCP"?value:"Не указан";}
        static ProviderCheckReply Get(IProviderCheckIo io,string url,CancellationToken cancel,StringBuilder trace,bool probe=false)
        {
            cancel.ThrowIfCancellationRequested();var watch=Stopwatch.StartNew();var uri=new Uri(url);
            // Exclude query IDs, response bodies, IPs from whoami and exception messages.
            trace.AppendLine("HTTPS host="+uri.Host+"; path="+uri.AbsolutePath);var reply=io.Get(url,cancel);
            trace.AppendLine("HTTP="+reply.Status+"; elapsed="+watch.ElapsedMilliseconds+" ms");
            if(reply.Status!=200&&!(probe&&reply.Status==204))throw new IOException("Unexpected checker HTTP status");return reply;
        }
        static IDictionary<string,object> Json(ProviderCheckReply reply)
        {
            if(string.IsNullOrEmpty(reply.Body)||reply.Body.Length>32768)throw new InvalidDataException();
            var data=new JavaScriptSerializer{MaxJsonLength=32768,RecursionLimit=12}.DeserializeObject(reply.Body) as IDictionary<string,object>;
            if(data==null)throw new InvalidDataException();return data;
        }
        static bool Boolean(IDictionary<string,object> data,string key)
        {object value;if(!data.TryGetValue(key,out value)||!(value is bool))throw new InvalidDataException();return (bool)value;}
        static string Nonce()
        {var bytes=new byte[8];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
    }
    sealed class ProviderCheckIo : IProviderCheckIo
    {
        public string Stamp(){return new MangoResolverCheckIo().Stamp();}
        internal static bool Allowed(string url)
        {
            Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo!=""||uri.Fragment!="")return false;
            string path=uri.AbsolutePath;
            return uri.Host=="check.comss.one"&&path=="/"||uri.Host=="probe.dns.wlrus.cfd"&&path=="/whoami"||uri.Host=="dns.webshiftvpn.ru"&&path=="/api/dns-check"||
                uri.Host=="dns.zapret.moe"&&path=="/api/dns-check"||(uri.Host=="dns-ai.ru"||uri.Host=="check.dns-ai.ru")&&path=="/dnscheck"||uri.Host=="test.dns.malw.link"&&path=="/";
        }
        public ProviderCheckReply Get(string url,CancellationToken cancel)
        {
            if(!Allowed(url))throw new IOException("Unexpected checker URL");cancel.ThrowIfCancellationRequested();var uri=new Uri(url);
            // Resolve through Windows on each request. A pooled HttpWebRequest ServicePoint may
            // retain its old destination after DNS was changed, even after the OS cache is flushed.
            var resolve=Task.Factory.FromAsync<string,IPAddress[]>(System.Net.Dns.BeginGetHostAddresses,System.Net.Dns.EndGetHostAddresses,uri.Host,null);
            if(!resolve.Wait(2500,cancel))throw new TimeoutException();Exception last=null;int attempts=0;
            foreach(var address in resolve.Result) {
                if(IPAddress.IsLoopback(address)||address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||address.IsIPv6LinkLocal||address.IsIPv6Multicast)continue;
                if(address.AddressFamily==AddressFamily.InterNetwork&&!AutoDnsProbe.PublicAddress(address.ToString())||address.AddressFamily==AddressFamily.InterNetworkV6&&(address.GetAddressBytes()[0]&0xfe)==0xfc)continue;
                if(attempts++>=2)break;
                cancel.ThrowIfCancellationRequested();
                try {
                    using(var tcp=new TcpClient(address.AddressFamily))using(cancel.Register(()=>tcp.Close())) {
                        var connect=Task.Factory.FromAsync<IPAddress,int>(tcp.BeginConnect,tcp.EndConnect,address,443,null);
                        if(!connect.Wait(3500,cancel))throw new TimeoutException();
                        using(var ssl=new SslStream(tcp.GetStream(),false)) {
                            ssl.ReadTimeout=5000;ssl.WriteTimeout=5000;
                            var auth=Task.Factory.FromAsync((callback,state)=>ssl.BeginAuthenticateAsClient(uri.Host,null,SslProtocols.Tls12,true,callback,state),ssl.EndAuthenticateAsClient,null);
                            if(!auth.Wait(5000,cancel))throw new TimeoutException();
                            byte[] request=Encoding.ASCII.GetBytes("GET "+uri.PathAndQuery+" HTTP/1.1\r\nHost: "+uri.Host+"\r\nUser-Agent: KodVsego-SmartDNS-Provider-Check\r\nAccept: application/json, */*\r\nAccept-Encoding: identity\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
                            ssl.Write(request,0,request.Length);
                            using(var output=new MemoryStream()) {
                                string redirect;int status=AppUpdate.ReadResponse(ssl,output,32768,cancel,null,out redirect);
                                return new ProviderCheckReply{Status=status,Body=Encoding.UTF8.GetString(output.ToArray())};
                            }
                        }
                    }
                }catch(OperationCanceledException){throw;}
                catch(Exception error){cancel.ThrowIfCancellationRequested();last=error;}
            }
            throw new IOException("System-path checker connection failed",last);
        }
        public void Lookup(string host,CancellationToken cancel)
        {
            if(!Regex.IsMatch(host??"",@"\A[a-f0-9]{16}\.check\.dns\.zapret\.moe\z"))throw new IOException("Unexpected check name");cancel.ThrowIfCancellationRequested();
            var task=Task.Factory.FromAsync<string,IPAddress[]>(System.Net.Dns.BeginGetHostAddresses,System.Net.Dns.EndGetHostAddresses,host,null);
            if(!task.Wait(2500,cancel))throw new TimeoutException();
        }
        public void Pause(CancellationToken cancel){if(cancel.WaitHandle.WaitOne(700))cancel.ThrowIfCancellationRequested();}
    }
}
