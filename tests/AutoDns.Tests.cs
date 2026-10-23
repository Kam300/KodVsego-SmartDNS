using System;
using System.IO;
using System.Linq;
using System.Threading;
using HbAi.XboxDns;

static class AutoDnsTests
{
    sealed class Probe : IAutoDnsProbe
    {
        internal int calls,encryptedCalls,badAddressAttempts;
        internal bool FailAll,FailDaily,Challenge;
        internal int SuccessProvider=4;
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel)
        {
            Interlocked.Increment(ref calls);if(encrypted)Interlocked.Increment(ref encryptedCalls);
            if(FailAll || provider!=SuccessProvider || FailDaily&&host.StartsWith("daily-"))throw new IOException("simulated DNS failure");
            return new[]{"109.120.131.201","109.120.131.200"};
        }
        public void Https(string host,string address,bool api,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if(address.EndsWith("201")){Interlocked.Increment(ref badAddressAttempts);throw new IOException("simulated relay timeout");}
            if(Challenge)throw new BrowserChallengeException();
        }
    }
    static int passed;
    static void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
    static void Main()
    {
        Check(AutoDns.Goals.Select(g=>g.Name).SequenceEqual(new[]{"Gemini","ChatGPT","Claude","ElevenLabs","Spotify","Xbox","Antigravity"}),"Service goals exposed without changing provider indices");
        Check(AutoDns.GoalIndex("ChatGPT")==1&&AutoDns.GoalIndex("gemini")==0&&AutoDns.GoalIndex(null)==-1&&AutoDns.GoalIndex("obsolete service")==-1,"Saved service names reload safely without relying on numeric order");
        Check(!AutoDns.Candidates(0,false).Contains(3)&&AutoDns.Candidates(5,false).SequenceEqual(new[]{3}),"Xbox and AI candidates remain separate");
        Check(!AutoDns.Candidates(6,false).Contains(0)&&!AutoDns.Candidates(2,false).Contains(4),"Unsupported Comss Antigravity and Bezmezhau Claude combinations excluded");
        Check(AutoDns.Candidates(0,true).SequenceEqual(new[]{0,4,5,6})&&!AutoDns.Candidates(5,true).Any(),"Strict encryption never uses disabled GeoHide or unencrypted Xbox");
        Check(!Dns.IsAvailable(1)&&!Dns.IsAvailable(2)&&!Dns.IsAvailable(-1)&&!Dns.IsAvailable(7),"Both GeoHide variants are disabled with stable historical IDs");
        Check(AutoDns.Candidates(6,false).SequenceEqual(new[]{4,5,6})&&AutoDns.Candidates(6,true).SequenceEqual(new[]{4,5,6}),"MagicDNS joins Antigravity selection in plain and encrypted modes");
        var switches=(bool[])typeof(ProviderAvailability).GetField("Enabled",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
        var savedSwitches=(bool[])switches.Clone();
        try {
            switches[1]=false;switches[2]=false;
            Check(!Dns.AvailableProviders.Contains(1)&&!Dns.AvailableProviders.Contains(2),"A disabled provider disappears from choices");
            Check(Enumerable.Range(0,AutoDns.Goals.Length).All(g=>AutoDns.Candidates(g,false).All(Dns.IsAvailable)),"The same switch excludes disabled providers from auto selection");
            Check(Dns.MatchProvider(Dns.V4[1])==1&&Dns.MatchProvider(Dns.V4[2])==2,"Disabled provider still recognized for recovery");
            Check(new[]{1,2}.All(p=>Dns.ExecuteDirect(true,p).Contains("временно недоступен")),"Stale normal commands cannot enable disabled provider");
            Check(new[]{1,2}.All(p=>EncryptedDns.Run(p).Contains("временно недоступен")),"Same switch blocks encrypted DNS activation");
            Check(new[]{0,2}.All(p=>AntigravityDns.Run(true,false,p).Contains("временно недоступен")),"Same switch blocks Antigravity rules");
        } finally {Array.Copy(savedSwitches,switches,switches.Length);}
        var fake=new Probe();var results=AutoDns.Search(0,false,fake,CancellationToken.None);
        Check(results.Length==4&&AutoDns.Best(results).Provider==4,"Only an enabled candidate with both DNS and HTTPS succeeds");
        Check(fake.badAddressAttempts==1,"Failed relay IP is tried before alternative resolved address");
        string diagnostic=AutoDns.Diagnostic(0,results);
        Check(diagnostic.Contains("Auto goal=Gemini")&&diagnostic.Contains("109.120.131.201")&&diagnostic.Contains("simulated relay timeout")&&diagnostic.Contains("HTTPS OK")&&diagnostic.Contains("Total=")&&diagnostic.Contains("IOException"),"Developer log keeps failed IP attempts, error type, timings and eventual success");
        Check(results.Single(r=>r.Provider==5).Encrypted&&fake.encryptedCalls==1,"DNS-AI always uses encryption even in mixed search");
        Check(AutoDns.Best(AutoDns.Search(0,false,new Probe{FailAll=true},CancellationToken.None))==null,"All failed candidates produce no selection");
        var currentOnly=new Probe();
        var health=AutoDns.Search(0,false,currentOnly,CancellationToken.None,null,4);
        Check(health.Length==1&&health[0].Provider==4&&currentOnly.calls==1,"Background health check only probes current provider");
        Check(AutoDns.Best(AutoDns.Search(6,true,new Probe{FailDaily=true},CancellationToken.None))==null,"Antigravity requires both endpoint domains");
        foreach(bool encrypted in new[]{false,true}) {
            var magicProbe=new Probe{SuccessProvider=6};
            var magic=AutoDns.Search(6,encrypted,magicProbe,CancellationToken.None,null,6).Single();
            Check(magic.Ok&&magic.Provider==6&&magic.Encrypted==encrypted&&magicProbe.calls==2&&magicProbe.encryptedCalls==(encrypted?2:0),"MagicDNS checks both Antigravity hosts with the requested transport: DoH="+encrypted);
            Check(!AutoDns.Search(6,encrypted,new Probe{SuccessProvider=6,FailDaily=true},CancellationToken.None,null,6).Single().Ok,"MagicDNS cannot pass with a failed second Antigravity host: DoH="+encrypted);
        }
        var token=new CancellationTokenSource();token.Cancel();bool cancelled=false;
        try{AutoDns.Search(0,false,new Probe(),token.Token);}catch(OperationCanceledException){cancelled=true;}
        Check(cancelled,"Cancelled selection cannot yield a provider");token.Dispose();
        Check(AutoDns.Best(new[]{new AutoDnsResult{Provider=0,Error="timeout",Milliseconds=1},new AutoDnsResult{Provider=1,Milliseconds=20},new AutoDnsResult{Provider=4,Milliseconds=10}}).Provider==4,"Fastest successful candidate wins, never a fast failure");
        foreach(string ip in new[]{"127.0.0.1","10.0.0.1","172.16.0.1","192.168.1.1","169.254.1.1","100.64.0.1","0.0.0.0","224.0.0.1","::1"})Check(!AutoDnsProbe.PublicAddress(ip),"Reject local/invalid relay: "+ip);
        Check(AutoDnsProbe.PublicAddress("109.120.131.200"),"Public relay eligible for verified TLS connection");
        Check(!AutoDnsProbe.AcceptStatus(403,false)&&!AutoDnsProbe.AcceptStatus(500,false)&&AutoDnsProbe.AcceptStatus(200,false),"Forbidden, challenge and server error are not website successes");
        Check(AutoDnsProbe.AcceptStatus(401,true)&&AutoDnsProbe.AcceptStatus(404,true)&&!AutoDnsProbe.AcceptStatus(403,true),"API connectivity permits missing authentication or root route, never forbidden");
        bool challenge=false,forbidden=false;
        try{AutoDnsProbe.CheckResponse(403,"HTTP/1.1 403 Forbidden\r\nCf-Mitigated: challenge\r\nServer: cloudflare\r\n",false);}catch(BrowserChallengeException){challenge=true;}
        Check(challenge,"Cloudflare challenge marker is separate from DNS failure");
        try{AutoDnsProbe.CheckResponse(403,"Server: cloudflare\r\n",false);}catch(BrowserChallengeException){throw new Exception("Bare forbidden incorrectly called a challenge");}catch(IOException){forbidden=true;}
        Check(forbidden,"Unmarked 403 remains unconfirmed, not automatically accepted");
        var challenged=AutoDns.Search(1,false,new Probe{Challenge=true},CancellationToken.None);
        Check(AutoDns.Best(challenged)!=null&&AutoDns.Best(challenged).NeedsBrowser,"Challenge-only route can be offered explicitly as try in browser");
        Check(AutoDns.Best(new[]{new AutoDnsResult{Provider=0,NeedsBrowser=true,Milliseconds=1},new AutoDnsResult{Provider=1,Milliseconds=50}}).Provider==1,"Verified response outranks a faster browser challenge");
        Check(AutoDns.Report(1,challenged).Contains("проверка в браузере"),"Report never labels a browser challenge as verified access");
        Check(AutoDns.Report(0,results).Contains("Вход в аккаунт и работа модели не проверялись")&&AutoDns.Report(0,results).Contains("общий DNS компьютера"),"Report explains verification limits and system DNS scope");
        Console.WriteLine("All "+passed+" auto-selection checks passed; fake probes only, no network or DNS changes.");
    }
}
