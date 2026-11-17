using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HbAi.XboxDns;

static class EndpointAddressHealthTests {
    static int passed;static void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    sealed class Probe : IAutoDnsProbe {
        internal readonly List<string> Attempts=new List<string>();internal string[] Addresses={"109.120.131.201","109.120.131.202","109.120.131.203","109.120.131.204"};
        internal bool Region,AllRegion,Quota,Transport,Generic;
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel){return Addresses;}
        public void Https(string host,string address,bool api,CancellationToken cancel){lock(Attempts)Attempts.Add(host+"|"+address);if(address!=Addresses.Last())throw new TimeoutException("timeout");}
        public ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel){
            lock(Attempts)Attempts.Add(endpoint.Host+"|"+address);
            if(Transport)throw new ServiceTransportException("TLS",new TimeoutException("timeout"));
            if(Quota)return Reply(429,"");
            if(Generic)return Reply(400,"{\"error\":{\"message\":\"bad request\"}}");
            if(!endpoint.Api&&AllRegion)return Reply(200,"{}");
            if(!endpoint.Api&&Region&&address!=Addresses.Last())throw new ServiceTransportException("TLS",new TimeoutException("timeout"));
            if(AllRegion||Region&&address!=Addresses.Last())return Reply(400,"{\"error\":{\"message\":\"User location is not supported\"}}");
            return Reply(endpoint.Api?401:200,"{}");
        }
        static ServiceHttpReply Reply(int status,string body){var reply=new ServiceHttpReply{Status=status,Body=body};reply.Headers["Content-Type"]="application/json";return reply;}
    }
    static void Main(){
        long now=1000;long version;var health=new EndpointAddressHealth(()=>now);health.Context("wifi-A");string host="generativelanguage.googleapis.com",a="109.120.131.201",b="109.120.131.204";
        Check(health.Ready(host,new[]{a,b},out version).SequenceEqual(new[]{a,b}),"Fresh service addresses retain resolver order");
        health.Reachable(host,b,version);Check(health.Ready(host,new[]{a,b},out version).First()==b,"Recently reachable HTTPS address is preferred");
        health.TransportFailed(host,b,version);Check(health.Ready(host,new[]{a,b},out version).SequenceEqual(new[]{a}),"Transport timeout benches only the affected host/IP");
        Check(health.Ready("other.example",new[]{b},out version).Single()==b,"A failure does not contaminate a different service host");
        health.Context("wifi-B");Check(health.Ready(host,new[]{b},out version).Single()==b,"New network releases transport penalties");
        health.RegionFailed(host,b,version);health.Context("wifi-C");Check(health.Ready(host,new[]{b},out version).Length==0,"Explicit region refusal survives a context reset");
        now+=299999;Check(health.Ready(host,new[]{b},out version).Length==0,"Region refusal remains excluded before five minutes");
        now++;Check(health.Ready(host,new[]{b},out version).Single()==b,"Region refusal expires after five minutes");
        health.RegionFailed(host,b,version);health.Reachable(host,b,version);Check(health.Ready(host,new[]{b},out version).Length==0,"A generic passing probe cannot clear a region refusal");
        health.ClientAnswer(host,b);Check(health.Ready(host,new[]{b},out version).Single()==b,"Exact client answer can clear that endpoint's refusal");
        long oldVersion=version;health.Context("new-wifi");health.TransportFailed(host,b,oldVersion);Check(health.Ready(host,new[]{b},out version).Length==1,"Late response cannot poison a new network context");
        health.Reachable(host,b,version);Check(health.Ready(host,new[]{a},out version).SequenceEqual(new[]{a}),"Cached success never injects an IP absent from the current provider's answer");
        Check(health.Ready(host,new[]{"127.0.0.1","10.0.0.1",a,a},out version).SequenceEqual(new[]{a}),"Only distinct public IPv4 candidates reach HTTPS");
        var many=Enumerable.Range(1,40).Select(n=>"109.120.132."+n);Check(health.Ready(host,many,out version).Length==8,"Endpoint attempts stay capped at eight addresses");
        health=new EndpointAddressHealth(()=>now);var probe=new Probe{Region=true};
        var result=ServiceAvailability.Check(1,0,true,probe,CancellationToken.None,1,health);
        Check(result.CanRecommend&&result.Endpoints.All(e=>e.Address==probe.Addresses.Last()),"Explicit region 400 on the first three addresses does not hide a fourth working endpoint");
        Check(probe.Attempts.Count==8,"Website and API each try all four candidate addresses sequentially");
        probe.Attempts.Clear();result=ServiceAvailability.Check(1,0,true,probe,CancellationToken.None,1,health);
        Check(result.CanRecommend&&probe.Attempts.Count==2&&probe.Attempts.All(ip=>ip.EndsWith(probe.Addresses.Last())),"Repeat search uses healthy addresses without repeating refused endpoints");
        health=new EndpointAddressHealth(()=>now);probe=new Probe{AllRegion=true};ServiceAvailability.Check(1,0,true,probe,CancellationToken.None,1,health);probe.Attempts.Clear();
        result=ServiceAvailability.Check(1,0,true,probe,CancellationToken.None,1,health);
        Check(!result.CanRecommend&&result.Endpoints.Single(e=>e.Endpoint.Api).RegionSuppressed&&probe.Attempts.Count==1,"All-region cooldown is explicit and avoids repeated doomed API connections");
        health=new EndpointAddressHealth(()=>now);probe=new Probe{Quota=true};ServiceAvailability.Check(1,0,true,probe,CancellationToken.None,1,health);probe.Attempts.Clear();ServiceAvailability.Check(1,0,true,probe,CancellationToken.None,1,health);
        Check(probe.Attempts.Count==2,"HTTP 429 does not blacklist addresses");
        health=new EndpointAddressHealth(()=>now);probe=new Probe{Generic=true};ServiceAvailability.Check(1,0,true,probe,CancellationToken.None,1,health);probe.Attempts.Clear();ServiceAvailability.Check(1,0,true,probe,CancellationToken.None,1,health);
        Check(probe.Attempts.Count==2,"Unclassified HTTP 400 is not assumed to be a region refusal");
        health=new EndpointAddressHealth(()=>now);probe=new Probe();int goal=AutoDns.GoalIndex("Antigravity");
        var auto=AutoDns.Search(goal,true,probe,CancellationToken.None,null,6,false,new[]{6},health).Single();
        Check(auto.Ok&&auto.EndpointAddresses.Count==2&&auto.EndpointAddresses.Values.All(ip=>ip==probe.Addresses.Last()),"Antigravity HTTPS search reaches the fourth IP and records actual checked endpoints");
        Check(auto.Trace.Contains(probe.Addresses.Last()),"Selected endpoint evidence is present in diagnostics");
        Console.WriteLine("All "+passed+" endpoint health checks passed; fake clocks and HTTPS replies, no live network or settings writes.");
    }
}
