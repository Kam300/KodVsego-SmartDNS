using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HbAi.XboxDns;

static class ResolverCheckTests
{
    static int passed;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Throws(Action action,string name){try{action();}catch(IOException){Check(true,name);return;}throw new Exception(name);}
    static ResolverNode Node(string ip){return new ResolverNode{Address=ip,Network="Test network"};}
    sealed class Fake : IResolverCheckIo
    {
        internal int Created,Queries,Collected,Pauses,Stamps;internal bool Changed,Fail,NoData,LookupFailure;
        internal readonly List<string> Hosts=new List<string>();
        internal ResolverNode[] System=new[]{Node("8.8.8.8")},Control=new[]{Node("8.8.8.8")};
        public string Stamp(){return Changed&&Stamps++>0?"other":"same";}
        public ResolverTicket Create(CancellationToken cancel){cancel.ThrowIfCancellationRequested();if(Fail)throw new IOException("failure token=must-not-log");Created++;return new ResolverTicket{Id=Created==1?"system123456":"control123456",Nonce="testnonce"};}
        public void Lookup(string host,bool system,int provider,DnsTransport protocol,CancellationToken cancel){cancel.ThrowIfCancellationRequested();Interlocked.Increment(ref Queries);lock(Hosts)Hosts.Add((system?"S:":"C:")+host);if(LookupFailure)throw new IOException("NXDOMAIN");}
        public ResolverNode[] Collect(ResolverTicket ticket,CancellationToken cancel){cancel.ThrowIfCancellationRequested();Collected++;return NoData?new ResolverNode[0]:ticket.Id.StartsWith("system")?System:Control;}
        public void Pause(CancellationToken cancel){cancel.ThrowIfCancellationRequested();Pauses++;}
    }
    static void Main()
    {
        var f=new Fake();var r=ResolverCheck.Run(0,DnsTransport.Https,f,CancellationToken.None);
        Check(f.Created==2&&f.Queries==12&&f.Collected==2,"Exactly two independent tickets and twelve bounded lookups");
        Check(f.Hosts.Count(h=>h.StartsWith("S:")&&h.Contains("system123456"))==6&&f.Hosts.Count(h=>h.StartsWith("C:")&&h.Contains("control123456"))==6,"System and control tickets cannot contaminate each other");
        Check(r.Comparison==ResolverComparison.Shared&&r.Report.Contains("не доказывает отсутствие перехвата"),"Shared recursive nodes do not assert the configured provider or absence of interception");
        Check(r.Summary.Contains("провайдер не подтверждён"),"Shared samples cannot be presented as a provider confirmation");
        var isp=new[]{new ResolverNode{Address="78.29.1.10",Network="AS8369 Intersvyaz-2 JSC"}};
        var intercepted=ResolverCheck.Run(0,DnsTransport.Plain,new Fake{System=isp,Control=isp},CancellationToken.None);
        Check(intercepted.Comparison==ResolverComparison.Shared&&intercepted.Summary.Contains("не подтверждён")&&intercepted.Report.Contains("могут перенаправляться"),"Matching ISP nodes in both Plain paths do not establish Comss identity");
        Check(r.Report.Contains("DoH браузера")&&r.Report.Contains("Настройки сети не менялись"),"Scope states browser limitation and read-only behavior");
        Check(!r.Detail.Contains("system123456")&&!r.Detail.Contains("testnonce"),"Session and nonce never enter diagnostics");
        Check(r.Report.Contains("8.8.8.8")&&r.Report.Contains("Test network")&&r.Provider=="Comss.one", "Report includes observed nodes, networks and control provider");
        f=new Fake{Control=new[]{Node("1.1.1.1")}};r=ResolverCheck.Run(0,DnsTransport.Plain,f,CancellationToken.None);
        Check(r.Comparison==ResolverComparison.DifferentNodes&&r.Report.Contains("Перехват и утечка не доказаны"),"Disjoint pools are not automatically classified as a leak");
        f=new Fake{NoData=true};r=ResolverCheck.Run(0,DnsTransport.Tls,f,CancellationToken.None);
        Check(r.Comparison==ResolverComparison.Incomplete&&f.Pauses==2&&f.Collected==4,"Empty observer response stays inconclusive with only one retry per sample");
        Check(r.Report.Contains("не означает, что ваш DNS не работает"),"Observer outage is separate from DNS failure");
        f=new Fake{Fail=true};r=ResolverCheck.Run(0,DnsTransport.Https,f,CancellationToken.None);
        Check(r.Comparison==ResolverComparison.Incomplete&&!r.Detail.Contains("must-not-log"),"HTTP or parsing failure is neutral and excludes raw server exception text");
        Check(new ResolverCheckResult().Comparison==ResolverComparison.Incomplete,"Timeout/failure result cannot default to shared success");
        f=new Fake{Changed=true};r=ResolverCheck.Run(0,DnsTransport.Https,f,CancellationToken.None);
        Check(r.Comparison==ResolverComparison.NetworkChanged&&r.Report.Contains("не относится к текущей сети"),"Network change invalidates otherwise matching samples without reapplying DNS");
        f=new Fake{LookupFailure=true};r=ResolverCheck.Run(0,DnsTransport.Https,f,CancellationToken.None);
        Check(r.Comparison==ResolverComparison.Shared&&r.Detail.Contains("lookup errors=6"),"Negative answers can still have valid authoritative observations");
        using(var stop=new CancellationTokenSource()){stop.Cancel();f=new Fake();bool cancelled=false;try{ResolverCheck.Run(0,DnsTransport.Https,f,stop.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&f.Created==0&&f.Queries==0,"Cancelled operation sends no new observer requests");}
        foreach(string id in new[]{"", "short", "abc1234567.evil", "abc1234567/evil", "ABC1234567", new string('a',33)})Throws(()=>ResolverCheck.ValidateTicket(new ResolverTicket{Id=id,Nonce="ok"}),"Reject observer-supplied hostname injection: "+id.Length);
        Throws(()=>ResolverCheck.ValidateTicket(new ResolverTicket{Id="good123456",Nonce="x&evil=1"}),"Reject observer-supplied form injection");
        var rows=new object[]{new Dictionary<string,object>{{"ip","8.8.8.8"},{"asn","Test\nnetwork"}},new Dictionary<string,object>{{"ip","8.8.8.8"}}};
        var nodes=MangoResolverCheckIo.ParseNodes(new Dictionary<string,object>{{"servers",rows}});
        Check(nodes.Length==1&&nodes[0].Network=="Testnetwork","Deduplicate valid nodes and remove control characters from labels");
        foreach(string ip in new[]{"127.0.0.1","192.168.0.1","::1","fe80::1","fd00::1","::ffff:127.0.0.1","invalid"}) {
            var invalid=new Dictionary<string,object>{{"servers",new object[]{new Dictionary<string,object>{{"ip",ip}}}}};
            Throws(()=>MangoResolverCheckIo.ParseNodes(invalid),"Reject malformed/non-public observed address "+ip);
        }
        Throws(()=>MangoResolverCheckIo.ParseNodes(new Dictionary<string,object>()),"Missing results are not a successful empty test");
        Check(ResolverCheck.ServiceIndex==ServiceAvailability.Targets.Length,"Resolver test follows the availability options without a generation slot");
        Console.WriteLine("All "+passed+" resolver checks passed; fake transport only, no network settings changed.");
    }
}
