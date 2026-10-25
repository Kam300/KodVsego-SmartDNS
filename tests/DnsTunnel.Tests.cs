using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.IO.Pipes;
using System.Drawing;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using HbAi.XboxDns;

static class TunnelTests
{
    sealed class FakeSession : ITunnelSession
    {
        public bool Running{get;private set;}public bool Connected{get;private set;}public bool Stopping{get;private set;}
        public string LastError{get;private set;}public string Statistics{get{return "12 0";}}public int Starts,Stops,Provider;public bool Tls;
        public event Action Changed;void Notify(){if(Changed!=null)Changed();}
        public void Start(int provider,bool tls){Starts++;Provider=provider;Tls=tls;Running=true;Notify();}
        public void Connect(){Connected=true;Notify();}
        public void Stop(){if(!Running)return;Stops++;Stopping=true;Notify();}
        public void Finish(){Running=Connected=Stopping=false;Notify();}
        public void Fail(){LastError="Запрос прав отменён. DNS не изменён.";Running=Connected=Stopping=false;Notify();}
        public void Dispose(){Stop();}
    }
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Reject(Action run,string name){bool rejected=false;try{run();}catch(IOException){rejected=true;}Check(rejected,name);}
    static byte[] Answer(byte[] query){var reply=TunnelWire.Error(query);reply[3]=128;if(TunnelPacket.U16(query,query.Length-4)==1){TunnelPacket.Put16(reply,6,1);reply=reply.Concat(new byte[]{192,12,0,1,0,1,0,0,0,60,0,4,93,184,215,14}).ToArray();}return reply;}
    static TunnelPacket Request(bool v6,int protocol,byte[] data,uint seq=100,uint ack=0,int flags=16)
    {return new TunnelPacket{Source=IPAddress.Parse(v6?"fd6b:7653:646e::1":"198.18.254.1").GetAddressBytes(),Destination=IPAddress.Parse(v6?DnsTunnelNative.V6:DnsTunnelNative.V4).GetAddressBytes(),SourcePort=45000,DestinationPort=53,Protocol=protocol,Payload=data,Sequence=seq,Acknowledgement=ack,Flags=flags,Window=32768};}
    // Build an incoming request with the same checksum implementation as replies;
    // independently check the resulting wire checksums below.
    static byte[] Wire(TunnelPacket p)
    {return new TunnelPacket{Source=p.Destination,Destination=p.Source,SourcePort=p.DestinationPort,DestinationPort=p.SourcePort,Protocol=p.Protocol}.Reply(p.Payload,p.Sequence,p.Acknowledgement,p.Flags);}
    [STAThread] static void Main(string[] args)
    {
        if(args.Length==1&&args[0]=="--scope-state") {
            var live=new TunnelDnsSystem();var backups=new RegistryDnsBackupStore();var ids=live.Adapters(true).Select(a=>a.Id).ToArray();
            var scope=new TunnelBackupStore(backups,ids);
            Console.WriteLine("Active adapters="+ids.Length+"; active snapshots="+scope.Load().Count+"; unrelated snapshots="+(backups.Load().Count-scope.Load().Count));
            scope.EnsureReady();Console.WriteLine("PASS current snapshot state allows tunnel startup; read only, no network writes");return;
        }
        // Exercise real Windows pipe handles without launching/elevating a worker,
        // loading a driver or touching any DNS settings.
        for(int cycle=0;cycle<10;cycle++) {
            Guid connectionToken=Guid.NewGuid();
            using(var server=DnsTunnel.Server(connectionToken))
            using(var client=new NamedPipeClientStream(".",DnsTunnel.PipeName(connectionToken),PipeDirection.InOut,PipeOptions.Asynchronous)) {
                var pending=server.BeginWaitForConnection(null,null);
                var connected=Task.Factory.StartNew(()=>{
                    client.Connect(3000);
                    using(var writer=new StreamWriter(client,Encoding.UTF8,1024,true){AutoFlush=true})
                    using(var reader=new StreamReader(client,Encoding.UTF8,false,1024,true)) {
                        writer.WriteLine("READY");var stop=reader.ReadLineAsync();if(!stop.Wait(3000)||stop.Result!="STOP")throw new Exception("Missing pipe shutdown command");
                    }
                });
                Check(DnsTunnel.WaitForConnection(server,pending,5000)&&DnsTunnel.ClientMatches(server,Process.GetCurrentProcess().Id),"Real control pipe connects without closing SafeHandle early, cycle "+cycle);
                Check(!DnsTunnel.ClientMatches(server,int.MaxValue),"Control pipe rejects an unexpected process ID");
                using(var reader=new StreamReader(server,Encoding.UTF8,false,1024,true))
                using(var writer=new StreamWriter(server,Encoding.UTF8,1024,true){AutoFlush=true}) {
                    var ready=reader.ReadLineAsync();Check(ready.Wait(3000)&&ready.Result=="READY","Connected pipe remains readable after EndWaitForConnection");
                    writer.WriteLine("STOP");Check(connected.Wait(3000),"Control pipe supports clean shutdown without a worker or DNS writes");
                }
            }
        }
        using(var server=DnsTunnel.Server(Guid.NewGuid())) {
            var pending=server.BeginWaitForConnection(null,null);
            Check(!DnsTunnel.WaitForConnection(server,pending,20),"Connection timeout cancels an unconnected pipe safely");
        }
        using(var server=DnsTunnel.Server(Guid.NewGuid())) {
            var pending=server.BeginWaitForConnection(null,null);
            var wait=Task.Factory.StartNew(()=>{try{DnsTunnel.WaitForConnection(server,pending,1000);}catch(ObjectDisposedException){}catch(IOException){}});
            server.Dispose();Check(wait.Wait(3000),"Closing while connecting completes the pending operation without hanging");
        }
        Check(Marshal.SizeOf(typeof(DnsTunnelNative.AddressRow))==80&&Marshal.OffsetOf(typeof(DnsTunnelNative.AddressRow),"InterfaceLuid").ToInt32()==32&&Marshal.OffsetOf(typeof(DnsTunnelNative.AddressRow),"Prefix").ToInt32()==60,"Unicast row matches the Windows ABI");
        var query=DnsProbe.Query("example.com");var answer=TunnelWire.Error(query);
        TunnelWire.Validate(query,answer);Check((answer[3]&15)==2,"SERVFAIL retains the original transaction and question");
        answer[3]=(byte)((answer[3]&240)|3);TunnelWire.Validate(query,answer);Check((answer[3]&15)==3,"NXDOMAIN is preserved as a DNS response, not a transport failure");
        var aaaa=(byte[])query.Clone();TunnelPacket.Put16(aaaa,aaaa.Length-4,28);TunnelWire.Validate(aaaa,TunnelWire.Error(aaaa));
        Check(TunnelPacket.U16(aaaa,aaaa.Length-4)==28,"Generic DNS transport supports AAAA questions");
        var txt=(byte[])query.Clone();TunnelPacket.Put16(txt,txt.Length-4,16);TunnelWire.Validate(txt,TunnelWire.Error(txt));Check(true,"Generic DNS transport supports TXT questions");
        var extended=query.Concat(new byte[]{0,0,41,4,208,0,0,0,0,0,0}).ToArray();TunnelPacket.Put16(extended,10,1);TunnelWire.Validate(extended,TunnelWire.Error(extended));Check(true,"EDNS metadata is not incorrectly treated as part of the question");
        var wrong=(byte[])answer.Clone();wrong[0]^=1;Reject(()=>TunnelWire.Validate(query,wrong),"Mismatched DNS transaction is rejected");
        Reject(()=>TunnelWire.Validate(txt,answer),"Mismatched question type is rejected");
        Reject(()=>TunnelWire.QuestionEnd(new byte[17]),"Empty DNS messages are rejected");
        var compressed=(byte[])query.Clone();compressed[12]=192;Reject(()=>TunnelWire.QuestionEnd(compressed),"Compressed/malformed request labels are rejected");
        var truncated=TunnelWire.Error(query,true);Check((truncated[2]&2)!=0&&(truncated[3]&15)==0,"Large UDP response can request TCP without inventing a DNS error");
        Check(TunnelDiagnostics.LogHost(DnsProbe.Query("private-visit.example"))=="<other>"&&TunnelDiagnostics.LogHost(DnsProbe.Query("CHATGPT.com"))=="chatgpt.com","Only fixed public diagnostic names are disclosed in query logs");
        var messages=new List<string>();
        var upstream=new TunnelUpstream(6,true,messages.Add,(address,q)=>{var reply=TunnelWire.Error(q);reply[3]=133;return reply;});
        var refused=upstream.Query(DnsProbe.Query("private-visit.example"));
        Check((refused[3]&15)==5&&messages.Single().Contains("REFUSED(5)")&&messages[0].Contains("upstream=")&&!messages[0].Contains("private-visit"),"REFUSED remains a DNS reply while diagnostic logs identify the server without disclosing private names");
        messages.Clear();upstream=new TunnelUpstream(0,false,messages.Add,(address,q)=>{throw new IOException("TLS failed",new IOException("api_key=sample-secret"));});
        Reject(()=>upstream.Query(query),"Both upstream transport failures remain failures");
        Check(messages.Count==1&&messages[0].Contains("TLS failed")&&!messages[0].Contains("sample-secret")&&messages[0].Contains("ms="),"Upstream cause and duration survive redaction");
        messages.Clear();upstream=new TunnelUpstream(6,true,messages.Add,(address,q)=>TunnelWire.Error(q));for(int i=0;i<510;i++)upstream.Query(query);
        Check(messages.Count==501&&messages.Last().Contains("limit=500"),"Repeated DNS failures cannot flood the detailed log indefinitely");
        Check(!TunnelDiagnostics.Line("Authorization: Bearer sample-secret\r\napi_key=another-secret").Contains("sample-secret")&&!TunnelDiagnostics.Line("api_key=another-secret").Contains("another-secret"),"Copied diagnostic lines redact authorization and key values");
        Check(TunnelDiagnostics.Line(new string('x',5000)).Length<2100&&!TunnelDiagnostics.Line("one\r\ntwo").Contains("\n"),"Individual log lines are bounded and cannot inject worker messages");
        var html=new ServiceHttpReply{Status=403,Body="<html><title>Access denied</title><script>private-script-text</script></html>"};html.Headers["Content-Type"]="text/html";html.Headers["cf-ray"]="test-ray";
        var endpoint=new ServiceEndpoint{Host="chatgpt.com",Path="/backend-api/codex/responses",Api=true,WebSocket=true};
        string summary=TunnelDiagnostics.Reply(endpoint,html);
        Check(summary.Contains("Forbidden; cause not established")&&summary.Contains("Access denied")&&summary.Contains("test-ray")&&!summary.Contains("private-script-text"),"HTML 403 records a bounded title and trace ID without inventing a regional cause or copying the page");
        html.Headers["cf-mitigated"]="challenge";Check(TunnelDiagnostics.Reply(endpoint,html).Contains("Cloudflare browser challenge"),"Only an explicit challenge header labels a browser challenge");
        var unauthorized=new ServiceHttpReply{Status=401,Body="{\"error\":{\"message\":\"api_key=sample-secret\",\"code\":\"missing_authorization\"}}"};unauthorized.Headers["Content-Type"]="application/json";
        Check(TunnelDiagnostics.Reply(endpoint,unauthorized).Contains("account NOT tested")&&!TunnelDiagnostics.Reply(endpoint,unauthorized).Contains("sample-secret"),"401 and safe error fields never imply that model access works");
        var regional=new ServiceHttpReply{Status=403,Body="{\"error\":{\"code\":\"unsupported_country_region_territory\"}}"};regional.Headers["Content-Type"]="application/json";
        Check(TunnelDiagnostics.Reply(endpoint,regional).Contains("explicit regional restriction"),"An explicit regional error is separated from an unexplained HTML 403");
        messages.Clear();int dnsCalls=0,httpCalls=0;bool codex=false;
        TunnelDiagnostics.Check(q=>{dnsCalls++;return Answer(q);},(ep,address,cancel)=>{httpCalls++;codex|=ep.WebSocket&&ep.Host=="chatgpt.com"&&ep.Path=="/backend-api/codex/responses";return unauthorized;},messages.Add,CancellationToken.None);
        Check(dnsCalls==8&&httpCalls==4&&codex&&messages.Last().Contains("generation NOT verified"),"Anonymous diagnostics exercise A/AAAA and the actual Codex WebSocket path without generation");
        Check(messages.Any(m=>m.Contains("IPv4=93.184.215.14"))&&messages.Any(m=>m.Contains("TLS=validated")),"Diagnostic output retains selected resolver addresses and completed TLS stages");
        messages.Clear();var checkedHosts=new List<string>();
        TunnelDiagnostics.Check(Answer,(ep,address,cancel)=>{checkedHosts.Add(ep.Host);return unauthorized;},messages.Add,CancellationToken.None,true);
        Check(checkedHosts.Contains("gemini.google.com")&&checkedHosts.Contains("notebooklm.google.com")&&checkedHosts.All(h=>!h.Contains("openai")&&!h.Contains("chatgpt")),"Google profile diagnostics target Gemini and NotebookLM without promising ChatGPT or Codex support");
        messages.Clear();httpCalls=0;
        TunnelDiagnostics.Check(q=>{var reply=TunnelWire.Error(q);reply[3]=133;return reply;},(ep,address,cancel)=>{httpCalls++;return unauthorized;},messages.Add,CancellationToken.None);
        Check(httpCalls==0&&messages.Any(m=>m.Contains("REFUSED(5)"))&&messages.Any(m=>m.Contains("skipped")),"DNS refusal prevents connecting to a fabricated or fallback service address");
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool stopped=false;try{TunnelDiagnostics.Check(q=>{throw new Exception("Must not resolve");},(ep,address,cancel)=>{throw new Exception("Must not connect");},m=>{},cancelled.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"Cancellation prevents any diagnostic network operation");}
        foreach(bool v6 in new[]{false,true})foreach(int protocol in new[]{6,17}) {
            var incoming=Wire(Request(v6,protocol,query));var parsed=TunnelPacket.Parse(incoming);
            Check(parsed.SourcePort==45000&&parsed.DestinationPort==53&&parsed.Payload.SequenceEqual(query),"Incoming DNS packet parsed for IPv"+(v6?6:4)+" protocol "+protocol);
            var response=parsed.Reply(answer,123,456,24);int ip=v6?40:20;int pseudo=protocol+response.Length-ip;int start=v6?8:12;
            for(int i=start;i<start+(v6?32:8);i+=2)pseudo+=TunnelPacket.U16(response,i);
            Check(TunnelPacket.Checksum(response,ip,response.Length-ip,pseudo)==0,"Wire response transport checksum verified for IPv"+(v6?6:4)+" protocol "+protocol);
            if(!v6)Check(TunnelPacket.Checksum(response,0,20)==0,"Response IPv4 header checksum verified");
            incoming[incoming.Length-1]^=1;Reject(()=>TunnelPacket.Parse(incoming),"Corrupt transport packet rejected for IPv"+(v6?6:4)+" protocol "+protocol);
        }
        var other=Request(false,17,query);other.Destination=IPAddress.Parse("1.1.1.1").GetAddressBytes();Reject(()=>TunnelPacket.Parse(Wire(other)),"Public internet packets are never routed by the DNS-only endpoint");
        other=Request(false,17,query);other.DestinationPort=443;Reject(()=>TunnelPacket.Parse(Wire(other)),"Non-DNS ports are rejected");
        foreach(bool v6 in new[]{false,true}) {
            var output=new List<byte[]>();int calls=0;Action<byte[]> finish=null;
            using(var tcp=new TunnelTcp(output.Add,(q,done)=>{calls++;finish=done;})) {
                long now=TunnelTcp.Now;var syn=Request(v6,6,new byte[0],100,0,2);tcp.Accept(syn,now);var synack=output.Last();int ip=v6?40:20;uint server=TunnelPacket.U32(synack,ip+4);
                Check(synack[ip+13]==18&&TunnelPacket.U32(synack,ip+8)==101,"TCP SYN acknowledged for IPv"+(v6?6:4));
                tcp.Accept(syn,now+100);Check(output.Last().SequenceEqual(synack),"Retransmitted SYN reuses the existing session");
                tcp.Accept(Request(v6,6,new byte[0],101,unchecked(server+1),16),now+200);
                byte[] frame=new byte[query.Length+2];TunnelPacket.Put16(frame,0,query.Length);Buffer.BlockCopy(query,0,frame,2,query.Length);
                tcp.Accept(Request(v6,6,frame.Take(3).ToArray(),101,unchecked(server+1),24),now+300);Check(calls==0,"Incomplete DNS TCP frame is buffered");
                tcp.Accept(Request(v6,6,frame.Skip(3).ToArray(),104,unchecked(server+1),24),now+400);Check(calls==1,"Complete DNS TCP frame resolves exactly once");
                tcp.Accept(Request(v6,6,frame.Skip(3).ToArray(),104,unchecked(server+1),24),now+500);Check(calls==1,"Duplicate TCP data cannot duplicate upstream requests");
                finish(answer);var data=output.Last();Check(data[ip+13]==24&&TunnelPacket.U16(data,ip+20)==answer.Length,"DNS TCP response is length-prefixed and ready for reassembly");
                int count=output.Count;tcp.Tick(TunnelTcp.Now+1100);Check(output.Count>count&&output.Last().SequenceEqual(data),"Unacknowledged TCP response is retransmitted");
            }
        }
        var system=new FakeSystem{CaptureDoh=true};var store=new MemoryStore();var engine=new DnsSettingsEngine(system,store);
        var before=system.Values.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
        engine.Change(true,new[]{DnsTunnelNative.V4,DnsTunnelNative.V6});
        Check(system.Values[system.Id+"/IPv4"].SequenceEqual(new[]{DnsTunnelNative.V4})&&system.Values[system.Id+"/IPv6"].SequenceEqual(new[]{DnsTunnelNative.V6}),"Tunnel target is selected for both DNS address families");
        engine.Change(false,new string[0]);Check(store.Load().Count==0&&before.All(p=>system.Values[p.Key].SequenceEqual(p.Value)),"Stopping restores both original DNS families and clears the snapshot");
        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();engine=new DnsSettingsEngine(system,store);system.FailWrites.Add(2);
        Reject(()=>engine.Change(true,new[]{DnsTunnelNative.V4,DnsTunnelNative.V6}),"Partial tunnel DNS apply rolls back on an IPv6 write failure");
        Check(store.Load().Count==0&&system.Values[system.Id+"/IPv4"].SequenceEqual(new[]{"192.0.2.53","192.0.2.54"}),"Rollback preserves the original IPv4 settings");
        // An unavailable adapter from an older session must not block the active
        // connection or be restored/deleted as a side effect of a new tunnel.
        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();
        Guid offline=new Guid("00000000-0000-0000-0000-000000000099");
        var old=new AdapterBackup{Guid=offline.ToString("B"),Ipv6Binding=true,Families=new[]{
            new FamilyBackup{Family="IPv4",Manual=true,Servers=new[]{"198.51.100.53"},DohCaptured=true,Doh=new DohConfig[0]},
            new FamilyBackup{Family="IPv6",Manual=false,Servers=new string[0],DohCaptured=true,Doh=new DohConfig[0]}}};
        store.Save(new List<AdapterBackup>{old});string original=store.Json;
        var scoped=new TunnelBackupStore(store,new[]{system.Id});scoped.EnsureReady();
        Check(scoped.Load().Count==0&&store.Json==original,"Unrelated offline snapshot permits startup without changing the old snapshot");
        engine=new DnsSettingsEngine(system,scoped);engine.Change(true,new[]{DnsTunnelNative.V4,DnsTunnelNative.V6});
        Check(scoped.Load().Count==1&&store.Load().Count==2,"New tunnel snapshot is merged with the retained old snapshot");
        var ownIds=TunnelBackupStore.ParseIds(scoped.Ids);
        Check(ownIds.SequenceEqual(new[]{system.Id}),"Persisted session scope contains only adapters owned by this tunnel");
        system.Add(offline);var offlineBefore=system.Values[offline+"/IPv4"].ToArray();
        var recoveredScope=new TunnelBackupStore(store,ownIds);
        new DnsSettingsEngine(system,recoveredScope).Change(false,new string[0]);
        Check(recoveredScope.Load().Count==0&&store.Json==original,"Recovery clears only this session's snapshots and preserves the previous snapshot exactly");
        Check(system.Values[offline+"/IPv4"].SequenceEqual(offlineBefore)&&system.BindingWrites==0,"An unrelated adapter becoming available is not restored or modified by tunnel shutdown");
        var activeStore=new MemoryStore();var activeEngine=new DnsSettingsEngine(system,activeStore);
        activeEngine.Change(true,new[]{DnsTunnelNative.V4,DnsTunnelNative.V6});
        Reject(()=>new TunnelBackupStore(activeStore,new[]{system.Id}).EnsureReady(),"An existing backup for the current active connection still blocks a conflicting startup");
        int beforeWrites=system.Writes;Reject(()=>scoped.Save(new List<AdapterBackup>{old}),"Scoped store rejects writes belonging to another adapter");
        Check(system.Writes==beforeWrites&&store.Json==original,"Rejected foreign snapshot does not write settings or backups");
        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();store.Save(new List<AdapterBackup>{old});
        original=store.Json;scoped=new TunnelBackupStore(store,new[]{system.Id});engine=new DnsSettingsEngine(system,scoped);system.FailWrites.Add(2);
        Reject(()=>engine.Change(true,new[]{DnsTunnelNative.V4,DnsTunnelNative.V6}),"Partial apply with a retained offline snapshot rolls back the current connection");
        Check(store.Json==original&&scoped.Load().Count==0&&system.Values[system.Id+"/IPv4"].SequenceEqual(new[]{"192.0.2.53","192.0.2.54"}),"Rollback preserves the unrelated snapshot exactly and restores the active adapter");
        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();store.Save(new List<AdapterBackup>{old});original=store.Json;store.FailSave=true;
        scoped=new TunnelBackupStore(store,new[]{system.Id});engine=new DnsSettingsEngine(system,scoped);
        Reject(()=>engine.Change(true,new[]{DnsTunnelNative.V4,DnsTunnelNative.V6}),"Failed durable backup stops the tunnel before applying DNS");
        Check(system.Writes==0&&store.Json==original,"Backup write failure does not modify the active adapter or retained old snapshot");
        foreach(object invalidScope in new object[]{"invalid type",new string[0],new[]{"bad-guid"},new[]{system.Id.ToString(),system.Id.ToString()},new[]{Guid.Empty.ToString()},new[]{DnsTunnelNative.AdapterId.ToString()}})
            Reject(()=>TunnelBackupStore.ParseIds(invalidScope),"Invalid persisted adapter scope cannot broaden recovery to other snapshots");
        int parent,provider;Guid token;bool tls;string id=Guid.NewGuid().ToString("N");
        Check(DnsTunnel.TryWorker(new[]{"--dns-tunnel-worker","123",id,"6","tls"},out parent,out token,out provider,out tls)&&tls&&provider==6,"Worker accepts only a built-in provider and supported protocol");
        foreach(var requestArgs in new[]{new[]{"--dns-tunnel-worker","0",id,"6","tls"},new[]{"--dns-tunnel-worker","123",id,"1","tls"},new[]{"--dns-tunnel-worker","123",id,"6","udp"},new[]{"--dns-tunnel-worker","123","invalid","6","tls"}})
            Check(!DnsTunnel.TryWorker(requestArgs,out parent,out token,out provider,out tls),"Invalid or disabled worker operation rejected");
        using(var window=new DnsTunnelWindow())Check(window.Controls.OfType<System.Windows.Forms.Button>().Any(b=>b.Text=="Включить")&&window.Controls.OfType<System.Windows.Forms.Button>().Single(b=>b.Text=="Выключить").Enabled==false,"Opening tunnel controls never starts a network operation");
        foreach(float requested in new[]{1f,1.5f,2f})using(var window=new DnsTunnelWindow(requested)) {
            Check(window.Controls.Cast<Control>().All(c=>c.Left>=0&&c.Top>=0&&c.Right<=window.ClientSize.Width&&c.Bottom<=window.ClientSize.Height),"All tunnel controls fit at requested scale "+requested);
            var heading=window.Controls.OfType<Label>().Single(l=>l.Text=="DNS-туннель · beta");
            Check(TextRenderer.MeasureText(heading.Text,heading.Font).Width<=heading.Width,"Tunnel title fits at requested scale "+requested);
            var note=window.Controls.OfType<Label>().Single(l=>l.Text.StartsWith("Шифрованный DNS"));
            Check(note.GetPreferredSize(new Size(note.Width,0)).Height<=note.Height,"Tunnel explanation fits without clipping at requested scale "+requested);
            Check(window.Controls.OfType<Button>().All(b=>TextRenderer.MeasureText(b.Text,b.Font).Height<b.Height),"Tunnel action captions fit at requested scale "+requested);
        }
        foreach(Size area in new[]{new Size(1920,1080),new Size(1280,720)}){
            float fitted=DnsTunnelWindow.FitScale(2f,area);
            Check(480*fitted+40<=area.Width&&550*fitted+70<=area.Height,"200 percent layout fits within work area "+area);
        }
        Check(TunnelSession.GoogleProvider(0)&&TunnelSession.GoogleProvider(5)&&!TunnelSession.GoogleProvider(3)&&!TunnelSession.GoogleProvider(1),"Quick profile excludes Xbox-only and disabled providers");
        int launches=0;using(var cancelledSession=new TunnelSession(info=>{launches++;Check(info.Arguments.StartsWith("--dns-tunnel-worker ")&&info.CreateNoWindow,"Main profile launches only the headless built-in worker");throw new System.ComponentModel.Win32Exception(1223);})){
            cancelledSession.Start(0,true);Check(launches==1&&!cancelledSession.Running&&cancelledSession.LastError.Contains("отменён"),"Actual controller handles cancelled elevation without starting an app or leaving the session busy");
        }
        var fake=new FakeSession();var fields=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        using(var main=new MainForm(fake,false)) {
            foreach(string field in new[]{"initializing","agLoading","active","busy"})typeof(MainForm).GetField(field,fields).SetValue(main,false);
            var handle=main.Handle;main.SelectGoogleMode(true);
            Check(main.GoogleMode&&fake.Starts==0&&main.Transport!=DnsTransport.Plain,"Selecting the Google profile does not start a worker or modify DNS");
            Check(!main.TransportAvailable(DnsTransport.Plain),"Quick tunnel cannot silently use plaintext DNS");
            var bounds=(Rectangle)typeof(MainForm).GetField("rSecureOption",fields).GetValue(main);
            Check(bounds.Contains(main.ConnectionModeButton)&&bounds.Contains(main.ConnectionProtocolButton)&&!main.ConnectionModeButton.IntersectsWith(main.ConnectionProtocolButton)&&main.ConnectionMethod==ConnectionMethod.Google&&main.ConnectionHint.Contains("всего ПК"),"Unified method card names the experimental profile and its whole-PC scope");
            int openForms=Application.OpenForms.Count;var toggle=typeof(MainForm).GetMethod("Toggle",fields);
            toggle.Invoke(main,null);Application.DoEvents();
            Check(fake.Starts==1&&fake.Provider==0&&fake.Tls&&Application.OpenForms.Count==openForms,"Main power button starts the selected encrypted profile without another form");
            fake.Connect();Application.DoEvents();
            Check(main.ConnectionLabel=="Gemini / NotebookLM"&&(bool)typeof(MainForm).GetField("active",fields).GetValue(main),"Worker connection updates the main screen instead of an auxiliary window");
            main.SelectGoogleMode(false);main.SelectTransport(DnsTransport.Plain);
            Check(main.GoogleMode&&main.Transport!=DnsTransport.Plain,"An active tunnel cannot be switched into ordinary DNS by the selector");
            toggle.Invoke(main,null);Application.DoEvents();Check(fake.Stops==1&&fake.Stopping,"Main power button requests DNS restoration on stop");
            fake.Finish();Application.DoEvents();
            Check(!(bool)typeof(MainForm).GetField("active",fields).GetValue(main)&&!(bool)typeof(MainForm).GetField("busy",fields).GetValue(main),"Completed restoration releases the main controls");
            toggle.Invoke(main,null);fake.Connect();Application.DoEvents();main.Close();Application.DoEvents();
            Check(!main.IsDisposed&&fake.Stopping,"Closing the main window waits for tunnel restoration");
            fake.Finish();Application.DoEvents();Check(main.IsDisposed,"Main window closes after the tunnel reports completion");
        }
        fake=new FakeSession();using(var main=new MainForm(fake,false)){
            foreach(string field in new[]{"initializing","agLoading","active","busy"})typeof(MainForm).GetField(field,fields).SetValue(main,false);var handle=main.Handle;main.SelectGoogleMode(true);
            typeof(MainForm).GetMethod("Toggle",fields).Invoke(main,null);fake.Fail();Application.DoEvents();
            Check(!(bool)typeof(MainForm).GetField("busy",fields).GetValue(main)&&((string)typeof(MainForm).GetField("error",fields).GetValue(main)).Contains("отменён"),"UAC cancellation remains inline and leaves the main screen usable");
            using(var settings=main.CreateSettingsDialog())Check(settings.Controls.OfType<LinkLabel>().Any(l=>l.Text.Contains("Скачать журнал"))&&!settings.Controls.OfType<LinkLabel>().Any(l=>l.Text=="DNS-туннель · beta"),"Settings expose the full journal download instead of a second tunnel window");
        }
        Console.WriteLine("All "+passed+" tunnel checks passed; fake packets and snapshots, no driver loading or network configuration writes.");
    }
}
