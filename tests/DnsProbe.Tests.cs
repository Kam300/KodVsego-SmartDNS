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
        Check(Dns.EncryptedOnly(5) && EncryptedDns.Templates[5] == "https://dns.dns-ai.ru/dns-query" && Dns.V4[5].SequenceEqual(new[]{"194.87.196.13","186.246.49.127"}), "DNS-AI uses published addresses and requires encryption");
        Check(DnsProbe.Targets(5).SequenceEqual(new[]{EncryptedDns.Templates[5]}), "DNS-AI tests target HTTPS only, not port 53");
        Check(DnsProbe.HostsForProvider(5).Contains("gemini.google.com") && DnsProbe.HostsForProvider(5).Contains("generativelanguage.googleapis.com"), "DNS-AI checks both Gemini website and API");
        var geminiResults = DnsProbe.CheckProvider(5,(server,host)=>new DnsProbeResult{Server=server,Host=host,Milliseconds=15,Error=host=="gemini.google.com"?"Gemini test failure":null});
        Check(geminiResults.Count(r=>r.Host=="gemini.google.com")==1 && DnsProbe.Report(5,geminiResults).Contains("✕ gemini.google.com — Gemini test failure"), "DNS-AI Gemini failure is tested through DoH and shown in report");
        var secure=new FamilyBackup{Family="IPv4",Manual=true,Servers=Dns.V4[5],DohCaptured=true,Doh=EncryptedDns.Strict(2,EncryptedDns.Templates[5])};
        Check(EncryptedDns.ExplicitlyConfigured(secure,5),"DNS-AI encrypted readback recognized");
        secure.Doh[0].Flags=6;Check(!EncryptedDns.ExplicitlyConfigured(secure,5),"DNS-AI with UDP fallback is not recognized as configured");
        secure.Doh=new DohConfig[0];Check(!EncryptedDns.ExplicitlyConfigured(secure,5),"Plain DNS-AI addresses are not enough for success");
        var vpn = VpnCheck.Scan(); Console.WriteLine("Read-only VPN scan: " + vpn.Label + "; " + string.Join(", ", vpn.Connections));
        for (int k=0;k<Dns.Name.Length;k++)
        {
            var hosts = DnsProbe.HostsForProvider(k);
            Check(hosts.Contains("example.com") && hosts.Distinct().Count() == hosts.Length, Dns.Name[k] + " has a unique test suite and baseline");
            Check(k == 3 ? hosts.Contains("xsts.auth.xboxlive.com") && !hosts.Contains("claude.ai") : hosts.Contains("cloudcode-pa.googleapis.com"), Dns.Name[k] + " tests its intended services");
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
