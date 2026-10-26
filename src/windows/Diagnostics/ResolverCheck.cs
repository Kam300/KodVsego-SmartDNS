using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HbAi.XboxDns
{
    sealed class ResolverNode
    {
        internal string Address, Network;
        public override string ToString() { return Address+(string.IsNullOrEmpty(Network)?"":" · "+Network); }
    }
    sealed class ResolverTicket { internal string Id, Nonce; }
    interface IResolverCheckIo
    {
        string Stamp();
        ResolverTicket Create(CancellationToken cancel);
        void Lookup(string host,bool system,int provider,DnsTransport protocol,CancellationToken cancel);
        ResolverNode[] Collect(ResolverTicket ticket,CancellationToken cancel);
        void Pause(CancellationToken cancel);
    }
    enum ResolverComparison { Incomplete, Shared, DifferentNodes, NetworkChanged }
    sealed class ResolverCheckResult
    {
        internal ResolverComparison Comparison;
        internal ProviderCheckResult Official;
        internal ResolverNode[] SystemNodes=new ResolverNode[0], ProviderNodes=new ResolverNode[0];
        internal string Detail, Provider, Protocol;
        internal string Summary { get { return Official!=null?Official.Summary:Comparison==ResolverComparison.Shared||Comparison==ResolverComparison.DifferentNodes?"DNS: провайдер не подтверждён":Comparison==ResolverComparison.NetworkChanged?"DNS: сеть изменилась · повторите":"DNS: проверка не завершена"; } }
        internal string Report { get { return Official!=null?Official.Report:Summary+"\n\nСистемный путь Windows:\n"+Nodes(SystemNodes)+"\n\nКонтроль: "+Provider+" · "+Protocol+"\n"+Nodes(ProviderNodes)+
            "\n\nНаблюдатель: MangoProxy / bash.ws. Уникальные тестовые имена обходят старый кэш этих имён. " +
            "Сравниваются адреса рекурсивных DNS-узлов, которые увидел внешний сервис, а не только настройки адаптера. " +
            "Адрес узла может отличаться от IP, введённого в настройках DNS.\n\n"+
            (Comparison==ResolverComparison.Shared?"В обеих выборках есть общий узел. Совпадение не подтверждает выбранного провайдера и не доказывает отсутствие перехвата: обычные DNS-запросы могут перенаправляться и в контрольном пути.":
            Comparison==ResolverComparison.DifferentNodes?"Общих узлов в этих выборках нет. Возможны другой DNS, VPN, пересылка запросов или разные узлы пула провайдера. Перехват и утечка не доказаны.":
            Comparison==ResolverComparison.NetworkChanged?"Во время проверки изменилось подключение или DNS. Результат не относится к текущей сети.":"Не удалось получить обе выборки. Недоступность тестового сервиса не означает, что ваш DNS не работает.")+
            "\n\nПроверяется системный путь приложения. Собственный DoH браузера, доступ в аккаунт и ответы ИИ не проверялись. Настройки сети не менялись."; } }
        static string Nodes(ResolverNode[] nodes) { return nodes.Length==0?"Нет данных":string.Join("\n",nodes.Select(n=>n.ToString())); }
    }
    static class ResolverCheck
    {
        internal static int ServiceIndex { get { return ServiceAvailability.Targets.Length; } }
        internal static ResolverCheckResult Run(int provider,DnsTransport protocol,IResolverCheckIo io,CancellationToken cancel)
        {
            var result=new ResolverCheckResult{Provider=Dns.Name[provider],Protocol=protocol.ToString()};
            var trace=new StringBuilder("Resolver observation; provider="+result.Provider+"; protocol="+protocol+"; source=MangoProxy/bash.ws; UTC="+DateTime.UtcNow.ToString("O")+"\n");
            string stamp=io.Stamp();
            try {
                // Separate tickets prevent the control lookups from contaminating the system sample.
                result.SystemNodes=Sample(true,provider,protocol,io,cancel,trace);
                result.ProviderNodes=Sample(false,provider,protocol,io,cancel,trace);
                cancel.ThrowIfCancellationRequested();
                result.Comparison=Compare(result.SystemNodes,result.ProviderNodes);
            }
            catch(OperationCanceledException){throw;}
            catch(Exception problem){result.Comparison=ResolverComparison.Incomplete;trace.AppendLine("Observer unavailable: "+problem.GetType().Name);}
            if(stamp!=io.Stamp())result.Comparison=ResolverComparison.NetworkChanged;
            trace.AppendLine("Comparison="+result.Comparison);result.Detail=trace.ToString();return result;
        }
        internal static ResolverComparison Compare(ResolverNode[] system,ResolverNode[] control)
        {
            if(system.Length==0||control.Length==0)return ResolverComparison.Incomplete;
            return system.Any(a=>control.Any(b=>a.Address==b.Address))?ResolverComparison.Shared:ResolverComparison.DifferentNodes;
        }
        static ResolverNode[] Sample(bool system,int provider,DnsTransport protocol,IResolverCheckIo io,CancellationToken cancel,StringBuilder trace)
        {
            cancel.ThrowIfCancellationRequested();var ticket=io.Create(cancel);ValidateTicket(ticket);
            int errors=0;
            Parallel.For(1,7,new ParallelOptions{MaxDegreeOfParallelism=2,CancellationToken=cancel},i=>{
                try{io.Lookup(i+"."+ticket.Id+".bash.ws",system,provider,protocol,cancel);}
                catch(OperationCanceledException){throw;}
                catch(Exception){Interlocked.Increment(ref errors);} // NXDOMAIN can still yield an authoritative observation.
            });
            var nodes=io.Collect(ticket,cancel);
            if(nodes.Length==0){io.Pause(cancel);nodes=io.Collect(ticket,cancel);}
            trace.AppendLine((system?"System":"Control")+" sample; queries=6; lookup errors="+errors+"; nodes="+string.Join(", ",nodes.Select(n=>n.ToString())));
            return nodes;
        }
        internal static void ValidateTicket(ResolverTicket ticket)
        {
            if(ticket==null||!Regex.IsMatch(ticket.Id??"",@"\A[a-z0-9]{10,32}\z")||!Regex.IsMatch(ticket.Nonce??"",@"\A[a-zA-Z0-9]{1,64}\z"))throw new IOException("Некорректный ответ наблюдателя DNS.");
        }
    }
    sealed class MangoResolverCheckIo : IResolverCheckIo
    {
        const string Endpoint="https://mangoproxy.ru/wp-admin/admin-ajax.php";
        public string Stamp()
        {
            return string.Join("|",NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up).OrderBy(n=>n.Id).Select(n=>
                n.Id+":"+string.Join(",",n.GetIPProperties().UnicastAddresses.Select(a=>a.Address.ToString()).OrderBy(a=>a))+":"+string.Join(",",n.GetIPProperties().DnsAddresses.Select(a=>a.ToString()))));
        }
        public ResolverTicket Create(CancellationToken cancel)
        {
            var visitor=Post("action=dns_leak_visitor",cancel);
            string nonce=Text(visitor,"nonce");
            if(!Regex.IsMatch(nonce,@"\A[a-zA-Z0-9]{1,64}\z"))throw new IOException("Наблюдатель DNS не вернул nonce.");
            var start=Post("action=dns_leak_start&_ajax_nonce="+nonce,cancel);
            var ticket=new ResolverTicket{Id=Text(start,"id"),Nonce=nonce};ResolverCheck.ValidateTicket(ticket);return ticket;
        }
        internal static string Text(IDictionary<string,object> map,string key)
        { object value;return map!=null&&map.TryGetValue(key,out value)&&value is string?(string)value:""; }
        public ResolverNode[] Collect(ResolverTicket ticket,CancellationToken cancel)
        {
            ResolverCheck.ValidateTicket(ticket);
            var data=Post("action=dns_leak_check&token="+ticket.Id+"&_ajax_nonce="+ticket.Nonce,cancel);
            return ParseNodes(data);
        }
        internal static ResolverNode[] ParseNodes(IDictionary<string,object> data)
        {
            object raw;var nodes=new List<ResolverNode>();
            if(!data.TryGetValue("servers",out raw)||!(raw is object[]))throw new IOException("Неверный список DNS-узлов.");
            var items=(object[])raw;if(items.Length>64)throw new IOException("Слишком много DNS-узлов.");
            foreach(var item in items){var map=item as IDictionary<string,object>;IPAddress ip;
                if(map==null||!IPAddress.TryParse(Text(map,"ip"),out ip)||!Public(ip))throw new IOException("Неверный адрес DNS-узла.");
                string network=Text(map,"asn");network=new string(network.Where(c=>!char.IsControl(c)).Take(160).ToArray());
                nodes.Add(new ResolverNode{Address=ip.ToString(),Network=network});
            }
            return nodes.GroupBy(n=>n.Address).Select(g=>g.First()).ToArray();
        }
        static bool Public(IPAddress ip)
        {if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();return ip.AddressFamily==AddressFamily.InterNetwork?AutoDnsProbe.PublicAddress(ip.ToString()):!ip.Equals(IPAddress.IPv6None)&&!ip.Equals(IPAddress.IPv6Loopback)&&!ip.IsIPv6LinkLocal&&!ip.IsIPv6Multicast&&(ip.GetAddressBytes()[0]&0xfe)!=0xfc;}
        static IDictionary<string,object> Post(string body,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create(Endpoint);request.Method="POST";request.ContentType="application/x-www-form-urlencoded";
            request.Timeout=6000;request.ReadWriteTimeout=6000;request.AllowAutoRedirect=false;request.Proxy=null;request.UseDefaultCredentials=false;
            request.UserAgent="KodVsego-SmartDNS-DNS-Check";request.CachePolicy=new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            byte[] bytes=Encoding.ASCII.GetBytes(body);request.ContentLength=bytes.Length;
            using(cancel.Register(()=>request.Abort()))try {
                using(var stream=request.GetRequestStream())stream.Write(bytes,0,bytes.Length);
                using(var response=(HttpWebResponse)request.GetResponse())using(var stream=response.GetResponseStream())using(var output=new MemoryStream()){
                    if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("Наблюдатель DNS недоступен.");
                    var buffer=new byte[4096];int read;while((read=stream.Read(buffer,0,buffer.Length))>0){cancel.ThrowIfCancellationRequested();if(output.Length+read>65536)throw new IOException("Слишком большой ответ DNS-проверки.");output.Write(buffer,0,read);}
                    var json=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=12}.DeserializeObject(Encoding.UTF8.GetString(output.ToArray())) as IDictionary<string,object>;
                    object success,data;if(json==null||!json.TryGetValue("success",out success)||!(success is bool)||!(bool)success||!json.TryGetValue("data",out data)||!(data is IDictionary<string,object>))throw new IOException("Наблюдатель DNS не вернул результат.");
                    return (IDictionary<string,object>)data;
                }
            }catch(WebException){cancel.ThrowIfCancellationRequested();throw;}
        }
        public void Lookup(string host,bool system,int provider,DnsTransport protocol,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if(system){var task=Task.Factory.FromAsync<string,IPAddress[]>(System.Net.Dns.BeginGetHostAddresses,System.Net.Dns.EndGetHostAddresses,host,null);if(!task.Wait(2500,cancel))throw new TimeoutException();return;}
            byte[] query=DnsProbe.Query(host);
            if(protocol!=DnsTransport.Plain){new TunnelUpstream(provider,protocol==DnsTransport.Tls).Query(query);return;}
            using(var udp=new UdpClient(AddressFamily.InterNetwork)){
                udp.Client.ReceiveTimeout=1800;udp.Client.SendTimeout=1800;udp.Connect(IPAddress.Parse(Dns.V4[provider][0]),53);udp.Send(query,query.Length);IPEndPoint source=null;udp.Receive(ref source);
            }
        }
        public void Pause(CancellationToken cancel){if(cancel.WaitHandle.WaitOne(1200))cancel.ThrowIfCancellationRequested();}
    }
}
