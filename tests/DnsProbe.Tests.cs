using System;
using System.Linq;
using HbAi.XboxDns;
static class ProbeTests
{
    static int passed;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(byte[] q, byte[] response, string name) { bool rejected = false; try { DnsProbe.Validate(q, response); } catch (System.IO.IOException) { rejected = true; } Check(rejected, name); }
    static void Main(string[] args)
    {
        byte[] q = DnsProbe.Query("example.com");
        bool resolved=false;
        var pinned=DohHttp2.ResolverAddresses(EncryptedDns.Templates[0],host=>{resolved=true;return new[]{"127.0.1.1"};});
        Check(!resolved&&pinned.SequenceEqual(new[]{"195.133.25.16"}),"Comss test uses configured Windows DoH IP without resolving its poisoned hostname");
        var geo=DohHttp2.ResolverAddresses(EncryptedDns.Templates[1],host=>new[]{"127.0.1.1","193.233.112.68"});
        Check(geo.SequenceEqual(new[]{"193.233.112.68"}),"Other provider addresses are resolved separately and local targets are rejected");
        Check(!DohHttp2.CanRetry(new System.Security.Authentication.AuthenticationException("wrong hostname certificate")),"Direct HTTPS never retries around a certificate validation failure");
        int attempts=0;var trace=new System.Collections.Generic.List<string>();
        var retried=DohHttp2.QueryReliable("https://resolver.test/dns-query",q,(url,packet)=>{attempts++;if(attempts==1)throw new System.ComponentModel.Win32Exception(12002,"simulated timeout");return packet;},trace.Add);
        Check(attempts==2&&retried==q&&trace.Any(line=>line.Contains("attempt=1"))&&trace.Any(line=>line.Contains("attempt=2")),"Temporary DoH timeout is retried once with both attempts logged");
        attempts=0;try{DohHttp2.QueryReliable("https://resolver.test/dns-query",q,(url,packet)=>{attempts++;throw new System.ComponentModel.Win32Exception(12029);},null);}catch(System.ComponentModel.Win32Exception){}
        Check(attempts==2,"Persistent network failure stops after two HTTPS attempts");
        foreach(Exception failure in new Exception[]{new System.ComponentModel.Win32Exception(12175,"certificate failure"),new System.IO.IOException("DoH HTTP 403"),new System.IO.IOException("malformed DNS")}) {
            attempts=0;try{DohHttp2.QueryReliable("https://resolver.test/dns-query",q,(url,packet)=>{attempts++;throw failure;},null);}catch(Exception){}
            Check(attempts==1,"Certificate, HTTP and data errors are never bypassed or retried: "+failure.Message);
        }
        foreach(string endpoint in new[]{"http://resolver.test/dns-query","https://user:secret@resolver.test/dns-query","https://resolver.test:444/dns-query","https://resolver.test/dns-query?token=secret"}) {
            bool rejected=false;try {DohHttp2.Query(endpoint,q);}catch(System.IO.IOException){rejected=true;}
            Check(rejected,"HTTP/2 transport rejects unsafe endpoint before connecting");
        }
        var answer = q.Concat(new byte[] {192,12,0,1,0,1,0,0,0,60,0,4,93,184,215,14}).ToArray();
        answer[2] = 129; answer[3] = 128; answer[7] = 1;
        DnsProbe.Validate(q, answer); Check(true, "Valid matching A response accepted");
        Check(DnsProbe.Addresses(q,answer).SequenceEqual(new[]{"93.184.215.14"}),"Resolved candidate IP is extracted from validated DNS packet");
        var bad = (byte[])answer.Clone(); bad[0] ^= 1; Reject(q,bad,"Wrong transaction rejected");
        bad = (byte[])answer.Clone(); bad[13] ^= 1; Reject(q,bad,"Different question rejected");
        bad = (byte[])answer.Clone(); bad[3] = 131; Reject(q,bad,"NXDOMAIN is not success");
        bad = (byte[])answer.Clone(); bad[2] |= 2; Reject(q,bad,"Truncation is not success");
        bad = (byte[])answer.Clone(); Array.Clear(bad,bad.Length-4,4); Reject(q,bad,"Blocked zero address rejected");
        Reject(q,answer.Take(answer.Length-2).ToArray(),"Incomplete A record rejected");
        bad = (byte[])answer.Clone(); bad[7]=0; Reject(q,bad,"Empty answer rejected");
        Check(Dns.V4.Length == Dns.V6.Length && Dns.Name.Length == Dns.Opens.Length && Dns.Name.Length == Dns.Url.Length, "Provider catalog aligned");
        for(int k=0;k<Dns.V4.Length;k++) Check(Dns.MatchProvider(Dns.V4[k]) == k, "Complete configured pair maps to provider " + k);
        Check(Dns.MatchProvider(new[] { Dns.V4[0][0], Dns.V4[4][0] }) == -1, "Mixed old and new servers do not select first provider");
        Check(EncryptedDns.Templates[0] == "https://dns.comss.one/dns-query" && EncryptedDns.ServersFor(0).SequenceEqual(new[] {"195.133.25.16"}), "Comss DoH uses its published dedicated address and endpoint");
        Check(Dns.MatchProvider(EncryptedDns.ServersFor(0)) == 0, "Comss encrypted address is recognized after apply and restart");
        Check(Dns.MatchProvider(new[] {"195.133.25.16", "192.0.2.53"}) == -1, "Mixed encrypted Comss and custom resolver is rejected");
        Check(Dns.V4[0].SequenceEqual(new[] {"83.220.169.155", "212.109.195.93"}), "Comss plain DNS remains separate from its encrypted address");
        Check(Dns.EncryptedOnly(5) && EncryptedDns.Templates[5] == "https://dns.dns-ai.ru/dns-query" && Dns.V4[5].SequenceEqual(new[]{"194.87.196.13","45.151.102.219"}) && EncryptedDns.ServersFor(5).SequenceEqual(Dns.V4[5]), "DNS-AI uses current published addresses and requires encryption");
        Check(Dns.MatchProvider(new[]{"194.87.196.13","186.246.49.127"})==5 && Dns.KnownServers.Contains("186.246.49.127"),"Retired DNS-AI pair remains recognized and recoverable");
        Check(!EncryptedDns.ServersFor(5).Contains("186.246.49.127") && Dns.MatchProvider(new[]{"186.246.49.127","45.151.102.219"})==-1 && Dns.MatchProvider(new[]{"186.246.49.127"})==-1,"Retired address is never newly applied; partial and mixed legacy sets are not attributed to DNS-AI");
        Check(Dns.Name[6]=="MagicDNS" && Dns.V4[6].SequenceEqual(new[]{"109.120.132.43","94.249.177.102"}) && EncryptedDns.Templates[6]=="https://dns.magicdns.ru/dns-query" && !Dns.EncryptedOnly(6), "MagicDNS supports published plain and HTTPS endpoints");
        Check(new[]{Dns.V4.Length,Dns.V6.Length,Dns.Site.Length,Dns.Url.Length,Dns.Opens.Length,EncryptedDns.Templates.Length}.All(n=>n==Dns.Name.Length), "Provider metadata stays aligned for DNS changes and restoration");
        Check(DnsProbe.Targets(5).SequenceEqual(new[]{EncryptedDns.Templates[5]}), "DNS-AI tests target HTTPS only, not port 53");
        Check(DnsProbe.HostsForProvider(5).Contains("gemini.google.com") && DnsProbe.HostsForProvider(5).Contains("generativelanguage.googleapis.com"), "DNS-AI checks both Gemini website and API");
        var geminiResults = DnsProbe.CheckProvider(5,(server,host)=>new DnsProbeResult{Server=server,Host=host,Milliseconds=15,Error=host=="gemini.google.com"?"Gemini test failure":null});
        Check(geminiResults.Count(r=>r.Host=="gemini.google.com")==1 && DnsProbe.Report(5,geminiResults).Contains("✕ gemini.google.com — Gemini test failure"), "DNS-AI Gemini failure is tested through DoH and shown in report");
        var secure=new FamilyBackup{Family="IPv4",Manual=true,Servers=Dns.V4[5],DohCaptured=true,Doh=EncryptedDns.Strict(2,EncryptedDns.Templates[5])};
        Check(EncryptedDns.ExplicitlyConfigured(secure,5),"DNS-AI encrypted readback recognized");
        secure.Doh[0].Flags=6;Check(!EncryptedDns.ExplicitlyConfigured(secure,5),"DNS-AI with UDP fallback is not recognized as configured");
        secure.Doh=new DohConfig[0];Check(!EncryptedDns.ExplicitlyConfigured(secure,5),"Plain DNS-AI addresses are not enough for success");
        secure.Servers=new[]{"194.87.196.13","186.246.49.127"};secure.Doh=EncryptedDns.Strict(2,EncryptedDns.Templates[5]);
        Check(Dns.MatchProvider(secure.Servers)==5&&EncryptedDns.ExplicitlyConfigured(secure,5),"Old DNS-AI connection is recognized with strict DoH metadata");
        secure.Doh[1].Template="https://other.example/dns-query";Check(!EncryptedDns.ExplicitlyConfigured(secure,5),"Legacy addresses cannot hide changed encryption settings");
        long time=0;var health=new DohAddressHealth(()=>time);string endpoint5=EncryptedDns.Templates[5];var addresses=EncryptedDns.ServersFor(5);
        var sent=new System.Collections.Generic.List<string>();
        Func<string,string,byte[],byte[]> failFirst=(url,address,packet)=>{Check(url==endpoint5,"Pinned address retains original HTTPS endpoint");sent.Add(address);if(address==addresses[0])throw new System.ComponentModel.Win32Exception(12029);return answer;};
        var healthy=DohHttp2.QueryHealthyAddresses(endpoint5,q,addresses,health,failFirst,trace.Add);
        Check(healthy==answer&&sent.SequenceEqual(addresses),"Network failure tries the next published address and validates its DNS response");
        sent.Clear();DohHttp2.QueryHealthyAddresses(endpoint5,q,addresses,health,failFirst,trace.Add);
        Check(sent.SequenceEqual(new[]{addresses[1]})&&trace.Any(line=>line.Contains("temporarily skipped addresses=")),"Following host checks skip the failed address and log the skip");
        long version;time=29999;Check(health.Ready(endpoint5,addresses,out version).SequenceEqual(new[]{addresses[1]}),"Failed address cooldown lasts 30 seconds");
        time=30000;Check(health.Ready(endpoint5,addresses,out version).SequenceEqual(addresses),"Failed address is retried when its cooldown expires");
        Check(health.Ready("https://another.example/dns-query",addresses,out version).SequenceEqual(addresses),"Address cooldown is scoped to its HTTPS resolver");
        health.Reset();sent.Clear();DohHttp2.QueryHealthyAddresses(endpoint5,q,addresses,health,failFirst,null);
        Check(sent.SequenceEqual(addresses),"Manual retry or network change reset makes both addresses available immediately");
        health.Ready(endpoint5,addresses,out version);health.Reset();health.Failure(endpoint5,addresses[0],version);
        Check(health.Ready(endpoint5,addresses,out version).Length==2,"An old in-flight failure cannot repopulate the cache after reconnect");
        sent.Clear();
        try{DohHttp2.QueryHealthyAddresses(endpoint5,q,addresses,health,(url,address,packet)=>{sent.Add(address);throw new TimeoutException();},null);}catch(System.IO.IOException){}
        Check(sent.Count==2,"Both unreachable addresses are tried once");
        try{DohHttp2.QueryHealthyAddresses(endpoint5,q,addresses,health,(url,address,packet)=>{sent.Add(address);return answer;},null);}catch(System.IO.IOException){}
        Check(sent.Count==2,"When all addresses are cooling down later host checks do not repeat their timeouts");
        time+=30000;DohHttp2.QueryHealthyAddresses(endpoint5,q,addresses,health,(url,address,packet)=>answer,null);
        health.Ready(endpoint5,addresses,out version);health.Failure(endpoint5,addresses[0],version);time+=30000;
        Check(health.Ready(endpoint5,addresses,out version).Contains(addresses[0]),"A successful reply resets the address failure history");
        health.Reset();long expected=30000;
        for(int n=0;n<9;n++) {
            health.Ready(endpoint5,addresses,out version);health.Failure(endpoint5,addresses[0],version);time+=expected-1;
            Check(!health.Ready(endpoint5,addresses,out version).Contains(addresses[0]),"Repeated failures back off, capped at 15 minutes, step "+n);
            time++;Check(health.Ready(endpoint5,addresses,out version).Contains(addresses[0]),"Cooldown expires at its exact deadline, step "+n);expected=Math.Min(900000,expected*2);
        }
        foreach(Exception error in new Exception[]{new System.ComponentModel.Win32Exception(12175),new System.IO.IOException("DoH HTTP 403"),new System.IO.IOException("malformed DNS"),new DohHttp2.AddressPinningUnavailableException()}) {
            health.Reset();int calls=0;bool failed=false;
            try{DohHttp2.QueryHealthyAddresses(endpoint5,q,addresses,health,(url,address,packet)=>{calls++;throw error;},null);}catch(Exception){failed=true;}
            Check(failed&&calls==1&&health.Ready(endpoint5,addresses,out version).Length==2,"Certificate, HTTP, data and platform errors neither switch addresses nor poison their health: "+error.GetType().Name);
        }
        health.Reset();int malformedCalls=0;bool invalidResponse=false;
        try{DohHttp2.QueryHealthyAddresses(endpoint5,q,addresses,health,(url,address,packet)=>{malformedCalls++;return q;},null);}catch(System.IO.IOException){invalidResponse=true;}
        Check(invalidResponse&&malformedCalls==1,"An invalid DNS reply is rejected before marking an address healthy");
        foreach(string unsafeAddress in new[]{"127.0.0.1","not-an-ip","::1"}) {
            bool rejected=false;try{DohHttp2.Query(endpoint5,q,5000,unsafeAddress);}catch(System.IO.IOException){rejected=true;}
            Check(rejected,"Pinned WinHTTP request rejects invalid or local address before connecting: "+unsafeAddress);
        }
        var vpn = VpnCheck.Scan(); Console.WriteLine("Read-only VPN scan: " + vpn.Label + "; " + string.Join(", ", vpn.Connections));
        for (int k=0;k<Dns.Name.Length;k++)
        {
            var hosts = DnsProbe.HostsForProvider(k);
            Check(hosts.Contains("example.com") && hosts.Distinct().Count() == hosts.Length, Dns.Name[k] + " has a unique test suite and baseline");
            Check(k == 3 ? hosts.Contains("xsts.auth.xboxlive.com") && !hosts.Contains("claude.ai") : k == 6 ? hosts.Contains("gemini.google.com") && hosts.Contains("claude.ai") && !hosts.Contains("cloudcode-pa.googleapis.com") : hosts.Contains("cloudcode-pa.googleapis.com"), Dns.Name[k] + " tests its intended services");
            var results = DnsProbe.CheckProvider(k, (server,host) => new DnsProbeResult {Server=server,Host=host,Milliseconds=12,Error=host=="example.com"?null:"simulated failure"});
            Check(results.Length == hosts.Length * DnsProbe.Targets(k).Length && DnsProbe.Targets(k).All(server => results.Count(r=>r.Server==server) == hosts.Length), Dns.Name[k] + " tests every host on each selected transport target");
            string report = DnsProbe.Report(k,results);
            Check(DnsProbe.Targets(k).All(report.Contains) && report.Contains("12 мс") && report.Contains("simulated failure"), Dns.Name[k] + " report contains transport targets, timing and failure");
            if(args.Contains("--live")) foreach (var result in DnsProbe.CheckProvider(k)) Console.WriteLine(result);
        }
        if(args.Contains("--https")) foreach(var template in EncryptedDns.Templates.Where(t=>t!=null)) {
            var result=DnsProbe.CheckHttps(template,"example.com");
            Check(result.Ok,"HTTPS probe: " + template + (result.Error==null?"":" · "+result.Error));
        }
        if(args.Contains("--https")) foreach(string host in new[]{"gemini.google.com","generativelanguage.googleapis.com"}) {
            var result=DnsProbe.CheckHttps(EncryptedDns.Templates[5],host);
            Check(result.Ok,"DNS-AI live Gemini HTTPS: "+host+" · "+(result.Error??result.Milliseconds+" ms"));
        }
        Console.WriteLine("All " + passed + " checks passed; no settings changed.");
    }
}
