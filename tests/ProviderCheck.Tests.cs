using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HbAi.XboxDns;

static class ProviderCheckTests
{
    static int passed;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    sealed class Fake : IProviderCheckIo
    {
        internal readonly List<string> Urls=new List<string>();
        internal string Host,Body="{}";internal int Status=200,Stamps,Lookups,Pauses;
        internal bool Changed,LookupFails,LookupTimeout,Fail,StampFails;
        internal Func<string,ProviderCheckReply> Response;
        public string Stamp(){if(StampFails)throw new IOException();return Changed&&Stamps++>0?"changed":"original";}
        public ProviderCheckReply Get(string url,CancellationToken cancel){cancel.ThrowIfCancellationRequested();Check(ProviderCheckIo.Allowed(url),"Generated checker URL is allowlisted");Urls.Add(url);if(Fail)throw new IOException("private-body-and-id");return Response==null?new ProviderCheckReply{Status=Status,Body=Body}:Response(url);}
        public void Lookup(string host,CancellationToken cancel){cancel.ThrowIfCancellationRequested();Lookups++;Host=host;if(LookupTimeout)throw new TimeoutException();if(LookupFails)throw new IOException("lookup failed");}
        public void Pause(CancellationToken cancel){cancel.ThrowIfCancellationRequested();Pauses++;}
    }
    static ProviderCheckResult Run(int provider,Fake fake){return ProviderCheck.Run(provider,fake,CancellationToken.None);}
    static void Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--live") {
            foreach(int provider in args.Skip(1).Select(int.Parse))using(var stop=new CancellationTokenSource(18000)) {
                var result=ProviderCheck.Run(provider,new ProviderCheckIo(),stop.Token);
                Console.WriteLine(Dns.Name[provider]+": "+result.State+"; source="+result.Source+"\n"+result.Detail);
            }
            return;
        }
        Check(new ProviderCheckResult().State==ProviderCheckState.Inconclusive,"Default state cannot imply success");
        Check(new[]{0,5,7,9,10,11,13}.All(ProviderCheck.Available)&&new[]{-1,1,2,3,4,6,8,12,999}.All(p=>!ProviderCheck.Available(p)),"Only reviewed checker contracts are enabled; hidden providers stay disabled");
        var comss=new Fake{Body=" \r\nComss.one DNS enabled\r\n"};var comssResult=Run(0,comss);
        Check(comssResult.State==ProviderCheckState.Confirmed&&comss.Urls.Count==1&&new Uri(comss.Urls[0]).Host=="check.comss.one"&&comssResult.Source.Contains("comss.ru"),"Comss accepts only the official exact marker via its special system-resolved hostname");
        foreach(string body in new[]{"",null,"direct","Comss.one DNS enabled extra","<html>Comss.one DNS enabled</html>","comss.one dns enabled",new string('x',33000)})
            Check(Run(0,new Fake{Body=body}).State==ProviderCheckState.Inconclusive,"Missing, changed or misleading Comss marker cannot confirm DNS");
        foreach(int code in new[]{204,302,403,404,500})
            Check(Run(0,new Fake{Status=code,Body="Comss.one DNS enabled"}).State==ProviderCheckState.Inconclusive,"Comss HTTP errors or redirects cannot confirm DNS "+code);
        Check(Run(0,new Fake{Fail=true}).State==ProviderCheckState.Inconclusive,"Unavailable Comss marker stays unknown instead of falling back to matching UDP nodes");
        Check(Run(0,new Fake{Changed=true,Body="Comss.one DNS enabled"}).State==ProviderCheckState.NetworkChanged,"Network change invalidates a Comss confirmation");
        var fake=new Fake{Body="{\"via\":\"ours\",\"ip\":\"private-value\"}"};var r=Run(10,fake);
        Check(r.State==ProviderCheckState.Confirmed&&r.Positive&&fake.Urls.Count==1,"WLrus requires its explicit ours marker");
        Check(!r.Detail.Contains("private-value")&&r.Report.Contains("системный DNS Windows")&&r.Report.Contains("DoH браузера"),"Report states system scope and omits returned client data");
        Check(Run(10,new Fake{Body="{\"via\":\"public\"}"}).State==ProviderCheckState.NotConfirmed,"WLrus public path is an explicit negative response");
        foreach(string body in new[]{"{}","{\"via\":true}","{\"via\":\"unexpected\"}","<html>captive portal</html>","",new string('x',33000)})
            Check(Run(10,new Fake{Body=body}).State==ProviderCheckState.Inconclusive,"Missing/invalid/oversized WLrus response cannot confirm or deny DNS");
        Check(Run(11,new Fake{Body="{\"connected\":true}"}).State==ProviderCheckState.Confirmed,"Webshift boolean true confirms its checker result");
        Check(Run(11,new Fake{Body="{\"connected\":false}"}).State==ProviderCheckState.NotConfirmed,"Webshift boolean false remains distinct from errors");
        foreach(string body in new[]{"{}","{\"connected\":\"true\"}","{\"connected\":1}","[]","true"})
            Check(Run(11,new Fake{Body=body}).State==ProviderCheckState.Inconclusive,"Webshift rejects coerced or absent connected marker");
        foreach(int code in new[]{204,301,302,403,404,429,500})Check(Run(11,new Fake{Status=code,Body="{\"connected\":true}"}).State==ProviderCheckState.Inconclusive,"HTTP "+code+" does not become a positive or negative verdict");
        fake=new Fake{Body="{\"seen\":true,\"protocol\":\"DNS-over-HTTPS\"}"};r=Run(7,fake);
        Check(r.State==ProviderCheckState.Confirmed&&r.Protocol=="DoH"&&fake.Lookups==1&&fake.Urls.Count==1,"Zapret observes a fresh Windows lookup and normalizes reported protocol");
        string nonce=fake.Host.Split('.')[0];
        Check(nonce.Length==16&&fake.Urls[0].EndsWith(nonce)&&!r.Detail.Contains(nonce),"The same random token links lookup and poll but never enters the journal");
        var next=new Fake{Body=fake.Body};Run(7,next);Check(next.Host!=fake.Host,"New checks do not reuse cached challenge names");
        fake=new Fake{Body="{\"seen\":false}",LookupFails=true};r=Run(7,fake);
        Check(r.State==ProviderCheckState.NotConfirmed&&fake.Urls.Count==4&&fake.Pauses==4,"Zapret polls a bounded number of times even after a negative lookup");
        Check(Run(7,new Fake{Body="{\"seen\":false}",LookupTimeout=true}).State==ProviderCheckState.Inconclusive,"A pending system lookup cannot yield a conclusive negative verdict");
        int polls=0;fake=new Fake{Response=url=>new ProviderCheckReply{Status=200,Body=++polls==2?"{\"seen\":true,\"protocol\":\"TLS\"}":"{\"seen\":false}"}};r=Run(7,fake);
        Check(r.State==ProviderCheckState.Confirmed&&polls==2&&r.Protocol=="DoT","Delayed observation can succeed without another system query");
        Check(Run(7,new Fake{Body="{\"seen\":\"true\"}"}).State==ProviderCheckState.Inconclusive,"Zapret rejects string truthiness");
        fake=new Fake{Status=204};r=Run(5,fake);
        Check(r.State==ProviderCheckState.Confirmed&&fake.Urls.Count==2&&new Uri(fake.Urls[0]).Host=="dns-ai.ru"&&new Uri(fake.Urls[1]).Host=="check.dns-ai.ru","DNS-AI requires control before the special-name probe");
        fake=new Fake{Status=404};r=Run(5,fake);
        Check(r.State==ProviderCheckState.Inconclusive&&fake.Urls.Count==1,"Absent DNS-AI control prevents a misleading negative verdict");
        fake=new Fake{Response=url=>{if(new Uri(url).Host=="check.dns-ai.ru")throw new TimeoutException();return new ProviderCheckReply{Status=204};}};
        Check(Run(5,fake).State==ProviderCheckState.Inconclusive,"Special-probe timeout is unknown rather than an asserted missing DNS setting");
        foreach(int provider in new[]{9,13}) {
            r=Run(provider,new Fake{Status=204});
            Check(r.State==ProviderCheckState.ProbePassed&&r.Report.Contains("не различает")&&!r.Summary.Contains("чекер подтвердил"),"Malw probe does not claim to distinguish ordinary/Cloudflare profile "+provider);
        }
        Check(Run(9,new Fake{Status=404}).State==ProviderCheckState.Inconclusive,"Malformed Malw endpoint does not pass merely because fetch resolved");
        r=Run(11,new Fake{Fail=true});Check(r.State==ProviderCheckState.Inconclusive&&!r.Detail.Contains("private-body-and-id"),"Network failures preserve a neutral result and exclude raw server details");
        r=Run(11,new Fake{Changed=true,Body="{\"connected\":true}"});Check(r.State==ProviderCheckState.NetworkChanged&&!r.Positive,"Network change invalidates a formerly positive response");
        r=Run(11,new Fake{StampFails=true});Check(r.State==ProviderCheckState.Inconclusive,"Unavailable network snapshot is unknown rather than a claimed network change");
        using(var cancel=new CancellationTokenSource()) {
            cancel.Cancel();fake=new Fake();bool stopped=false;
            try{ProviderCheck.Run(11,fake,cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Check(stopped&&fake.Urls.Count==0,"Cancellation prevents all new checker traffic");
        }
        foreach(string url in new[]{"http://dns.webshiftvpn.ru/api/dns-check","https://dns.webshiftvpn.ru.evil/api/dns-check","https://user:pass@dns.webshiftvpn.ru/api/dns-check","https://dns.webshiftvpn.ru:444/api/dns-check","https://dns.webshiftvpn.ru/other","https://127.0.0.1/api/dns-check","https://dns.webshiftvpn.ru/api/dns-check#fragment"})
            Check(!ProviderCheckIo.Allowed(url),"Reject foreign, redirected, credentialed or unexpected checker URL");
        bool refused=false;fake=new Fake();try{Run(3,fake);}catch(ArgumentOutOfRangeException){refused=true;}Check(refused&&fake.Urls.Count==0,"Disabled provider cannot start a checker");
        var combined=new ResolverCheckResult{Official=Run(11,new Fake{Body="{\"connected\":true}"})};
        Check(combined.Summary==combined.Official.Summary&&combined.Report.Contains("dns.webshiftvpn.ru")&&!combined.Report.Contains("MangoProxy"),"Resolver result presents the official checker without inventing recursive-node evidence");
        Console.WriteLine("All "+passed+" provider-checker checks passed; fake network replies only, no DNS or adapter changes.");
    }
}
