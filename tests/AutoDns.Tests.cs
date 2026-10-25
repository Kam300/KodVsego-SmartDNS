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
        internal bool FailAll,FailDaily,Challenge,ApiHtml,AnyProvider;
        internal int ApiStatus=401,activeRequests,maxRequests;
        internal string ApiBody="{\"error\":{\"message\":\"Missing authentication\"}}";
        internal int SuccessProvider=4;
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel)
        {
            Interlocked.Increment(ref calls);if(encrypted)Interlocked.Increment(ref encryptedCalls);
            if(FailAll || !AnyProvider&&provider!=SuccessProvider || FailDaily&&host.StartsWith("daily-"))throw new IOException("simulated DNS failure");
            return new[]{"109.120.131.201","109.120.131.200"};
        }
        public void Https(string host,string address,bool api,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if(address.EndsWith("201")){Interlocked.Increment(ref badAddressAttempts);throw new IOException("simulated relay timeout");}
            if(Challenge)throw new BrowserChallengeException();
        }
        public ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if(address.EndsWith("201")){Interlocked.Increment(ref badAddressAttempts);throw new IOException("simulated relay timeout");}
            int current=Interlocked.Increment(ref activeRequests);
            int previous;do{previous=maxRequests;if(previous>=current)break;}while(Interlocked.CompareExchange(ref maxRequests,current,previous)!=previous);
            try {
                if(AnyProvider)Thread.Sleep(15);
                var reply=new ServiceHttpReply{Status=endpoint.Api?ApiStatus:200,Body=endpoint.Api?ApiBody:null};
                reply.Headers["Content-Type"]=endpoint.Api&&!ApiHtml?"application/json":"text/html";
                if(Challenge&&!endpoint.Api){reply.Status=403;reply.Headers["cf-mitigated"]="challenge";}
                return reply;
            }finally{Interlocked.Decrement(ref activeRequests);}
        }
    }
    static int passed;
    static void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
    static void Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--live") {
            var live=AutoDns.Search(0,true,new AutoDnsProbe(true),CancellationToken.None,null,6,true);
            Console.WriteLine(AutoDns.Report(0,live)+"\n\n"+AutoDns.Diagnostic(0,live));return;
        }
        Check(AutoDns.Goals.Select(g=>g.Name).SequenceEqual(new[]{"Gemini","ChatGPT","Claude","ElevenLabs","Spotify","Antigravity"}),"Unavailable Xbox goal is hidden without changing historical provider IDs");
        Check(AutoDns.GoalIndex("ChatGPT")==1&&AutoDns.GoalIndex("gemini")==0&&AutoDns.GoalIndex(null)==-1&&AutoDns.GoalIndex("obsolete service")==-1,"Saved service names reload safely without relying on numeric order");
        Check(AutoDns.GoalIndex("Xbox")==-1&&Enumerable.Range(0,AutoDns.Goals.Length).All(g=>!AutoDns.Candidates(g,false).Contains(3)),"Saved Xbox goal falls back safely and Xbox DNS never participates in Auto");
        Check(!AutoDns.Candidates(AutoDns.GoalIndex("Antigravity"),false).Contains(0)&&!AutoDns.Candidates(2,false).Contains(4),"Unsupported Comss Antigravity and Bezmezhau Claude combinations excluded");
        Check(AutoDns.Candidates(0,true).SequenceEqual(new[]{0,4,5,6,7,8,9,10,13}),"Encrypted Auto includes the new providers with published Gemini support");
        Check(AutoDns.Candidates(0,true,true).SequenceEqual(new[]{0,4,5,6,7,8,9,10,13}),"TLS Auto follows confirmed TLS metadata and availability switches");
        Check(!Dns.IsAvailable(1)&&!Dns.IsAvailable(2)&&!Dns.IsAvailable(3)&&!Dns.IsAvailable(-1)&&!Dns.IsAvailable(Dns.Name.Length)&&Dns.IsAvailable(8),"Only the US GeoHide entry is enabled; historical RU/EU and Xbox IDs stay disabled");
        Check(AutoDns.Candidates(AutoDns.GoalIndex("Antigravity"),false).SequenceEqual(new[]{4,5,6,9,13})&&AutoDns.Candidates(AutoDns.GoalIndex("Antigravity"),true).SequenceEqual(new[]{4,5,6,9,13}),"Malw variants join Antigravity Auto only with published support");
        Check(Enumerable.Range(0,AutoDns.Goals.Length).All(g=>!AutoDns.Candidates(g,false).Contains(11)&&!AutoDns.Candidates(g,false).Contains(12)),"Webshift and filtering AdGuard are not advertised as AI unlockers without declared coverage");
        Check(Dns.Name.Length==14&&Enumerable.Range(7,7).All(Dns.IsAvailable)&&ProviderCatalog.All[8].key=="geohide-us","New providers append to the shared catalog without reviving GeoHide RU/EU");
        Check(ProviderCatalog.All[11].doh=="https://d.webshiftvpn.ru/dns-query"&&Dns.EncryptedOnly(11)&&!DotDns.Available(11)&&Dns.V4[11].SequenceEqual(new[]{"31.31.207.68"}),"Webshift uses its verified bootstrap with HTTPS only");
        Check(Dns.V4[12].SequenceEqual(new[]{"94.140.14.14","94.140.15.15"})&&EncryptedDns.Templates[12]=="https://dns.adguard-dns.com/dns-query"&&DotDns.Host(12)=="dns.adguard-dns.com","AdGuard addresses stay separate from Webshift and use the matching TLS hostname");
        Check(Dns.EncryptedOnly(13)&&DotDns.Available(13)&&EncryptedDns.Templates[13].Contains("5u35p8m9i7.cloudflare-gateway.com"),"Published Malw Cloudflare alternative supports encryption without exposing plaintext mode");
        Check(Dns.V6[7].Length==1&&Dns.V6[9].Length==2&&Dns.V6[12].Length==2&&Dns.V4[8].Length==4,"Published IPv6 and all four GeoHide US addresses are retained");
        var switches=(bool[])typeof(ProviderAvailability).GetField("Enabled",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
        var savedSwitches=(bool[])switches.Clone();
        try {
            switches[1]=false;switches[2]=false;
            Check(!Dns.AvailableProviders.Contains(1)&&!Dns.AvailableProviders.Contains(2)&&!Dns.AvailableProviders.Contains(3),"Disabled GeoHide and Xbox providers disappear from choices");
            Check(Enumerable.Range(0,AutoDns.Goals.Length).All(g=>AutoDns.Candidates(g,false).All(Dns.IsAvailable)),"The same switch excludes disabled providers from auto selection");
            Check(Dns.MatchProvider(Dns.V4[1])==1&&Dns.MatchProvider(Dns.V4[2])==2&&Dns.MatchProvider(Dns.V4[3])==3&&Dns.MatchProvider(new[]{"111.88.96.50","111.88.96.51"})==3,"Disabled providers and historical Xbox addresses still recognized for recovery");
            Check(new[]{1,2,3}.All(p=>Dns.ExecuteDirect(true,p).Contains("временно недоступен")),"Stale normal commands cannot enable disabled provider");
            Check(new[]{1,2,3}.All(p=>EncryptedDns.Run(p).Contains("временно недоступен")&&EncryptedDns.Run(p,false,true).Contains("временно недоступен")),"Same switch blocks HTTPS and TLS activation of disabled providers");
            Check(new[]{0,2}.All(p=>AntigravityDns.Run(true,false,p).Contains("временно недоступен")),"Same switch blocks Antigravity rules");
        } finally {Array.Copy(savedSwitches,switches,switches.Length);}
        var fake=new Probe();var results=AutoDns.Search(0,false,fake,CancellationToken.None);
        Check(results.Length==9&&AutoDns.Best(results).Provider==4,"Only an enabled candidate with both DNS and HTTPS succeeds across the expanded catalog");
        Check(fake.badAddressAttempts==2,"Both website and API retry a failed IP before the alternative resolved address");
        string diagnostic=AutoDns.Diagnostic(0,results);
        Check(diagnostic.Contains("Auto goal=Gemini")&&diagnostic.Contains("109.120.131.201")&&diagnostic.Contains("simulated relay timeout")&&diagnostic.Contains("Classification=Login")&&diagnostic.Contains("Total=")&&diagnostic.Contains("IOException"),"Developer log keeps failed IP attempts, error type, timings and API classification");
        Check(results.Single(r=>r.Provider==5).Encrypted&&results.Single(r=>r.Provider==13).Encrypted&&fake.encryptedCalls==4,"DNS-AI and Malw CF always use encryption for both endpoints even in mixed search");
        Check(AutoDns.Best(AutoDns.Search(0,false,new Probe{FailAll=true},CancellationToken.None))==null,"All failed candidates produce no selection");
        var currentOnly=new Probe();
        var health=AutoDns.Search(0,false,currentOnly,CancellationToken.None,null,4);
        Check(health.Length==1&&health[0].Provider==4&&currentOnly.calls==2,"Background health checks website and API only through current provider");
        Check(AutoDns.Best(AutoDns.Search(AutoDns.GoalIndex("Antigravity"),true,new Probe{FailDaily=true},CancellationToken.None))==null,"Antigravity requires both endpoint domains");
        foreach(bool encrypted in new[]{false,true}) {
            var magicProbe=new Probe{SuccessProvider=6};
            var magic=AutoDns.Search(AutoDns.GoalIndex("Antigravity"),encrypted,magicProbe,CancellationToken.None,null,6).Single();
            Check(magic.Ok&&magic.Provider==6&&magic.Encrypted==encrypted&&magicProbe.calls==2&&magicProbe.encryptedCalls==(encrypted?2:0),"MagicDNS checks both Antigravity hosts with the requested transport: DoH="+encrypted);
            Check(!AutoDns.Search(AutoDns.GoalIndex("Antigravity"),encrypted,new Probe{SuccessProvider=6,FailDaily=true},CancellationToken.None,null,6).Single().Ok,"MagicDNS cannot pass with a failed second Antigravity host: DoH="+encrypted);
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
        foreach(int status in new[]{403,404,429,503}) {
            var failed=AutoDns.Search(0,false,new Probe{ApiStatus=status,ApiBody="{}"},CancellationToken.None);
            Check(AutoDns.Best(failed)==null&&failed.Single(r=>r.Provider==4).Availability.Endpoints[0].Reply==ServiceReply.Response,"A working website cannot hide a failed/unconfirmed API HTTP "+status);
        }
        Check(AutoDns.Best(AutoDns.Search(0,false,new Probe{ApiStatus=200,ApiHtml=true,ApiBody="<html>landing</html>"},CancellationToken.None))==null,"API HTML 200 cannot win auto selection");
        Check(AutoDns.Best(AutoDns.Search(0,false,new Probe{ApiStatus=403,ApiBody="{\"error\":{\"code\":\"unsupported_country_region_territory\"}}"},CancellationToken.None))==null,"Explicit region block rejects candidate with working website");
        var verified=AutoDns.Search(0,false,new Probe{ApiStatus=200,ApiBody="{\"models\":[]}"},CancellationToken.None);
        Check(AutoDns.Best(verified)!=null&&AutoDns.Report(0,verified).Contains("API:"),"Expected API response is eligible with separate website and API results");
        var concurrent=new Probe{AnyProvider=true};
        AutoDns.Search(0,false,concurrent,CancellationToken.None);
        Check(concurrent.maxRequests<=4&&concurrent.calls==18,"Expanded catalog checks remain bounded to four concurrent HTTP requests");
        Check(!diagnostic.Contains("Missing authentication"),"Auto diagnostics do not copy raw API response bodies");
        Console.WriteLine("All "+passed+" auto-selection checks passed; fake probes only, no network or DNS changes.");
    }
}
