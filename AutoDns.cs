using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HbAi.XboxDns
{
    sealed class AutoGoal
    {
        internal string Name;
        internal string[] Hosts;
        internal bool Api;
    }
    sealed class AutoDnsResult
    {
        internal int Provider, Milliseconds;
        internal bool Encrypted, NeedsBrowser, Tls;
        internal string Error;
        internal string Trace;
        internal ServiceAvailabilityResult Availability;
        internal string Summary {get{return Availability==null?(NeedsBrowser?"HTTPS есть · проверка в браузере":"HTTPS отвечает"):Availability.EndpointSummary;}}
        internal bool Ok { get { return Error == null; } }
    }
    sealed class BrowserChallengeException : IOException
    {
        internal BrowserChallengeException() : base("Сайт требует проверку в браузере (Cloudflare)") { }
    }
    interface IAutoDnsProbe : IServiceAvailabilityProbe
    {
        void Https(string host, string address, bool api, CancellationToken cancel);
    }
    static class AutoDns
    {
        internal static int GoalIndex(string name)
        {return Array.FindIndex(Goals,goal=>string.Equals(goal.Name,name,StringComparison.OrdinalIgnoreCase));}
        internal static readonly AutoGoal[] Goals = new[] {
            new AutoGoal {Name="Gemini", Hosts=new[]{"gemini.google.com"}},
            new AutoGoal {Name="ChatGPT", Hosts=new[]{"chatgpt.com"}},
            new AutoGoal {Name="Claude", Hosts=new[]{"claude.ai"}},
            new AutoGoal {Name="ElevenLabs", Hosts=new[]{"elevenlabs.io"}},
            new AutoGoal {Name="Spotify", Hosts=new[]{"open.spotify.com"}},
            new AutoGoal {Name="Xbox", Hosts=new[]{"xbox.com"}},
            new AutoGoal {Name="Antigravity", Hosts=new[]{"cloudcode-pa.googleapis.com","daily-cloudcode-pa.googleapis.com"}, Api=true}
        }.Where(goal=>ProviderCatalog.All.Any(provider=>Dns.IsAvailable(provider.id)&&provider.autoGoals.Contains(goal.Name))).ToArray();
        internal static int[] Candidates(int goal, bool encrypted, bool tls = false)
        {
            if(goal<0 || goal>=Goals.Length) throw new ArgumentOutOfRangeException("goal");
            return Enumerable.Range(0,ProviderCatalog.All.Length).Where(p=>ProviderCatalog.All[p].autoGoals.Contains(Goals[goal].Name)&&Dns.IsAvailable(p)&&(!encrypted || (tls ? DotDns.Available(p) : EncryptedDns.Templates[p]!=null))).ToArray();
        }
        internal static AutoDnsResult[] Search(int goal, bool encrypted, IAutoDnsProbe probe, CancellationToken cancel, Action<AutoDnsResult> progress=null, int? onlyProvider=null, bool tls=false)
        {
            var providers=Candidates(goal,encrypted,tls);
            if(onlyProvider.HasValue) providers=providers.Where(p=>p==onlyProvider.Value).ToArray();
            var results=new AutoDnsResult[providers.Length];
            // At most two providers at once; never a task for every host/server.
            Parallel.For(0,providers.Length,new ParallelOptions {MaxDegreeOfParallelism=2,CancellationToken=cancel},i=> {
                int provider=providers[i];
                var result=new AutoDnsResult {Provider=provider,Encrypted=encrypted||Dns.EncryptedOnly(provider),Tls=tls};
                var watch=Stopwatch.StartNew();
                var trace=new StringBuilder();trace.AppendLine("Provider="+Dns.Name[provider]+"; protocol="+(result.Encrypted?(tls?"TLS":"HTTPS"):"DNS")+"; UTC="+DateTime.UtcNow.ToString("O"));
                trace.AppendLine("Mode=AvailabilityOnly; authentication and generation not attempted");
                try {
                    int service=Array.FindIndex(ServiceAvailability.Targets,t=>t.Name==Goals[goal].Name);
                    if(service>=0) {
                        trace.AppendLine("DNS servers="+(result.Encrypted?(tls?DotDns.Host(provider):EncryptedDns.Templates[provider]):string.Join(",",Dns.V4[provider])));
                        // Two providers in parallel, with sequential endpoints within each provider.
                        result.Availability=ServiceAvailability.Check(service,provider,result.Encrypted,probe,cancel,1);
                        trace.AppendLine(result.Availability.Diagnostic);
                        result.NeedsBrowser=result.Availability.Endpoints.Any(e=>e.Reply==ServiceReply.BrowserCheck);
                        if(!result.Availability.CanRecommend)result.Error=result.Availability.EndpointSummary;
                    }
                    else {
                    foreach(string host in Goals[goal].Hosts) {
                        cancel.ThrowIfCancellationRequested();
                        trace.AppendLine(watch.ElapsedMilliseconds+" ms DNS query "+host+"; servers="+(result.Encrypted?(tls?DotDns.Host(provider):EncryptedDns.Templates[provider]):string.Join(",",Dns.V4[provider]))+"; timeout="+(result.Encrypted?DohHttp2.RequestTimeoutMilliseconds:2000)+" ms");
                        var addresses=probe.Resolve(provider,result.Encrypted,host,cancel);
                        trace.AppendLine(watch.ElapsedMilliseconds+" ms DNS "+host+" -> "+string.Join(", ",addresses));
                        bool reachable=false, challenged=false; string error="DNS не вернул IPv4-адрес";
                        foreach(string address in addresses.Distinct().Take(3)) {
                            cancel.ThrowIfCancellationRequested();
                            trace.AppendLine(watch.ElapsedMilliseconds+" ms HTTPS "+host+" @ "+address+"; connect/TLS timeout=3500 ms");
                            try {probe.Https(host,address,Goals[goal].Api,cancel);trace.AppendLine(watch.ElapsedMilliseconds+" ms HTTPS OK");reachable=true;break;}
                            catch(OperationCanceledException){throw;}
                            catch(BrowserChallengeException ex){challenged=true;trace.AppendLine(watch.ElapsedMilliseconds+" ms "+ex.Message);}
                            catch(Exception ex){error=ex.Message;trace.AppendLine(watch.ElapsedMilliseconds+" ms "+ex.ToString());}
                        }
                        if(!reachable && challenged) { reachable=true;result.NeedsBrowser=true; }
                        if(!reachable)throw new IOException(host+": "+error);
                    }
                    }
                }
                catch(OperationCanceledException){throw;}
                catch(Exception ex){result.Error=ex.Message;trace.AppendLine(ex.ToString());}
                result.Trace=DiagnosticReport.Scrub(trace.ToString());
                result.Milliseconds=(int)watch.ElapsedMilliseconds;results[i]=result;
                if(progress!=null)progress(result);
            });
            cancel.ThrowIfCancellationRequested();return results;
        }
        internal static AutoDnsResult Best(IEnumerable<AutoDnsResult> results)
        { return results.Where(r=>r.Ok).OrderBy(r=>r.NeedsBrowser).ThenBy(r=>r.Milliseconds).ThenBy(r=>r.Provider).FirstOrDefault(); }
        internal static string Diagnostic(int goal,IEnumerable<AutoDnsResult> results)
        {return DiagnosticReport.Scrub("Auto goal="+Goals[goal].Name+"\n"+string.Join("\n\n",results.Select(r=>(r.Trace??"")+"\nTotal="+r.Milliseconds+" ms; error="+r.Error+"; browser challenge="+r.NeedsBrowser)));}
        internal static string Report(int goal, IEnumerable<AutoDnsResult> results)
        {
            return "Цель: "+Goals[goal].Name+"\n\n"+string.Join("\n",results.OrderBy(r=>r.Provider).Select(r=>Dns.Name[r.Provider]+(r.Encrypted?(r.Tls?" · DoT":" · DoH"):" · DNS")+": "+(r.Ok?r.Summary+" · "+r.Milliseconds+" мс":r.Error)))+
                "\n\nПроверено соединение с адресом от выбранного DNS. Вход в аккаунт и работа модели не проверялись. Применяется общий DNS компьютера.";
        }
    }
    sealed class AutoDnsProbe : IAutoDnsProbe
    {
        readonly bool tls;
        internal AutoDnsProbe(bool tls = false) { this.tls = tls; }
        public ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel)
        {return new ServiceAvailabilityProbe(tls).Request(endpoint,address,cancel);}
        public string[] Resolve(int provider, bool encrypted, string host, CancellationToken cancel)
        {
            if (encrypted && tls) { cancel.ThrowIfCancellationRequested(); var answer=DotDns.Resolve(provider,host); cancel.ThrowIfCancellationRequested(); return answer; }
            var request=DnsProbe.Query(host); Exception last=null;var addresses=new List<string>();
            foreach(string server in encrypted?new[]{EncryptedDns.Templates[provider]}:Dns.V4[provider]) {
                cancel.ThrowIfCancellationRequested();
                try {
                    byte[] response;
                    if(encrypted)response=DohHttp2.QueryReliable(server,request);
                    else using(var udp=new UdpClient(AddressFamily.InterNetwork)) {
                        udp.Client.ReceiveTimeout=2000;udp.Client.SendTimeout=2000;
                        udp.Connect(IPAddress.Parse(server),53);udp.Send(request,request.Length);
                        IPEndPoint sender=null;response=udp.Receive(ref sender);
                    }
                    DnsProbe.Validate(request,response);
                    addresses.AddRange(DnsProbe.Addresses(request,response));
                }
                catch(Exception ex){last=ex;}
            }
            cancel.ThrowIfCancellationRequested();
            if(addresses.Count>0)return addresses.Distinct().ToArray();
            throw new IOException("DNS не ответил: "+(last==null?"нет сервера":last.Message),last);
        }
        internal static bool AcceptStatus(int status, bool api)
        { return status>=200&&status<400 || api&&(status==401||status==404||status==405); }
        internal static void CheckResponse(int status, string headers, bool api)
        {
            // Cloudflare documents cf-mitigated: challenge as its challenge-page marker.
            // A plain 403 or Server: cloudflare alone does not prove a browser challenge.
            bool challenge=(headers??"").Split('\n').Any(line=> {
                int colon=line.IndexOf(':');
                return colon>0&&line.Substring(0,colon).Trim().Equals("cf-mitigated",StringComparison.OrdinalIgnoreCase)&&
                    line.Substring(colon+1).Trim().Equals("challenge",StringComparison.OrdinalIgnoreCase);
            });
            if(challenge)throw new BrowserChallengeException();
            if(!AcceptStatus(status,api))throw new IOException("HTTPS "+status+" · сервис отклонил проверку");
        }
        internal static bool PublicAddress(string address)
        {
            IPAddress ip;
            if(!IPAddress.TryParse(address,out ip)||ip.AddressFamily!=AddressFamily.InterNetwork)return false;
            var b=ip.GetAddressBytes();
            return b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&
                !(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127);
        }
        public void Https(string host, string address, bool api, CancellationToken cancel)
        {
            IPAddress ip;
            if(!PublicAddress(address)||!IPAddress.TryParse(address,out ip))throw new IOException("DNS вернул локальный или некорректный адрес сервиса");
            using(var tcp=new TcpClient(AddressFamily.InterNetwork))
            using(cancel.Register(()=>tcp.Close())) {
                var connect=tcp.BeginConnect(ip,443,null,null);
                using(var wait=connect.AsyncWaitHandle)if(!wait.WaitOne(3500))throw new IOException("HTTPS: нет соединения (3,5 с)");
                tcp.EndConnect(connect);cancel.ThrowIfCancellationRequested();
                using(var ssl=new SslStream(tcp.GetStream(),false)) {
                    ssl.ReadTimeout=3500;ssl.WriteTimeout=3500;
                    // Connect to the candidate IP, but validate the original host's certificate and SNI.
                    var auth=ssl.BeginAuthenticateAsClient(host,null,SslProtocols.Tls12,true,null,null);
                    using(var wait=auth.AsyncWaitHandle)if(!wait.WaitOne(3500))throw new IOException("HTTPS: TLS не ответил");
                    ssl.EndAuthenticateAsClient(auth);cancel.ThrowIfCancellationRequested();
                    var request=Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: "+host+"\r\nUser-Agent: KodVsego-SmartDNS\r\nAccept: text/html,application/json\r\nConnection: close\r\n\r\n");
                    ssl.Write(request,0,request.Length);
                    var header=new StringBuilder();
                    var reading=Stopwatch.StartNew();
                    while(header.Length<8192) {
                        if(reading.ElapsedMilliseconds>=3500)throw new IOException("HTTPS: время ожидания ответа истекло");
                        ssl.ReadTimeout=Math.Max(1,3500-(int)reading.ElapsedMilliseconds);
                        cancel.ThrowIfCancellationRequested();int c=ssl.ReadByte();if(c<0)break;
                        header.Append((char)c);int n=header.Length;
                        if(n>=4&&header[n-4]=='\r'&&header[n-3]=='\n'&&header[n-2]=='\r'&&header[n-1]=='\n')break;
                    }
                    string first=header.ToString().Split('\n')[0].Trim();var parts=first.Split(' ');int status;
                    if(parts.Length<2||!parts[0].StartsWith("HTTP/1.",StringComparison.Ordinal)||!int.TryParse(parts[1],out status))throw new IOException("Некорректный ответ HTTPS");
                    CheckResponse(status,header.ToString(),api);
                }
            }
        }
    }
}
