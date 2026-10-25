using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HbAi.XboxDns;

static class ServiceAvailabilityTests
{
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static ServiceEndpoint Api {get{return ServiceAvailability.Targets[0].Endpoints[1];}}
    static ServiceEndpoint Web {get{return ServiceAvailability.Targets[0].Endpoints[0];}}
    static ServiceHttpReply Reply(int status,string body="",string type="application/json")
    {var response=new ServiceHttpReply{Status=status,Body=body};response.Headers["Content-Type"]=type;return response;}
    sealed class Probe : IServiceAvailabilityProbe
    {
        internal int calls;
        internal bool DnsFails, TlsFails, Challenge, HttpFails, ConnectFails;
        internal string[] Addresses = new[]{"109.120.132.43","94.249.177.102"};
        internal bool Encrypted;
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel)
        {Encrypted=encrypted;cancel.ThrowIfCancellationRequested();if(DnsFails)throw new IOException("DNS timeout");return Addresses;}
        public ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel)
        {
            Interlocked.Increment(ref calls);cancel.ThrowIfCancellationRequested();
            if(TlsFails)throw new ServiceTransportException("TLS",new IOException("certificate mismatch"));
            if(HttpFails)throw new ServiceTransportException("HTTP",new IOException("bad reply"));
            if(ConnectFails)throw new ServiceTransportException("соединение",new IOException("timeout"));
            var response=Reply(endpoint.Api?401:200,"{\"error\":{\"message\":\"missing authentication\"}}");
            if(Challenge){response.Status=403;response.Headers["cf-mitigated"]="challenge";}
            return response;
        }
    }
    static ServiceHttpReply Read(string wire,bool api=true)
    {using(var input=new MemoryStream(Encoding.UTF8.GetBytes(wire)))return ServiceAvailabilityProbe.ReadResponse(input,api,CancellationToken.None);}
    static void Invalid(string wire,string reason)
    {bool failed=false;try{Read(wire);}catch(IOException){failed=true;}Check(failed,reason);}
    static void Main(string[] args)
    {
        if(args.Length>0&&(args[0]=="--live"||args[0]=="--live-all")){
            for(int target=0;target<(args[0]=="--live-all"?ServiceAvailability.Targets.Length:1);target++) {
                var live=ServiceAvailability.Check(target,6,true,new ServiceAvailabilityProbe(true),CancellationToken.None);
                Console.WriteLine(live.Name+"\n"+live.Summary+"\n\n"+live.Report+"\n\n"+live.Diagnostic);
            }
            return;
        }
        Check(ServiceAvailability.Targets.Length==4&&ServiceAvailability.Targets.All(t=>t.Endpoints.Length==2),"Each AI service checks separate website and read-only API endpoints");
        Check(ServiceAvailability.Classify(Api,Reply(200,"<html>landing page</html>","text/html"))==ServiceReply.Unknown,"An API HTML 200 never proves service access");
        Check(ServiceAvailability.Classify(Api,Reply(200,"{}"))==ServiceReply.Unknown,"An arbitrary API JSON 200 never proves service access");
        Check(ServiceAvailability.Classify(Api,Reply(200,"{\"error\":{\"message\":\"unavailable\"}}"))==ServiceReply.Unknown,"An error hidden inside a 200 is unconfirmed");
        Check(ServiceAvailability.Classify(Api,Reply(200,"{\"models\":[]}"))==ServiceReply.Response,"Expected API structure means server response, never generation");
        Check(ServiceAvailability.Classify(Api,Reply(403,"{\"error\":{\"message\":\"Method doesn't allow unregistered callers. Please use API Key\"}}"))==ServiceReply.Login,"Google key-required response is authentication, not DNS failure");
        Check(ServiceAvailability.Classify(Api,Reply(400,"{\"error\":{\"message\":\"API key not valid\"}}"))==ServiceReply.Login,"Invalid or missing key does not mean an inaccessible server");
        Check(ServiceAvailability.Classify(Api,Reply(401))==ServiceReply.Login,"401 is a reachable server requiring authentication");
        Check(ServiceAvailability.Classify(Api,Reply(403,"{\"error\":{\"code\":\"unsupported_country_region_territory\"}}"))==ServiceReply.Restricted,"Explicit regional error is distinguished from key requirements");
        Check(ServiceAvailability.Classify(Api,Reply(400,"{\"error\":{\"message\":\"User location is not supported for the API use.\"}}"))==ServiceReply.Restricted,"Google location restriction detected in API error fields");
        Check(ServiceAvailability.Classify(Web,Reply(200,"not available in your country","text/html"))==ServiceReply.Response,"Marketing page text cannot invent a regional block");
        var challenge=Reply(403);challenge.Headers["cf-mitigated"]="challenge";
        Check(ServiceAvailability.Classify(Web,challenge)==ServiceReply.BrowserCheck,"Cloudflare challenge requires browser and is not a DNS failure");
        Check(ServiceAvailability.Classify(Web,Reply(403))==ServiceReply.Unknown,"Unmarked forbidden does not invent a captcha or regional reason");
        Check(ServiceAvailability.Classify(Api,Reply(404))==ServiceReply.Unknown,"Missing API endpoint is unconfirmed, never accessible");
        Check(ServiceAvailability.Classify(Api,Reply(429))==ServiceReply.RateLimited,"Rate limit is independent from DNS failure");
        Check(ServiceAvailability.Classify(Api,Reply(503))==ServiceReply.ServerError,"Server failure is independent from DNS failure");
        var redirect=Reply(302);redirect.Headers["Location"]="https://accounts.google.com/signin";
        Check(ServiceAvailability.Classify(Web,redirect)==ServiceReply.Login,"Login redirect is not silently accepted as usable AI");
        redirect.Headers["Location"]="https://example.org/";
        Check(ServiceAvailability.Classify(Web,redirect)==ServiceReply.Unknown,"Other redirects stay unconfirmed and are never followed");
        var probe=new Probe();var result=ServiceAvailability.Check(0,6,true,probe,CancellationToken.None);
        Check(result.Endpoints.All(e=>e.Dns&&e.Connection&&e.Tls)&&probe.calls==2&&probe.Encrypted,"Website and API requests use selected encrypted DNS");
        Check(!result.Attention&&result.Endpoints[1].Reply==ServiceReply.Login,"Authentication requirement is not a network failure");
        Check(result.Summary.Contains("Генерация не проверялась")&&result.Report.Contains("API и сайт могут иметь разные ограничения"),"Visible report explains verification limits");
        Check(!result.Diagnostic.Contains("missing authentication"),"API response bodies are not copied into diagnostic logs");
        var dns=new Probe{DnsFails=true};result=ServiceAvailability.Check(0,6,false,dns,CancellationToken.None);
        Check(dns.calls==0&&result.Endpoints.All(e=>!e.Dns&&!e.Tls&&e.Error.StartsWith("DNS:")),"DNS failure stops before any HTTPS request");
        result=ServiceAvailability.Check(0,6,false,new Probe{TlsFails=true},CancellationToken.None);
        Check(result.Endpoints.All(e=>e.Dns&&e.Connection&&!e.Tls&&e.Error.StartsWith("TLS:")),"Certificate failure is shown at TLS stage");
        result=ServiceAvailability.Check(0,6,false,new Probe{HttpFails=true},CancellationToken.None);
        Check(result.Endpoints.All(e=>e.Dns&&e.Connection&&e.Tls&&e.Error.StartsWith("HTTP:")),"Bad HTTP reply preserves completed DNS and TLS stages");
        result=ServiceAvailability.Check(0,6,false,new Probe{ConnectFails=true},CancellationToken.None);
        Check(result.Endpoints.All(e=>e.Dns&&!e.Connection&&!e.Tls),"Connection timeout is not mislabeled as DNS failure");
        var challenged=new Probe{Challenge=true};result=ServiceAvailability.Check(0,6,false,challenged,CancellationToken.None);
        Check(challenged.calls==2&&result.Endpoints.All(e=>e.Reply==ServiceReply.BrowserCheck)&&result.Attention&&!result.OfferReport,"Browser checks do not retry HTTP responses or clutter the summary with a technical report panel");
        result=ServiceAvailability.Check(0,6,false,new Probe{Addresses=new[]{"127.0.0.1","192.168.0.1"}},CancellationToken.None);
        Check(result.Endpoints.All(e=>!e.Dns),"Local or router addresses cannot be mistaken for service addresses");
        using(var cancel=new CancellationTokenSource()) {
            cancel.Cancel();bool cancelled=false;try{ServiceAvailability.Check(0,6,true,new Probe(),cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled,"Closing the dialog can cancel testing without presenting stale success");
        }
        var response=Read("HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}");
        Check(response.Status==401&&response.Body=="{}","HTTP error bodies are read for classification without an API key");
        response=Read("HTTP/1.1 403 Forbidden\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}\r\n0\r\n\r\n");
        Check(response.Body=="{}","Chunked JSON errors decoded correctly");
        Check(Read("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 999999\r\n\r\n",false).Body==null,"Website probe does not download a whole landing page");
        Check(Read("HTTP/1.1 200 OK\r\nContent-Security-Policy: "+new string('a',22000)+"\r\n\r\n",false).Status==200,"Large real-world CSP headers fit within the bounded header allowance");
        Invalid("HTTP/1.1 200 OK\r\nX-Test: "+new string('a',65536)+"\r\n\r\n","Header allowance remains bounded");
        response=Read("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 40000\r\n\r\n"+new string('x',40000));
        Check(response.Body.Length==32768&&ServiceAvailability.Classify(Api,response)==ServiceReply.Unknown,"Large or truncated API response is bounded and unconfirmed");
        Invalid("HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n","Duplicate framing headers rejected");
        Invalid("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n","Ambiguous response framing rejected");
        Invalid("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n","Malformed chunk size rejected");
        Invalid("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 3\r\n\r\n{}","Truncated body rejected");
        const string upgraded="HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n";
        Invalid(upgraded,"Ordinary HTTP probes do not accidentally accept a protocol upgrade");
        using(var wire=new MemoryStream(Encoding.ASCII.GetBytes(upgraded+"unread websocket frame"))) {
            response=ServiceAvailabilityProbe.ReadResponse(wire,true,CancellationToken.None,true,true);
            ServiceAvailabilityProbe.ValidateUpgrade(response,"dGhlIHNhbXBsZSBub25jZQ==");
            Check(response.Status==101&&response.Body==null&&wire.Position==Encoding.ASCII.GetByteCount(upgraded),"Explicit WebSocket diagnostic validates the RFC handshake without reading frames or sending model data");
            response.Headers["Sec-WebSocket-Accept"]="wrong";bool rejected=false;try{ServiceAvailabilityProbe.ValidateUpgrade(response,"dGhlIHNhbXBsZSBub25jZQ==");}catch(IOException){rejected=true;}
            Check(rejected,"A forged WebSocket accept value is rejected");
        }
        // Use the actual bounded response length, independent of content encoding.
        string page="<title>Access denied</title>";string htmlWire="HTTP/1.1 403 Forbidden\r\nContent-Type: text/html\r\nContent-Length: "+page.Length+"\r\n\r\n"+page;
        using(var wire=new MemoryStream(Encoding.ASCII.GetBytes(htmlWire)))Check(ServiceAvailabilityProbe.ReadResponse(wire,true,CancellationToken.None).Body==null,"Existing public API probes still avoid downloading HTML");
        using(var wire=new MemoryStream(Encoding.ASCII.GetBytes(htmlWire)))Check(ServiceAvailabilityProbe.ReadResponse(wire,true,CancellationToken.None,false,true).Body==page,"Explicit diagnostics can inspect bounded anonymous HTML errors");
        Console.WriteLine("All "+passed+" availability checks passed; no credentials or network settings used.");
    }
}
