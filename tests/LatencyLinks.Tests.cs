using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HbAi.XboxDns;

static class LatencyLinksTests
{
    static int passed;static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);passed++;Console.WriteLine("PASS "+text);}
    static void Set(MainForm form,string field,object value){typeof(MainForm).GetField(field,Fields).SetValue(form,value);}
    sealed class Probe : IAutoDnsProbe
    {
        internal int Calls;internal bool Encrypted;
        public string[] Resolve(int provider,bool encrypted,string host,CancellationToken cancel){cancel.ThrowIfCancellationRequested();Calls++;Encrypted=encrypted;return new[]{"93.184.215.14"};}
        public void Https(string host,string address,bool api,CancellationToken cancel){throw new Exception("Latency must not make an authenticated/service request");}
        public ServiceHttpReply Request(ServiceEndpoint endpoint,string address,CancellationToken cancel){throw new Exception("Latency must not make an API request");}
    }
    static ProviderLatency Result(int ms=37,string error=null){return new ProviderLatency{Provider=0,Protocol=DnsTransport.Https,Milliseconds=ms,Error=error,MeasuredUtc=DateTime.UtcNow,Path="fake measured path"};}
    [STAThread]static void Main()
    {
        Check(Dns.Url[0]=="https://www.comss.ru/page.php?id=7315","Comss opens its DNS information page rather than the software portal");
        Check(Dns.Url[4]=="https://bezmezhau.com/ru/setup"&&Dns.Url[12].Contains("/ru/public-dns.html"),"Russian setup pages are used when published");
        Check(Dns.Url[11]=="https://dns.webshiftvpn.ru/"&&Dns.Site[11]=="dns.webshiftvpn.ru","Webshift information link is distinct from its DoH endpoint");
        Check(Dns.Url[9]=="https://info.dns.malw.link/setup"&&Dns.Url[13]=="https://info.dns.malw.link/setup/cloudflare","Malw and Malw CF open their matching profile instructions");
        foreach(int id in Dns.AvailableProviders){var uri=new Uri(Dns.Url[id]);Check(uri.Scheme=="https"&&uri.AbsolutePath!="/dns-query"&&Dns.Url[id]!=EncryptedDns.Templates[id],"Information link is an HTTPS page rather than the resolver endpoint: "+Dns.Name[id]);Check(uri.Host.Replace("www.","")==Dns.Site[id],"Header domain matches the information link: "+Dns.Name[id]);}
        var probe=new Probe();var direct=ProviderLatency.MeasureActive(0,DnsTransport.Https,false,false,CancellationToken.None,probe);
        Check(probe.Calls==1&&probe.Encrypted&&direct.Error==null&&direct.Path.Contains("Прямой"),"Windows mode measures a DNS transaction through the selected encrypted transport");
        string actual=null;int requests=0;
        Func<string,string,DnsProbeResult> loop=(address,host)=>{requests++;actual=address;Check(host=="example.com","Application ping uses a DNS test name only");return new DnsProbeResult{Milliseconds=41};};
        var local=ProviderLatency.MeasureActive(0,DnsTransport.Https,true,false,CancellationToken.None,probe,loop);
        Check(actual=="127.0.0.1"&&requests==1&&probe.Calls==1&&local.Milliseconds==41&&local.Path.Contains("127.0.0.1"),"Application mode includes the running loopback proxy instead of bypassing it");
        var google=ProviderLatency.MeasureActive(0,DnsTransport.Tls,false,true,CancellationToken.None,probe,loop);
        Check(actual==DnsTunnelNative.V4&&google.Protocol==DnsTransport.Tls,"Google profile measures through its DNS adapter address");
        var failed=ProviderLatency.MeasureActive(0,DnsTransport.Https,true,false,CancellationToken.None,probe,(a,h)=>new DnsProbeResult{Error="fixture timeout",Milliseconds=2000});
        Check(failed.Error!=null,"A timed out DNS probe never becomes a successful zero latency");
        using(var stop=new CancellationTokenSource()){stop.Cancel();bool cancelled=false;try{ProviderLatency.MeasureActive(0,DnsTransport.Https,true,false,stop.Token,probe,loop);}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&requests==2,"Cancellation prevents new loopback traffic");}
        using(var form=new MainForm(new TunnelSession(),false)){
            foreach(string field in new[]{"initializing","agLoading","busy"})Set(form,field,false);Set(form,"active",false);Set(form,"defaultGoal",-1);
            Check(form.ConnectionLatencyLabel==null,"Disabled DNS has no invented ping");
            Set(form,"active",true);Set(form,"dnsSet",0);Set(form,"secureDns",true);Set(form,"tlsDns",false);Set(form,"hasBackup",true);Set(form,"dnsConfirmed",true);Set(form,"networkAvailable",true);
            Set(form,"latencyProvider",0);Set(form,"latencyTransport",DnsTransport.Https);Set(form,"latencyRevision",7);Set(form,"latencyChecking",true);
            Check(form.ConnectionLatencyLabel=="… мс","A pending measurement is visibly different from a completed one");
            Check(!form.AcceptConnectionLatencyResult(Result(),6),"Old cancelled ping cannot overwrite a newer request");
            Check(form.AcceptConnectionLatencyResult(Result(),7)&&form.ConnectionLatencyLabel=="37 мс"&&form.ConnectionStatusCaption.Contains("37 мс"),"A matching DNS result appears beside the connection status");
            Check(form.ConnectionLatencyTip.Contains("fake measured path")&&form.ConnectionLatencyTip.Contains("не ICMP"),"Ping tooltip explains the measured path and DNS timing scope");
            Check(!form.CheckerConfirmed,"A good ping alone cannot confirm the official checker");
            form.AcceptConnectionLatencyResult(Result(0),7);Check(form.ConnectionLatencyLabel=="<1 мс","Submillisecond transaction is labeled accurately");
            form.AcceptConnectionLatencyResult(Result(0,"timeout"),7);Check(form.ConnectionLatencyLabel=="— мс","Unavailable measurement is not displayed as zero milliseconds");
            form.AcceptConnectionLatencyResult(Result(),7);Set(form,"dnsSet",7);Check(!form.LatencyResultCurrent&&form.ConnectionLatencyLabel!="37 мс","Changing provider immediately hides stale latency");Set(form,"dnsSet",0);
            Set(form,"tlsDns",true);Check(!form.LatencyResultCurrent&&!form.AcceptConnectionLatencyResult(Result(),7),"Changing protocol rejects old latency");Set(form,"tlsDns",false);
            Set(form,"localMode",true);Check(!form.LatencyResultCurrent,"Changing connection method hides stale latency");Set(form,"latencyLocal",true);
            Check(form.AcceptConnectionLatencyResult(Result(),7),"Local measurement uses its own matching mode snapshot");
            Set(form,"busy",true);Check(form.ConnectionLatencyLabel==null&&!form.AcceptConnectionLatencyResult(Result(),7),"Provider switching cannot accept or display an old ping");Set(form,"busy",false);
            foreach(bool dark in new[]{false,true}){
                typeof(MainForm).GetMethod("SetTheme",Fields).Invoke(form,new object[]{dark,false});Set(form,"themeMix",dark?1f:0f);Set(form,"powerMix",1f);
                foreach(bool app in new[]{false,true}){Set(form,"localMode",app);Set(form,"latencyLocal",app);form.AcceptConnectionLatencyResult(Result(),7);
                    using(var image=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)){
                        form.DrawToBitmap(image,form.ClientRectangle);image.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-ping-"+dark+"-"+app+".png"));
                        using(var graphics=Graphics.FromImage(image))using(var font=new Font((FontFamily)typeof(MainForm).GetField("fHead",Fields).GetValue(form),form.ConnectionStatusFontSize(graphics)*96f/72f,GraphicsUnit.Pixel))
                            Check(graphics.MeasureString(form.ConnectionStatusCaption,font).Width<=(int)typeof(MainForm).GetField("layoutWidth",Fields).GetValue(form)-60,"Status and ping fit without overlap, dark="+dark+" app="+app);
                    }
                    Check(form.LatencyStatusBounds.Bottom<form.CheckerStatusBounds.Top,"Ping status and checker remain separate rows");
                }
            }
            Set(form,"active",false);Check(form.ConnectionLatencyLabel==null&&!form.AcceptConnectionLatencyResult(Result(),7),"Disabling DNS discards late ping replies");
        }
        Console.WriteLine("All "+passed+" latency and information-link checks passed; fake DNS probes, no browser launches or network setting changes.");
    }
}
