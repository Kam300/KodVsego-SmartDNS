using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HbAi.XboxDns;

static class LocalDnsTests
{
    static int passed;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Reject(Action run,string name){bool rejected=false;try{run();}catch(IOException){rejected=true;}Check(rejected,name);}
    static byte[] Reply(byte[] query)
    {
        var answer=TunnelWire.Error(query);answer[3]=128;
        if(TunnelPacket.U16(query,query.Length-4)==1){TunnelPacket.Put16(answer,6,1);answer=answer.Concat(new byte[]{192,12,0,1,0,1,0,0,0,60,0,4,93,184,215,14}).ToArray();}
        return answer;
    }
    static byte[] Udp(int port,IPAddress address,byte[] query)
    {
        using(var client=new UdpClient(address.AddressFamily)){client.Client.ReceiveTimeout=3000;client.Connect(address,port);client.Send(query,query.Length);IPEndPoint from=null;return client.Receive(ref from);}
    }
    static byte[] Tcp(NetworkStream stream,byte[] query)
    {
        var frame=new[]{(byte)(query.Length>>8),(byte)query.Length}.Concat(query).ToArray();
        // Fragment the DNS-over-TCP length prefix and message deliberately.
        stream.Write(frame,0,1);stream.Write(frame,1,3);stream.Write(frame,4,frame.Length-4);
        int hi=stream.ReadByte(),lo=stream.ReadByte();if(hi<0||lo<0)throw new IOException("No DNS frame");var reply=new byte[hi*256+lo];int offset=0;
        while(offset<reply.Length){int read=stream.Read(reply,offset,reply.Length-offset);if(read<=0)throw new IOException("Short frame");offset+=read;}return reply;
    }
    sealed class Session : ITunnelSession,ILocalDnsConfiguration
    {
        public bool Running{get;private set;}public bool Connected{get;private set;}public bool Stopping{get;private set;}public string LastError{get;set;}public string Statistics{get{return "2 0";}}
        public event Action Changed;internal int Starts,Stops;internal bool Tls;internal int Provider;
        internal int Antigravity=-1;public void SetAntigravity(int provider){Antigravity=provider;}
        void Notify(){if(Changed!=null)Changed();}
        public void Start(int provider,bool tls){Provider=provider;Tls=tls;Starts++;Running=true;Notify();}
        internal void Connect(){Connected=true;Notify();}internal void Finish(){Running=Connected=Stopping=false;Notify();}
        public void Stop(){if(!Running)return;Stops++;Stopping=true;Notify();}public void Dispose(){Stop();}
    }
    [STAThread] static void Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--fake-pipe-owner"){
            using(var pipe=DnsTunnel.Server(Guid.ParseExact(args[1],"N"))){var pending=pipe.BeginWaitForConnection(null,null);if(!DnsTunnel.WaitForConnection(pipe,pending,10000))return;
                using(var reader=new StreamReader(pipe))if(reader.ReadLine()=="READY")Process.GetCurrentProcess().Kill();
            }return;
        }
        var query=DnsProbe.Query("example.com");
        int commonCalls=0,specialCalls=0;
        var routes=new LocalDnsRouting(q=>{commonCalls++;return Reply(q);},q=>{specialCalls++;return Reply(q);});
        foreach(string host in AntigravityDns.Domains){var packet=DnsProbe.Query(host.ToUpperInvariant());foreach(int type in new[]{1,28,65,16}){TunnelPacket.Put16(packet,packet.Length-4,type);routes.Query(packet);}}
        foreach(string host in new[]{"gemini.google.com","chatgpt.com","oauth2.googleapis.com","accounts.google.com","sub.cloudcode-pa.googleapis.com","cloudcode-pa.googleapis.com.evil.test"})routes.Query(DnsProbe.Query(host));
        Check(specialCalls==8&&commonCalls==6,"Only the two exact Antigravity API names use the separate resolver, across DNS record types");
        var messages=new List<string>();routes.Probe(messages.Add);Check(messages.Count==2&&messages.All(s=>s.Contains("model/account NOT verified")),"Both Antigravity DNS names must pass preflight without claiming account/model availability");
        Reject(()=>new LocalDnsRouting(Reply,q=>TunnelWire.Error(q)).Probe(s=>{}),"Failed Antigravity DNS preflight rejects activation instead of silently using the common provider");
        int normal=0;new LocalDnsRouting(q=>{normal++;return Reply(q);}).Query(DnsProbe.Query(AntigravityDns.Domains[0]));Check(normal==1,"Disabling the special route returns Antigravity names to the common DNS path");
        Check(LocalDnsRouting.Providers.SequenceEqual(new[]{4,5,6,9,13})&&!LocalDnsRouting.Supports(0)&&!LocalDnsRouting.Supports(1)&&!LocalDnsRouting.Supports(3),"Only enabled catalog providers with declared Antigravity goal and DoH are offered");
        using(var proxy=new LocalDnsProxy(Reply)){
            proxy.Start(0);Check(proxy.Port>0,"Tests bind a free loopback port instead of changing adapter DNS or taking port 53");
            proxy.Probe();Check(proxy.Error==null,"Local UDP/TCP self-check works before DNS is applied");
            foreach(var address in proxy.Ipv6?new[]{IPAddress.Loopback,IPAddress.IPv6Loopback}:new[]{IPAddress.Loopback}){
                var answer=Udp(proxy.Port,address,query);DnsProbe.Validate(query,answer);Check(answer.SequenceEqual(Reply(query)),"UDP wire bytes preserved on "+address);
                using(var client=new TcpClient(address.AddressFamily)){
                    client.Connect(address,proxy.Port);client.ReceiveTimeout=3000;
                    using(var stream=client.GetStream())for(int i=0;i<3;i++){var tcp=Tcp(stream,query);Check(tcp.SequenceEqual(Reply(query)),"Fragmented TCP frame and connection reuse on "+address+" request "+i);}
                }
                var aaaa=query.ToArray();TunnelPacket.Put16(aaaa,aaaa.Length-4,28);var ipv6Answer=Udp(proxy.Port,address,aaaa);Check(ipv6Answer.SequenceEqual(Reply(aaaa)),"AAAA/NODATA responses pass through unchanged");
                var https=query.ToArray();TunnelPacket.Put16(https,https.Length-4,65);Check(Udp(proxy.Port,address,https).SequenceEqual(Reply(https)),"HTTPS record query is forwarded without rewriting");
            }
            using(var conflict=new LocalDnsProxy(Reply))Reject(()=>conflict.Start(proxy.Port,false),"Occupied UDP/TCP endpoint is rejected before any network writes");
            Check(Udp(proxy.Port,IPAddress.Loopback,query).SequenceEqual(Reply(query)),"Failed competing listener leaves the original server working");
            using(var idle=new TcpClient()){idle.Connect(IPAddress.Loopback,proxy.Port);proxy.Dispose();Check(proxy.Error==null,"Closing listeners and idle TCP clients is a normal shutdown");}
        }
        int reusable;using(var proxy=new LocalDnsProxy(Reply)){proxy.Start(0,false);reusable=proxy.Port;proxy.Dispose();}
        using(var proxy=new LocalDnsProxy(Reply)){proxy.Start(reusable,false);proxy.Probe();Check(true,"Stopped proxy releases both transports for a new session");}
        using(var proxy=new LocalDnsProxy(q=>{Thread.Sleep(20);return Reply(q);})){proxy.Start(0,false);
            for(int i=0;i<10;i++)using(var departed=new UdpClient()){departed.Connect(IPAddress.Loopback,proxy.Port);departed.Send(query,query.Length);}
            Thread.Sleep(100);proxy.Probe();Check(proxy.Error==null,"Clients abandoning UDP responses cannot kill the local listener through ICMP resets");
        }
        var occupiedTcp=new TcpListener(IPAddress.Loopback,0);occupiedTcp.Start();int tcpPort=((IPEndPoint)occupiedTcp.LocalEndpoint).Port;
        try{using(var conflict=new LocalDnsProxy(Reply))Reject(()=>conflict.Start(tcpPort,false),"TCP-only collision rolls back the already bound UDP socket");}finally{occupiedTcp.Stop();}
        using(var retry=new LocalDnsProxy(Reply)){retry.Start(tcpPort,false);retry.Probe();Check(true,"Failed TCP startup leaves the port reusable for both transports");}
        using(var proxy=new LocalDnsProxy(q=>{throw new IOException("offline");})){proxy.Start(0,false);var fail=Udp(proxy.Port,IPAddress.Loopback,query);Check((fail[3]&15)==2&&fail[0]==query[0]&&fail[1]==query[1],"Encrypted upstream failure yields SERVFAIL with original transaction, no plaintext fallback");Reject(()=>proxy.Probe(),"Failed upstream cannot pass local preflight");}
        using(var proxy=new LocalDnsProxy(q=>{var a=Reply(q);a[0]^=1;return a;})){proxy.Start(0,false);Check((Udp(proxy.Port,IPAddress.Loopback,query)[3]&15)==2,"Mismatched upstream transaction is rejected");}
        using(var proxy=new LocalDnsProxy(q=>{var a=Reply(q);a[3]=131;TunnelPacket.Put16(a,6,0);return a.Take(q.Length).ToArray();})){proxy.Start(0,false);Check((Udp(proxy.Port,IPAddress.Loopback,query)[3]&15)==3,"NXDOMAIN is retained rather than converted to connectivity success");}
        var opt=query.Concat(new byte[]{0,0,41,4,208,0,0,0,0,0,0}).ToArray();TunnelPacket.Put16(opt,10,1);
        Check(LocalDnsProxy.UdpLimit(query)==512&&LocalDnsProxy.UdpLimit(opt)==1232,"UDP size follows EDNS with a conservative classic-DNS default");
        using(var proxy=new LocalDnsProxy(q=>Reply(q).Concat(new byte[1000]).ToArray())){proxy.Start(0,false);var shortReply=Udp(proxy.Port,IPAddress.Loopback,query);Check((shortReply[2]&2)!=0&&shortReply.Length==query.Length,"Oversized UDP response requests a TCP retry instead of exceeding the client limit");using(var tcp=new TcpClient()){tcp.Connect(IPAddress.Loopback,proxy.Port);tcp.ReceiveTimeout=3000;Check(Tcp(tcp.GetStream(),query).Length>512,"TCP retains the complete large upstream response");}}
        var order=new List<string>();
        Action run=()=>LocalDnsLifecycle.Run(()=>order.Add("listen"),()=>order.Add("probe"),()=>false,()=>order.Add("guard"),()=>order.Add("apply"),()=>order.Add("connected"),()=>order.Add("wait"),()=>order.Add("restore"),()=>order.Add("stop"));run();
        Check(string.Join(",",order)=="listen,probe,guard,apply,connected,wait,restore,stop","Listener and guard precede DNS writes; restore precedes listener shutdown");
        foreach(string fail in new[]{"listen","probe","guard","apply","connected","wait","restore"}){
            order.Clear();Action<string> step=name=>{order.Add(name);if(name==fail)throw new IOException(name);};
            Reject(()=>LocalDnsLifecycle.Run(()=>step("listen"),()=>step("probe"),()=>false,()=>step("guard"),()=>step("apply"),()=>step("connected"),()=>step("wait"),()=>step("restore"),()=>step("stop")),"Failure is surfaced at "+fail);
            Check(order.Last()=="stop"&&(!order.Contains("apply")||order.Contains("restore")),"Failure at "+fail+" preserves stop/restore order");
        }
        order.Clear();LocalDnsLifecycle.Run(()=>order.Add("listen"),()=>order.Add("probe"),()=>true,()=>order.Add("guard"),()=>order.Add("apply"),()=>{},()=>{},()=>order.Add("restore"),()=>order.Add("stop"));
        Check(string.Join(",",order)=="listen,probe,stop","Cancel during preflight never marks or changes adapters");
        order.Clear();bool stoppedDuringArm=false;LocalDnsLifecycle.Run(()=>order.Add("listen"),()=>order.Add("probe"),()=>stoppedDuringArm,()=>{order.Add("guard");stoppedDuringArm=true;},()=>order.Add("apply"),()=>{},()=>{},()=>order.Add("restore"),()=>order.Add("stop"));
        Check(string.Join(",",order)=="listen,probe,guard,restore,stop","Cancellation while the guardian starts clears the armed snapshot marker without applying DNS");
        var system=new FakeSystem{CaptureDoh=true};var store=new MemoryStore();var engine=new DnsSettingsEngine(system,store);
        var original=system.Values[system.Id+"/IPv4"].ToArray();
        engine.ChangeVerified(new[]{"127.0.0.1","::1"},null,false,()=>{});
        Check(system.Values[system.Id+"/IPv4"].SequenceEqual(new[]{"127.0.0.1"})&&system.Values[system.Id+"/IPv6"].SequenceEqual(new[]{"::1"})&&system.Ipv6Enabled,"Fake adapter routes both families through loopback while preserving IPv6 binding");
        new DnsSettingsEngine(system,store).Change(false,new string[0]);Check(system.Values[system.Id+"/IPv4"].SequenceEqual(original)&&system.Values[system.Id+"/IPv6"].Length==0&&store.Load().Count==0,"Fresh recovery engine restores original settings from durable snapshots");
        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();engine=new DnsSettingsEngine(system,store);var currentSystem=system;
        Reject(()=>engine.ChangeVerified(new[]{"127.0.0.1","::1"},null,false,()=>{throw new IOException("verification failed");}),"System-query verification failure rolls the loopback settings back");
        Check(currentSystem.Values[currentSystem.Id+"/IPv4"].SequenceEqual(original)&&store.Load().Count==0,"Failed activation leaves no dead loopback DNS on fake adapters");
        foreach(string localhost in new[]{"127.0.0.1","127.0.0.2","::1","::ffff:127.0.0.1"})Reject(()=>LocalDnsWorker.ValidateBaseline(new[]{new FamilyBackup{Family="IPv4",Servers=new[]{localhost}}}),"Never save an unknown existing loopback resolver as original DNS: "+localhost);
        LocalDnsWorker.ValidateBaseline(new[]{new FamilyBackup{Family="IPv4",Servers=new[]{"192.168.0.1","8.8.8.8"}}});Check(true,"Private router and public DNS remain valid restoration baselines");
        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();Guid offline=Guid.NewGuid();store.Save(new List<AdapterBackup>{new AdapterBackup{Guid=offline.ToString("B"),Families=new[]{new FamilyBackup{Family="IPv4",Manual=true,Servers=new[]{"192.0.2.99"}}}}});
        var scoped=new TunnelBackupStore(store,new[]{system.Id});engine=new DnsSettingsEngine(system,scoped);engine.Change(true,new[]{"127.0.0.1","::1"});engine.Change(false,new string[0]);
        Check(store.Load().Count==1&&store.Load()[0].Guid==offline.ToString("B")&&store.Load()[0].Families[0].Servers[0]=="192.0.2.99","Local activation and restoration retain unrelated offline adapter snapshots");
        string owner=Guid.NewGuid().ToString("N"),other=Guid.NewGuid().ToString("N");bool recovered=false,cleared=false;
        Check(!LocalDnsGuardian.RecoverOwned(owner,()=>other,()=>recovered=true,()=>cleared=true)&&!recovered&&!cleared,"Late watchdog cannot restore or clear a different session");
        LocalDnsGuardian.RecoverOwned(owner,()=>owner,()=>recovered=true,()=>cleared=true);Check(recovered&&cleared,"Matching watchdog owner permits scoped recovery");
        cleared=false;Reject(()=>LocalDnsGuardian.RecoverOwned(owner,()=>owner,()=>{throw new IOException("restore failed");},()=>cleared=true),"Recovery failure remains visible");Check(!cleared,"Watchdog keeps durable ownership after failed restoration");
        Guid crashToken=Guid.NewGuid();
        using(var child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--fake-pipe-owner "+crashToken.ToString("N")){UseShellExecute=false,CreateNoWindow=true}))
        using(var pipe=new NamedPipeClientStream(".",DnsTunnel.PipeName(crashToken),PipeDirection.InOut,PipeOptions.Asynchronous)){
            pipe.Connect(5000);Check(DnsTunnel.ServerMatches(pipe,child.Id),"Guardian authenticates the actual owner process before monitoring disconnect");
            try{using(var reader=new StreamReader(pipe,System.Text.Encoding.UTF8,false,1024,true))using(var writer=new StreamWriter(pipe,System.Text.Encoding.UTF8,1024,true){AutoFlush=true}){
                writer.WriteLine("READY");LocalDnsGuardian.WaitForDisconnect(reader);Check(child.WaitForExit(5000),"Abrupt termination of a fake owner process releases the real control pipe");
                recovered=false;LocalDnsGuardian.RecoverOwned(owner,()=>owner,()=>recovered=true,()=>{});Check(recovered,"Watchdog path reaches scoped recovery after abrupt pipe-owner termination, using fake settings");
            }}catch(IOException){} // Flushing the writer after the deliberate peer kill may see a broken pipe.
        }
        int pid,provider;Guid token;bool tls;string id=Guid.NewGuid().ToString("N");
        Check(LocalDnsWorker.TryArgs(new[]{"--dns-local-worker","123",id,"5","https"},out pid,out token,out provider,out tls)&&provider==5&&!tls,"Constrained local worker CLI accepts an enabled encrypted provider");
        int specialProvider;Check(LocalDnsWorker.TryArgs(new[]{"--dns-local-worker","123",id,"5","tls","6"},out pid,out token,out provider,out tls,out specialProvider)&&provider==5&&tls&&specialProvider==6,"Common DNS/TLS and independent Antigravity/DoH provider travel separately to the worker");
        Check(!LocalDnsWorker.TryArgs(new[]{"--dns-local-worker","123",id,"5","https","0"},out pid,out token,out provider,out tls,out specialProvider)&&!LocalDnsWorker.TryArgs(new[]{"--dns-local-worker","123",id,"5","https","1"},out pid,out token,out provider,out tls,out specialProvider),"Worker refuses unconfirmed Comss or disabled GeoHide for the special route");
        Check(!LocalDnsWorker.TryArgs(new[]{"--dns-local-worker","123",id,"3","https"},out pid,out token,out provider,out tls)&&!LocalDnsWorker.TryArgs(new[]{"--dns-tunnel-worker","123",id,"5","https"},out pid,out token,out provider,out tls),"Local worker rejects disabled Xbox and another worker mode");
        Check(!LocalDnsGuardian.TryGuard(new[]{"--dns-local-guard","0",id},out pid,out token)&&!LocalDnsGuardian.TryGuard(new[]{"--dns-local-guard","1",Guid.Empty.ToString("N")},out pid,out token),"Guardian rejects missing parent and empty session owner");
        int launches=0;using(var session=new TunnelSession(info=>{launches++;Check(info.Arguments.StartsWith("--dns-local-worker ")&&!info.Arguments.Contains("wintun"),"Local session launches only the local worker");throw new System.ComponentModel.Win32Exception(1223);},true)){session.Start(5,false);Check(launches==1&&!session.Running&&session.LastError.Contains("отменён"),"Cancelled elevation never claims an active localhost resolver");}
        var google=new Session();var local=new Session();var fields=BindingFlags.Instance|BindingFlags.NonPublic;
        using(var form=new MainForm(google,false,local)){
            foreach(string field in new[]{"initializing","agLoading","active","busy"})typeof(MainForm).GetField(field,fields).SetValue(form,false);var handle=form.Handle;var originalSize=form.Size;
            typeof(MainForm).GetField("defaultGoal",fields).SetValue(form,-1);
            Check(form.LocalAntigravityProvider==5,"Default Antigravity route provider is DNS-AI (5)");
            Check(!form.LocalMode&&form.EnableAntigravityAppMode()&&form.LocalMode&&!form.GoogleMode&&form.Transport==DnsTransport.Https&&form.LocalAntigravity&&form.LocalAntigravityProvider==5,"Enabling Antigravity from Windows mode automatically switches to Mode 2 (DoH) with DNS-AI route");
            typeof(MainForm).GetField("hideAntigravityRestart",fields).SetValue(form,false);
            Check(form.ShouldOfferLocalAntigravityRestart(true)&&!form.ShouldOfferLocalAntigravityRestart(false),"Mode 2 with Antigravity route offers Antigravity restart when enabled in settings and Antigravity is running");
            typeof(MainForm).GetField("hideAntigravityRestart",fields).SetValue(form,true);
            Check(!form.ShouldOfferLocalAntigravityRestart(true),"Disabling Antigravity restart in settings suppresses the Mode 2 restart prompt");
            typeof(MainForm).GetField("hideAntigravityRestart",fields).SetValue(form,false);
            form.SelectLocalAntigravity(false,5);form.SelectLocalMode(false);
            form.SelectLocalMode(true);Check(form.LocalMode&&!form.GoogleMode&&form.Transport==DnsTransport.Https&&local.Starts==0,"Selecting local compatibility is separate from Google and defaults to HTTPS without starting DNS");
            typeof(MainForm).GetMethod("PickSet",fields).Invoke(form,new object[]{12});
            int selected=(int)typeof(MainForm).GetField("dnsSet",fields).GetValue(form);form.SelectLocalAntigravity(true,6);Check(form.LocalAntigravity&&form.LocalAntigravityProvider==6&&(int)typeof(MainForm).GetField("dnsSet",fields).GetValue(form)==selected&&local.Starts==0,"Antigravity has its own opt-in provider and does not start or replace the main resolver");
            var bounds=(Rectangle)typeof(MainForm).GetField("rSecureOption",fields).GetValue(form);
            Check(form.Size==originalSize&&bounds.Contains(form.ConnectionModeButton)&&bounds.Contains(form.ConnectionProtocolButton)&&!form.ConnectionModeButton.IntersectsWith(form.ConnectionProtocolButton)&&form.ConnectionMethod==ConnectionMethod.Application&&form.ConnectionHint.Contains("открыто"),"Unified method card fits the original window and explains the application lifetime");
            Check(((Rectangle)typeof(MainForm).GetField("rAgOption",fields).GetValue(form)).Contains(form.LocalAntigravityProviderButton),"Separate Antigravity provider stays inside its compact card");
            var toggle=typeof(MainForm).GetMethod("Toggle",fields);toggle.Invoke(form,null);local.Connect();Application.DoEvents();
            Check(local.Starts==1&&google.Starts==0&&form.ConnectionLabel=="Локальный DNS включён","Main power uses the local worker and reports its connection");
            Check(local.Antigravity==6,"Power button passes the selected independent Antigravity route to the local session");form.SelectLocalAntigravity(false,4);Check(form.LocalAntigravity&&form.LocalAntigravityProvider==6,"Running route cannot be replaced or disabled midway through outstanding DNS queries");
            Check(local.Provider==12&&local.Antigravity==6&&!form.SharedAntigravityDeclared,"Application mode can keep AdGuard as main DNS and use an explicitly separate MagicDNS route");
            Check(!form.ConnectionWarning,"Healthy connected local mode has no warning status");
            form.UpdateVpnStatus(new VpnStatus{Connections=new[]{"happ-xray"}});
            Check(form.ConnectionWarning,"Active VPN (e.g. happ-xray) triggers yellow ConnectionWarning in local mode");
            form.UpdateVpnStatus(new VpnStatus());
            Check(!form.ConnectionWarning,"Clearing VPN restores healthy non-warning state");
            typeof(MainForm).GetField("latencyProvider",fields).SetValue(form,selected);typeof(MainForm).GetField("latencyTransport",fields).SetValue(form,form.Transport);typeof(MainForm).GetField("latencyLocal",fields).SetValue(form,true);typeof(MainForm).GetField("latencyChecking",fields).SetValue(form,false);
            typeof(MainForm).GetField("connectionLatency",fields).SetValue(form,new ProviderLatency{Provider=selected,Protocol=form.Transport,Error="Timeout",MeasuredUtc=DateTime.UtcNow});
            Check(form.LatencyWarning&&form.ConnectionWarning&&form.ConnectionStatusCaption.EndsWith("— мс"),"Failed DNS latency triggers yellow warning status");
            typeof(MainForm).GetField("connectionLatency",fields).SetValue(form,new ProviderLatency{Provider=selected,Protocol=form.Transport,Milliseconds=24,MeasuredUtc=DateTime.UtcNow});
            Check(!form.LatencyWarning&&!form.ConnectionWarning,"Successful DNS latency clears warning status");
            int rev=(int)typeof(MainForm).GetField("resolverRevision",fields).GetValue(form);
            Check(form.AcceptManualResolverResult(selected,form.Transport,rev,new ResolverCheckResult{Provider=HbAi.XboxDns.Dns.Name[selected],Protocol=form.Transport.ToString(),Comparison=ResolverComparison.Incomplete})&&form.CheckerWarning&&form.ConnectionWarning&&form.CheckerCaption.Contains("не удалось проверить"),"Failed website checker triggers yellow warning status");
            Check(form.AcceptManualResolverResult(selected,form.Transport,rev,new ResolverCheckResult{Provider=HbAi.XboxDns.Dns.Name[selected],Protocol=form.Transport.ToString(),Official=new ProviderCheckResult{State=ProviderCheckState.Confirmed}})&&!form.CheckerWarning&&!form.ConnectionWarning,"Confirmed website checker clears yellow warning status");
            form.SelectGoogleMode(true);form.SelectLocalMode(false);form.SelectTransport(DnsTransport.Plain);Check(form.LocalMode&&!form.GoogleMode&&form.Transport!=DnsTransport.Plain,"Active localhost mode cannot switch profile or downgrade encryption");
            form.Close();Application.DoEvents();Check(!form.IsDisposed&&local.Stops>0,"Closing waits for original DNS restoration");local.Finish();Application.DoEvents();Check(form.IsDisposed,"Form closes once the local worker has restored DNS");
        }
        Console.WriteLine("All "+passed+" local DNS checks passed; loopback sockets and fake settings only, no adapter or driver changes.");
    }
}
