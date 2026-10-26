using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    interface ILocalDnsConfiguration { void SetAntigravity(int provider); }
    sealed class LocalDnsRouting
    {
        Func<byte[],byte[]> common,antigravity;
        internal LocalDnsRouting(Func<byte[],byte[]> common,Func<byte[],byte[]> antigravity=null){this.common=common;this.antigravity=antigravity;}
        internal static int[] Providers {get{return Dns.AvailableProviders.Where(Supports).ToArray();}}
        internal static bool Supports(int provider){return Dns.IsAvailable(provider)&&RuntimeProviders.All[provider].autoGoals.Contains("Antigravity")&&EncryptedDns.Templates[provider]!=null;}
        internal static bool IsAntigravity(byte[] query)
        {
            int end=TunnelWire.QuestionEnd(query),offset=12;var labels=new List<string>();
            while(offset<end-4){int length=query[offset++];if(length==0)break;labels.Add(Encoding.ASCII.GetString(query,offset,length));offset+=length;}
            return AntigravityDns.Domains.Contains(string.Join(".",labels),StringComparer.OrdinalIgnoreCase);
        }
        internal byte[] Query(byte[] query){var special=Volatile.Read(ref antigravity);return special!=null&&IsAntigravity(query)?special(query):Volatile.Read(ref common)(query);}
        internal void ReplaceAntigravity(Func<byte[],byte[]> next,Action flush)
        {
            if(next==null)throw new ArgumentNullException("next");
            var previous=Interlocked.Exchange(ref antigravity,next);
            try{flush();}catch{Interlocked.Exchange(ref antigravity,previous);try{flush();}catch{}throw;}
        }
        internal void ReplaceCommon(Func<byte[],byte[]> next,Action flush)
        {
            if(next==null)throw new ArgumentNullException("next");
            var previous=Interlocked.Exchange(ref common,next);
            try{flush();}catch{Interlocked.Exchange(ref common,previous);try{flush();}catch{}throw;}
        }
        internal void Probe(Action<string> trace)
        {
            if(antigravity==null)return;
            foreach(string host in AntigravityDns.Domains){var query=DnsProbe.Query(host);var response=Query(query);DnsProbe.Validate(query,response);trace("ANTIGRAVITY DNS preflight host="+host+"; rcode=NOERROR; IPv4="+string.Join(",",DnsProbe.Addresses(query,response))+"; model/account NOT verified");}
        }
    }
    // Independent implementation using our existing encrypted transport and backup engine.
    // Listeners are loopback-only; no packet driver, routing changes or plaintext upstream.
    sealed class LocalDnsProxy : IDisposable
    {
        readonly Func<byte[],byte[]> exchange;
        readonly Action<string> trace;
        readonly List<UdpClient> datagrams=new List<UdpClient>();
        readonly List<TcpListener> listeners=new List<TcpListener>();
        readonly List<Task> readers=new List<Task>();
        readonly HashSet<TcpClient> clients=new HashSet<TcpClient>();
        readonly SemaphoreSlim slots=new SemaphoreSlim(32,32);
        readonly object gate=new object();
        volatile bool stopped;volatile string failure;
        long answers,errors,rejected;int pending;
        internal string Error {get{return failure;}}
        internal int Port {get;private set;}
        internal bool Ipv6 {get;private set;}
        internal string Statistics {get{return Interlocked.Read(ref answers)+" "+Interlocked.Read(ref errors);}}
        internal string Counters {get{return "answers="+Interlocked.Read(ref answers)+"; errors="+Interlocked.Read(ref errors)+"; rejected="+Interlocked.Read(ref rejected)+"; in-flight="+Volatile.Read(ref pending);}}
        internal LocalDnsProxy(Func<byte[],byte[]> exchange,Action<string> trace=null){this.exchange=exchange;this.trace=trace??(s=>{});}
        internal void Start(int port=53,bool ipv6=true)
        {
            if(readers.Count>0||stopped)throw new InvalidOperationException("Локальный DNS уже запущен или закрыт.");
            try {
                Bind(IPAddress.Loopback,port);Port=((IPEndPoint)listeners[0].LocalEndpoint).Port;
                // Fail before changing DNS if a required IPv6 loopback listener cannot bind.
                if(ipv6&&Socket.OSSupportsIPv6){Bind(IPAddress.IPv6Loopback,Port);Ipv6=true;}
                foreach(var udp in datagrams){var socket=udp;readers.Add(Task.Factory.StartNew(()=>ReceiveUdp(socket),TaskCreationOptions.LongRunning));}
                foreach(var tcp in listeners){var listener=tcp;readers.Add(Task.Factory.StartNew(()=>AcceptTcp(listener),TaskCreationOptions.LongRunning));}
                trace("LOCAL LISTEN UDP/TCP; IPv4=127.0.0.1; IPv6="+Ipv6+"; port="+Port);
            }catch(Exception problem){Dispose();throw new IOException("Не удалось открыть локальный DNS. Порт UDP/TCP "+port+" может быть занят DNS-AI, другим DNS-приложением или службой Windows. Настройки не изменены.",problem);}
        }
        void Bind(IPAddress address,int port)
        {
            var udp=new UdpClient(address.AddressFamily);TcpListener tcp=null;
            try {
                udp.ExclusiveAddressUse=true;
                if(address.AddressFamily==AddressFamily.InterNetworkV6)udp.Client.SetSocketOption(SocketOptionLevel.IPv6,SocketOptionName.IPv6Only,true);
                udp.Client.Bind(new IPEndPoint(address,port));int chosen=((IPEndPoint)udp.Client.LocalEndPoint).Port;
                tcp=new TcpListener(address,chosen);tcp.Server.ExclusiveAddressUse=true;
                if(address.AddressFamily==AddressFamily.InterNetworkV6)tcp.Server.SetSocketOption(SocketOptionLevel.IPv6,SocketOptionName.IPv6Only,true);
                tcp.Start(16);datagrams.Add(udp);listeners.Add(tcp);
            }catch{udp.Close();if(tcp!=null)tcp.Stop();throw;}
        }
        void Fatal(Exception problem){if(!stopped)failure=problem.GetType().Name;}
        void ReceiveUdp(UdpClient socket)
        {
            while(!stopped)try {
                IPEndPoint from=null;var query=socket.Receive(ref from);
                if(!IPAddress.IsLoopback(from.Address)||query.Length<12){Interlocked.Increment(ref rejected);continue;}
                if(!slots.Wait(0)){Interlocked.Increment(ref rejected);continue;}
                var client=from;Interlocked.Increment(ref pending);
                ThreadPool.QueueUserWorkItem(_=>{try{
                    byte[] reply=Answer(query);if(reply==null)return;
                    if(reply.Length>UdpLimit(query))reply=TunnelWire.Error(query,true);
                    if(!stopped)socket.Send(reply,reply.Length,client);
                }catch(Exception){if(!stopped)Interlocked.Increment(ref errors);}finally{Interlocked.Decrement(ref pending);slots.Release();}});
            }catch(ObjectDisposedException){break;}catch(SocketException problem){if(!stopped&&problem.SocketErrorCode==SocketError.ConnectionReset)continue;Fatal(problem);break;}catch(Exception problem){Fatal(problem);break;}
        }
        void AcceptTcp(TcpListener listener)
        {
            while(!stopped)try {
                var client=listener.AcceptTcpClient();
                if(!IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint).Address)||!slots.Wait(0)){client.Close();Interlocked.Increment(ref rejected);continue;}
                lock(gate){if(stopped){client.Close();slots.Release();break;}clients.Add(client);}Interlocked.Increment(ref pending);
                ThreadPool.QueueUserWorkItem(_=>{try{ServeTcp(client);}catch(Exception){if(!stopped)Interlocked.Increment(ref errors);}finally{lock(gate)clients.Remove(client);client.Close();Interlocked.Decrement(ref pending);slots.Release();}});
            }catch(ObjectDisposedException){break;}catch(Exception problem){Fatal(problem);break;}
        }
        void ServeTcp(TcpClient client)
        {
            client.ReceiveTimeout=5000;client.SendTimeout=5000;
            using(var stream=client.GetStream())for(int requests=0;requests<32&&!stopped;requests++){
                int hi=stream.ReadByte();if(hi<0)return;int lo=stream.ReadByte();if(lo<0)return;
                int length=hi*256+lo;if(length<12)return;var query=new byte[length];int offset=0;
                while(offset<length){int read=stream.Read(query,offset,length-offset);if(read<=0)return;offset+=read;}
                byte[] reply=Answer(query);if(reply==null)return;
                if(stopped)return;stream.WriteByte((byte)(reply.Length>>8));stream.WriteByte((byte)reply.Length);stream.Write(reply,0,reply.Length);
            }
        }
        byte[] Answer(byte[] query)
        {
            try{TunnelWire.QuestionEnd(query);}catch(IOException){Interlocked.Increment(ref rejected);return null;}
            try{var reply=exchange(query);TunnelWire.Validate(query,reply);Interlocked.Increment(ref answers);return reply;}
            catch(Exception problem){Interlocked.Increment(ref errors);if(Interlocked.Read(ref errors)<=20)trace("LOCAL upstream error: "+TunnelDiagnostics.Error(problem));return TunnelWire.Error(query);}
        }
        internal static int UdpLimit(byte[] query)
        {
            try {
                int offset=TunnelWire.QuestionEnd(query);
                // Do not guess where an OPT record is when the query contains other sections.
                if(TunnelPacket.U16(query,6)!=0||TunnelPacket.U16(query,8)!=0||TunnelPacket.U16(query,10)!=1)return 512;
                if(offset+11>query.Length||query[offset]!=0||TunnelPacket.U16(query,offset+1)!=41)return 512;
                return Math.Max(512,Math.Min(4096,TunnelPacket.U16(query,offset+3)));
            }catch(IOException){return 512;}
        }
        public void Dispose()
        {
            stopped=true;foreach(var socket in datagrams)socket.Close();foreach(var listener in listeners)listener.Stop();
            lock(gate)foreach(var client in clients)client.Close();
            try{Task.WaitAll(readers.ToArray(),2000);}catch(AggregateException){}
            // In-flight encrypted calls own their slot until completion. Never dispose it early.
        }
        internal void Probe()
        {
            foreach(var address in Ipv6?new[]{IPAddress.Loopback,IPAddress.IPv6Loopback}:new[]{IPAddress.Loopback}){
                byte[] query=DnsProbe.Query("example.com");
                using(var udp=new UdpClient(address.AddressFamily)){udp.Client.ReceiveTimeout=10000;udp.Connect(address,Port);udp.Send(query,query.Length);IPEndPoint from=null;DnsProbe.Validate(query,udp.Receive(ref from));}
                using(var client=new TcpClient(address.AddressFamily)){
                    client.Connect(address,Port);client.ReceiveTimeout=10000;client.SendTimeout=10000;
                    using(var stream=client.GetStream()){
                        stream.WriteByte((byte)(query.Length>>8));stream.WriteByte((byte)query.Length);stream.Write(query,0,query.Length);
                        int hi=stream.ReadByte(),lo=stream.ReadByte();if(hi<0||lo<0||hi*256+lo<12)throw new IOException("Локальный TCP DNS не ответил.");
                        var reply=new byte[hi*256+lo];int offset=0;while(offset<reply.Length){int read=stream.Read(reply,offset,reply.Length-offset);if(read==0)throw new IOException("Неполный ответ локального DNS.");offset+=read;}DnsProbe.Validate(query,reply);
                    }
                }
            }
        }
    }
    static class LocalDnsLifecycle
    {
        internal static void Run(Action start,Action probe,Func<bool> cancelled,Action arm,Action apply,Action connected,Action wait,Action restore,Action stop)
        {
            bool changed=false;
            try{start();probe();if(cancelled())return;arm();changed=true;if(cancelled())return;apply();if(cancelled())return;connected();wait();}
            finally{try{if(changed)restore();}finally{stop();}}
        }
    }
    static class LocalDnsWorker
    {
        internal static void ValidateBaseline(IEnumerable<FamilyBackup> families)
        {
            foreach(var family in families)foreach(string server in family.Servers){IPAddress ip;
                if(IPAddress.TryParse(server,out ip)&&IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6?ip.MapToIPv4():ip))
                    throw new IOException("У адаптера уже задан локальный DNS. Сначала выключите DNS в DNS-AI или другой программе, которая его установила, чтобы она восстановила прежние настройки. Новая копия не создана.");
            }
        }
        internal static bool TryArgs(string[] args,out int parent,out Guid token,out int provider,out bool tls)
        {int antigravity;return TryArgs(args,out parent,out token,out provider,out tls,out antigravity);}
        internal static bool TryArgs(string[] args,out int parent,out Guid token,out int provider,out bool tls,out int antigravity)
        {bool specialTls;return TryArgs(args,out parent,out token,out provider,out tls,out antigravity,out specialTls);}
        internal static bool TryArgs(string[] args,out int parent,out Guid token,out int provider,out bool tls,out int antigravity,out bool specialTls)
        {
            antigravity=-1;specialTls=false;
            bool valid=DnsTunnel.TryWorkerMode(args!=null&&(args.Length==6||args.Length==7)?args.Take(5).ToArray():args,"--dns-local-worker",out parent,out token,out provider,out tls);
            if(!valid)return false;
            if(args.Length==5)return true;
            if(!int.TryParse(args[5],out antigravity)||(antigravity!=-1&&!LocalDnsRouting.Supports(antigravity)))return false;
            if(args.Length==6)return true;
            specialTls=args[6]=="tls";
            return (specialTls||args[6]=="https")&&(!specialTls||antigravity==-1||DotDns.Available(antigravity));
        }
        internal static int Run(string[] args)
        {
            int parent,provider,antigravity;Guid token;bool tls,specialTls;if(!TryArgs(args,out parent,out token,out provider,out tls,out antigravity,out specialTls)||!DnsElevation.IsAdministrator())return 5;
            try{using(var pipe=new NamedPipeClientStream(".",DnsTunnel.PipeName(token),PipeDirection.InOut,PipeOptions.Asynchronous)){
                pipe.Connect(10000);if(!DnsTunnel.ServerMatches(pipe,parent))return 5;
                using(var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true))using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true}){
                    var logGate=new object();Action<string> trace=s=>{try{lock(logGate)writer.WriteLine("TRACE "+TunnelDiagnostics.Line(s));}catch(IOException){}catch(ObjectDisposedException){}};
                    string stage="initialization";var elapsed=Stopwatch.StartNew();
                    Action<string> phase=name=>{stage=name;trace("LOCAL STAGE="+name+"; session-ms="+elapsed.ElapsedMilliseconds);};
                    var command=reader.ReadLineAsync();command.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                    using(var mutex=new Mutex(false,DnsTunnel.MutexName)){
                        bool locked=false;string result=null;LocalDnsGuardian guard=null;
                        try {
                            phase("guard-and-snapshot");try{locked=mutex.WaitOne(0);}catch(AbandonedMutexException){locked=true;}if(!locked)throw new IOException("Другая операция DNS ещё выполняется.");
                            if(DnsTunnel.Pending){phase("recover-previous-session");string recovery=DnsTunnel.Recover();if(recovery!=null)throw new IOException(recovery);}
                            phase("check-network-and-original-dns");
                            if(VpnCheck.Scan().NeedsAttention)throw new IOException("Сначала выключите другой VPN.");
                            string conflict=EncryptedDns.PolicyConflict(NrptRegistryReader.Read(true));if(conflict!=null)throw new IOException(conflict);
                            var system=new TunnelDnsSystem();var snapshots=new TunnelBackupStore(new RegistryDnsBackupStore(),system.Adapters(true).Select(a=>a.Id));snapshots.EnsureReady();
                            ValidateBaseline(system.Adapters(true).SelectMany(a=>new[]{a.V4?system.Read(a.Id,"IPv4"):null,a.V6?system.Read(a.Id,"IPv6"):null}).Where(f=>f!=null));
                            trace("LOCAL session UTC="+DateTime.UtcNow.ToString("O")+"; provider="+Dns.Name[provider]+"; protocol="+(tls?"DoT":"DoH")+"; adapters="+snapshots.Ids.Length+"; unrelated snapshots="+(new RegistryDnsBackupStore().Load().Count-snapshots.Load().Count));
                            string fingerprint=TunnelDnsSystem.Fingerprint();var engine=new DnsSettingsEngine(system,snapshots);
                            var upstream=new TunnelUpstream(provider,tls,trace);
                            var special=antigravity>=0?new TunnelUpstream(antigravity,specialTls,s=>trace("ANTIGRAVITY "+s)):null;
                            var routes=new LocalDnsRouting(upstream.Query,special==null?null:(Func<byte[],byte[]>)special.Query);
                            trace("ANTIGRAVITY separate="+(special!=null)+(special==null?"":"; provider="+Dns.Name[antigravity]+"; protocol="+(specialTls?"DoT":"DoH")+"; exact API domains only")+"; client/account NOT modified or verified");
                            var switching=new LocalDnsSwitchController();
                            using(var proxy=new LocalDnsProxy(routes.Query,trace)){
                                LocalDnsLifecycle.Run(()=>{phase("start-loopback-listeners");proxy.Start();if(system.Adapters(true).Any(a=>a.V6)&&!proxy.Ipv6)throw new IOException("IPv6 включён у адаптера, но локальный IPv6 DNS недоступен. Настройки не изменены.");},()=>{phase("encrypted-dns-preflight");proxy.Probe();routes.Probe(trace);},()=>command.IsCompleted,
                                    ()=>{phase("start-recovery-process");if(fingerprint!=TunnelDnsSystem.Fingerprint())throw new IOException("Сеть изменилась. Включите режим заново.");guard=LocalDnsGuardian.Start();if(fingerprint!=TunnelDnsSystem.Fingerprint())throw new IOException("Сеть изменилась до применения DNS. Включите режим заново.");phase("arm-recovery");guard.Mark(snapshots.Ids);},
                                    ()=>{phase("apply-and-verify");if(fingerprint!=TunnelDnsSystem.Fingerprint())throw new IOException("Сеть изменилась до применения DNS.");engine.ChangeVerified(proxy.Ipv6?new[]{"127.0.0.1","::1"}:new[]{"127.0.0.1"},null,false,()=>{proxy.Probe();SystemDnsVerification.Verify();if(fingerprint!=TunnelDnsSystem.Fingerprint())throw new IOException("Сеть изменилась при проверке DNS.");});},
                                    ()=>{phase("connected");trace("LOCAL connected; provider="+Dns.Name[provider]+"; protocol="+(tls?"DoT":"DoH")+"; default route=unchanged; Wintun=not used");lock(logGate)writer.WriteLine("CONNECTED Локальный DNS · "+Dns.Name[provider]);},
                                    ()=>{phase("running");int ticks=0;try{while(proxy.Error==null){
                                        if(guard.Exited)throw new IOException("Процесс восстановления остановлен. Локальный DNS выключается.");
                                        if(fingerprint!=TunnelDnsSystem.Fingerprint())throw new IOException("Сеть изменилась. Локальный DNS выключен; включите его заново.");
                                        if(command.IsCompleted){string line=command.GetAwaiter().GetResult();if(line==null||line=="STOP")break;
                                            RuntimeProviders.Reload();var requested=LocalDnsSwitchCommand.Parse(line);trace("LOCAL SWITCH requested provider="+Dns.Name[requested.Provider]+"; route="+(requested.Antigravity?"Antigravity":"common")+"; protocol="+(requested.Tls?"DoT":"DoH"));
                                            if(!switching.Begin(requested,candidate=>{var next=new TunnelUpstream(candidate.Provider,candidate.Tls,trace);foreach(string host in candidate.Antigravity?AntigravityDns.Domains:new[]{"example.com"}){var question=DnsProbe.Query(host);DnsProbe.Validate(question,next.Query(question));}return next.Query;}))lock(logGate)writer.WriteLine("SWITCH_FAILED Предыдущая проверка ещё завершается. Повторите позже; прежний DNS работает.");
                                            command=reader.ReadLineAsync();command.ContinueWith(t=>{var observed=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                                        }
                                        switching.Poll(routes,system.Flush,line=>{trace("LOCAL "+line);lock(logGate)writer.WriteLine(line);});
                                        if(ticks%5==0)lock(logGate)writer.WriteLine("STATS "+proxy.Statistics);if(ticks++%50==0)trace("LOCAL COUNTERS "+proxy.Counters);Thread.Sleep(100);
                                    }if(proxy.Error!=null)throw new IOException("Локальный DNS остановлен: "+proxy.Error);}finally{switching.Cancel();}},
                                    ()=>{string before=stage;phase("restore-original-dns");string failure=DnsTunnel.RestoreSnapshot(engine,snapshots);trace("LOCAL RESTORE="+(failure??"complete"));if(failure!=null)result=failure;stage=before;},
                                    ()=>{string before=stage;phase("stop-loopback-listeners");trace("LOCAL FINAL "+proxy.Counters);proxy.Dispose();stage=before;});
                            }
                        }catch(Exception problem){string cause=TunnelDiagnostics.Error(problem);result=result==null?cause:result+"; Причина остановки: "+cause;trace("LOCAL ERROR stage="+stage+"; HRESULT=0x"+problem.HResult.ToString("X8")+"; "+result);}
                        finally{if(locked)mutex.ReleaseMutex();if(guard!=null)try{guard.Dispose();}catch(Exception problem){string cause=TunnelDiagnostics.Error(problem);result=result==null?cause:result+"; "+cause;trace("LOCAL ERROR stage=stop-recovery-process; "+cause);}}
                        trace("LOCAL SESSION END; ms="+elapsed.ElapsedMilliseconds+"; result="+(result??"stopped"));
                        foreach(string entry in DiagnosticReport.Recent(12000).Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))trace("WORKER "+entry);
                        try{lock(logGate)writer.WriteLine(result==null?"STOPPED":"ERROR "+result);}catch(IOException){}
                        return result==null?0:1;
                    }
                }
            }}catch{return 1;}
        }
    }
    // An elevated peer watches a private pipe. EOF also occurs on TerminateProcess;
    // recovery is restricted to this session's durable owner ID and adapter snapshots.
    sealed class LocalDnsGuardian : IDisposable
    {
        readonly NamedPipeServerStream pipe;readonly Process process;readonly Guid owner;
        LocalDnsGuardian(NamedPipeServerStream pipe,Process process,Guid owner){this.pipe=pipe;this.process=process;this.owner=owner;}
        internal bool Exited {get{return process.HasExited;}}
        internal static bool MarkerPresent {get{using(var key=Registry.LocalMachine.OpenSubKey(RegistryDnsBackupStore.Key))return key!=null&&key.GetValue("LocalResolverOwner")!=null;}}
        internal static LocalDnsGuardian Start()
        {
            Guid owner=Guid.NewGuid();var pipe=DnsTunnel.Server(owner);Process child=null;
            try{var pending=pipe.BeginWaitForConnection(null,null);child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--dns-local-guard "+Process.GetCurrentProcess().Id+" "+owner.ToString("N")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)});
                if(child==null||!DnsTunnel.WaitForConnection(pipe,pending,10000)||!DnsTunnel.ClientMatches(pipe,child.Id))throw new IOException("Не удалось запустить процесс восстановления DNS.");
                using(var reader=new StreamReader(pipe,Encoding.UTF8,false,1024,true)){var ready=reader.ReadLineAsync();if(!ready.Wait(5000)||ready.Result!="READY")throw new IOException("Процесс восстановления DNS не готов.");}
                return new LocalDnsGuardian(pipe,child,owner);
            }catch{pipe.Dispose();if(child!=null)child.Dispose();throw;}
        }
        internal void Mark(string[] ids)
        {
            DnsTunnel.Mark(true,ids,owner.ToString("N"));
        }
        internal static bool Owns(string expected,string actual){Guid id;return Guid.TryParseExact(expected,"N",out id)&&id!=Guid.Empty&&string.Equals(expected,actual,StringComparison.Ordinal);}
        internal static bool RecoverOwned(string owner,Func<string> currentOwner,Action recover,Action clear)
        {if(!Owns(owner,currentOwner()))return false;recover();clear();return true;}
        internal static void WaitForDisconnect(TextReader reader)
        {try{while(reader.ReadLine()!=null){}}catch(IOException){}}
        internal static bool TryGuard(string[] args,out int parent,out Guid owner)
        {parent=0;owner=Guid.Empty;return args!=null&&args.Length==3&&args[0]=="--dns-local-guard"&&int.TryParse(args[1],out parent)&&parent>0&&Guid.TryParseExact(args[2],"N",out owner)&&owner!=Guid.Empty;}
        internal static int Run(string[] args)
        {
            int parent;Guid owner;if(!TryGuard(args,out parent,out owner)||!DnsElevation.IsAdministrator())return 5;
            try{
                bool authenticated=false;
                try{using(var pipe=new NamedPipeClientStream(".",DnsTunnel.PipeName(owner),PipeDirection.InOut,PipeOptions.Asynchronous)){
                    pipe.Connect(10000);if(!DnsTunnel.ServerMatches(pipe,parent))return 5;
                    authenticated=true;
                    using(var reader=new StreamReader(pipe,Encoding.UTF8,false,1024,true))using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true}){writer.WriteLine("READY");WaitForDisconnect(reader);}
                }}catch(IOException){if(!authenticated)return 1;}
                using(var mutex=new Mutex(false,DnsTunnel.MutexName)){
                    bool locked=false;try{try{locked=mutex.WaitOne(20000);}catch(AbandonedMutexException){locked=true;}if(!locked)return 1;
                        using(var key=Registry.LocalMachine.OpenSubKey(RegistryDnsBackupStore.Key,true)){
                            if(key==null)return 0;
                            RecoverOwned(owner.ToString("N"),()=>key.GetValue("LocalResolverOwner") as string,
                                ()=>{string failure=DnsTunnel.Pending?DnsTunnel.Recover():null;if(failure!=null)throw new IOException(failure);},
                                ()=>key.DeleteValue("LocalResolverOwner",false));return 0;
                        }
                    }finally{if(locked)mutex.ReleaseMutex();}
                }
            }catch{return 1;}
        }
        public void Dispose(){pipe.Dispose();try{process.WaitForExit(4000);}catch(InvalidOperationException){}process.Dispose();}
    }
}
