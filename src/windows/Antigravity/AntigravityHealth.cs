using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    enum AgHealthKind { Other, Transport, Region, Capacity, Quota, Authentication, Server, Answer }
    sealed class AgHealthEvent
    {
        internal AgHealthKind Kind;internal DateTime When;internal string Host,Detail,Key,Source,Request;internal bool LocalProxy;
        internal bool CanSwitch {get{return Source=="language_server.log"&&!LocalProxy&&(Kind==AgHealthKind.Transport||Kind==AgHealthKind.Region)&&AntigravityDns.Domains.Contains(Host);}}
    }
    static class AgHealthParser
    {
        internal static AgHealthEvent Parse(string line,string source,DateTime now,ref string lastHost)
        {
            if(string.IsNullOrEmpty(line))return null;
            string text=line.Replace("ERROR: logging before google.Init: ",""),lower=text.ToLowerInvariant();
            var stamp=Regex.Match(text,@"[IWEF](\d{2})(\d{2}) (\d{2}:\d{2}:\d{2}(?:\.\d+)?)");DateTime when;
            if(stamp.Success){if(!DateTime.TryParseExact(now.Year+"-"+stamp.Groups[1].Value+"-"+stamp.Groups[2].Value+" "+stamp.Groups[3].Value,new[]{"yyyy-MM-dd HH:mm:ss","yyyy-MM-dd HH:mm:ss.FFFFFFF"},CultureInfo.InvariantCulture,DateTimeStyles.None,out when))return null;if(when>now.AddDays(2))when=when.AddYears(-1);}
            else {var iso=Regex.Match(text,@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?");if(!iso.Success||!DateTime.TryParse(iso.Value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out when))return null;when=when.ToLocalTime();}
            if(when>now.AddSeconds(5))return null;
            var urlHost=Regex.Match(lower,@"\b(?:https?|wss?)://([^/\s:]+)");if(urlHost.Success)lastHost=AntigravityDns.Domains.Contains(urlHost.Groups[1].Value)?urlHost.Groups[1].Value:null;
            bool answer=AntigravityDns.Domains.Contains(lastHost)&&lower.Contains("streamgeneratecontent")&&lower.Contains("responseid:");
            bool error=Regex.IsMatch(text,@"(?:^|\s)[EWF]\d{4} \d{2}:")||lower.Contains("[error]")||lower.Contains("[warn]")||lower.Contains("attempt ")&&lower.Contains("failed (")||lower.Contains("model unavailable");
            if(!answer&&!error)return null;
            bool permission=lower.Contains("ignoring invalid allow entry")||lower.Contains("invalid grant string");
            bool bootstrap=AntigravityDns.Domains.Contains(lastHost)&&urlHost.Success&&lower.Contains("/v1internal:")&&!lower.Contains("streamgeneratecontent");
            bool network=new[]{"eof","timeout","timed out","wsarecv","connection reset","connection refused","connectex","network is unreachable","forcibly closed","use of closed network connection","no such host","tls handshake"}.Any(lower.Contains);
            var kind=AgHealthKind.Other;
            if(permission)kind=AgHealthKind.Other;
            else if(answer)kind=AgHealthKind.Answer;
            else if(lower.Contains("code 503")||lower.Contains("no capacity available")||lower.Contains("experiencing high traffic"))kind=AgHealthKind.Capacity;
            else if(lower.Contains("code 429")||lower.Contains("resource_exhausted"))kind=AgHealthKind.Quota;
            else if(lower.Contains("code 401")||lower.Contains("unauthenticated"))kind=AgHealthKind.Authentication;
            else if(lower.Contains("code 500"))kind=AgHealthKind.Server;
            else if(lower.Contains("user location is not supported"))kind=AgHealthKind.Region;
            else if(network&&(bootstrap||lower.Contains("attempt ")&&lower.Contains("failed (")&&(lower.Contains("streamgeneratecontent")||lower.Contains("stream reading error"))))kind=AgHealthKind.Transport;
            var attempt=Regex.Match(lower,@"attempt (\d+)").Groups[1].Value;
            var socket=Regex.Match(lower,@"\b(?:read|write|dial) (?:tcp|udp) (\S+->\S+)");
            return new AgHealthEvent{Kind=kind,When=when,Host=lastHost,Source=source,Request=bootstrap?lastHost+":"+(socket.Success?socket.Groups[1].Value.TrimEnd(':'):Regex.Match(lower,@"/v1internal:([a-z]+)").Groups[1].Value):null,LocalProxy=lower.Contains("->127.")||lower.Contains("->[::1]:"),Key=source+":"+when.Ticks+":"+kind+":"+attempt+":"+lastHost+":"+socket.Value,Detail=SafeDetail(text)};
        }
        internal static string SafeDetail(string text)
        {
            if(text.IndexOf("ignoring invalid allow entry",StringComparison.OrdinalIgnoreCase)>=0||text.IndexOf("invalid grant string",StringComparison.OrdinalIgnoreCase)>=0)return "Некорректная запись разрешения Antigravity; текст команды скрыт.";
            text=Regex.Replace(text,@"(?i)((?:authorization|proxy-authorization|cookie|set-cookie)[""']?\s*[:=]).*$","$1 <hidden>");
            text=DiagnosticReport.Scrub(text);
            text=Regex.Replace(text,@"(?i)[a-z]:\x5c{1,2}users\x5c{1,2}[^\x5c/\s""]+","%USERPROFILE%");
            text=Regex.Replace(text,@"(?i)\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b","<id>");
            text=Regex.Replace(text,@"(?i)\b(?:https?|wss?)://[^\s""<>]+",m=>{Uri u;if(!Uri.TryCreate(m.Value.TrimEnd(')',','),UriKind.Absolute,out u))return "<url>";string path=AntigravityDns.Domains.Contains(u.Host)&&Regex.IsMatch(u.AbsolutePath,@"^/v1internal:[A-Za-z]+$")?u.AbsolutePath:"/<path>";return u.Scheme+"://"+u.Host+path;});
            text=Regex.Replace(text,@"(?i)((?:Trace|ResponseID|csrf[_-]?token|session[_-]?id|conversation[_-]?id|project[_-]?id)\s*[:=]\s*)[^\s,;""']+","$1<hidden>");
            var payload=Regex.Match(text,@"(?i)[""']?(?:prompt|messages|input|request_body|response_body|content)[""']?\s*[:=]");if(payload.Success)text=text.Substring(0,payload.Index)+"<payload omitted>";
            return text.Length>1600?text.Substring(0,1600)+" [shortened]":text;
        }
    }
    sealed class AgLogTail
    {
        readonly string path;long position;bool initialized;byte[] remainder=new byte[0];string lastHost;
        internal AgLogTail(string path){this.path=path;try{if(File.Exists(path)){position=new FileInfo(path).Length;initialized=true;}}catch(IOException){}catch(UnauthorizedAccessException){}}
        internal AgHealthEvent[] Read(DateTime now)
        {
            if(!File.Exists(path))return new AgHealthEvent[0];
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                long length=stream.Length;if(!initialized){initialized=true;position=0;}
                if(length<position){position=0;remainder=new byte[0];lastHost=null;}
                if(length==position)return new AgHealthEvent[0];
                if(length-position>131072){position=length-131072;remainder=new byte[0];DiagnosticReport.Record("Журнал Antigravity","Пропущена часть слишком большого прироста журнала; читаются последние 128 КиБ.");}
                stream.Position=position;var bytes=new byte[(int)Math.Min(131072,length-position)];int count=stream.Read(bytes,0,bytes.Length);position+=count;
                var content=remainder.Concat(bytes.Take(count)).ToArray();int end=Array.LastIndexOf(content,(byte)10);if(end<0){remainder=content.Length<=16384?content:new byte[0];return new AgHealthEvent[0];}
                remainder=content.Length-end-1<=16384?content.Skip(end+1).ToArray():new byte[0];
                var result=new List<AgHealthEvent>();foreach(string line in Encoding.UTF8.GetString(content,0,end).Split('\n')){var item=AgHealthParser.Parse(line,Path.GetFileName(path),now,ref lastHost);if(item!=null)result.Add(item);}return result.ToArray();
            }
        }
    }
    sealed class AgLogMonitor : IDisposable
    {
        readonly AgLogTail[] tails;readonly object gate=new object();AgHealthEvent latestStop;int busy;volatile bool disposed;
        internal AgLogMonitor(string directory=null){string dir=directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Antigravity\logs");tails=new[]{new AgLogTail(Path.Combine(dir,"language_server.log")),new AgLogTail(Path.Combine(dir,"main.log"))};}
        internal void Poll(Action<AgHealthEvent[]> receive)
        {
            if(disposed||Interlocked.CompareExchange(ref busy,1,0)!=0)return;
            ThreadPool.QueueUserWorkItem(_=>{try{var events=Collect();if(!disposed)receive(events);
            }catch(Exception error){if(!disposed)DiagnosticReport.Record("Журнал Antigravity",error.GetType().Name);}finally{Interlocked.Exchange(ref busy,0);}});
        }
        internal AgHealthEvent[] Collect()
        {
            lock(gate){
                if(disposed)return new AgHealthEvent[0];var events=new List<AgHealthEvent>();foreach(var tail in tails)try{events.AddRange(tail.Read(DateTime.Now));}catch(IOException){}catch(UnauthorizedAccessException){}
                foreach(var item in events){
                    if(item.Kind==AgHealthKind.Answer||item.Kind==AgHealthKind.Capacity||item.Kind==AgHealthKind.Quota||item.Kind==AgHealthKind.Authentication||item.Kind==AgHealthKind.Server)if(latestStop==null||item.When>=latestStop.When)latestStop=item;
                    DiagnosticReport.Record("Antigravity · "+item.Kind,item.Kind==AgHealthKind.Answer?"Model stream started; host="+item.Host:item.Source+": "+item.Detail);
                }return events.ToArray();
            }
        }
        internal AgHealthEvent StopEventAfter(DateTime since){lock(gate)return latestStop!=null&&latestStop.When>=since?latestStop:null;}
        public void Dispose(){disposed=true;}
    }
    sealed class AgFailoverPolicy
    {
        readonly Dictionary<int,DateTime> excluded=new Dictionary<int,DateTime>();readonly Queue<DateTime> searches=new Queue<DateTime>();readonly Queue<string> seen=new Queue<string>();readonly Dictionary<string,DateTime> requests=new Dictionary<string,DateTime>();
        DateTime first,last,next,lastRegion;int failures;int workingRoute=-1;
        internal static int Route(int provider,bool tls){return provider*2+(tls?1:0);}
        internal void ResetSignal(){failures=0;}
        internal bool Observe(AgHealthEvent item,int provider,DateTime now,bool tls=false)
        {
            if(item.Kind==AgHealthKind.Answer||item.Kind==AgHealthKind.Capacity||item.Kind==AgHealthKind.Quota||item.Kind==AgHealthKind.Authentication||item.Kind==AgHealthKind.Server){ResetSignal();return false;}
            if(!item.CanSwitch||item.When<now.AddMinutes(-2)||seen.Contains(item.Key))return false;seen.Enqueue(item.Key);while(seen.Count>64)seen.Dequeue();
            if(item.Request!=null){DateTime previous;if(requests.TryGetValue(item.Request,out previous)&&item.When-previous<TimeSpan.FromSeconds(5))return false;if(requests.Count>=64)requests.Clear();requests[item.Request]=item.When;}
            if(item.Kind==AgHealthKind.Region){if(item.When-lastRegion<TimeSpan.FromSeconds(5))return false;lastRegion=item.When;}
            if(failures==0||item.When-first>TimeSpan.FromSeconds(90)||item.When<last){first=item.When;failures=0;}last=item.When;failures++;
            if(failures<2||now<next)return false;
            while(searches.Count>0&&now-searches.Peek()>TimeSpan.FromMinutes(10))searches.Dequeue();
            if(searches.Count>=3)return false;
            searches.Enqueue(now);next=now.AddMinutes(1);excluded[Route(provider,tls)]=now.AddMinutes(1);ResetSignal();return true;
        }
        internal int[] Candidates(IEnumerable<int> visible,int current,DateTime now)
        {return visible.Where(id=>id!=current&&LocalDnsRouting.Supports(id)&&(CanProbe(id,false,now)||DotDns.Available(id)&&CanProbe(id,true,now))).Distinct().ToArray();}
        internal bool CanProbe(int provider,bool tls,DateTime now){DateTime until;return !excluded.TryGetValue(Route(provider,tls),out until)||now>=until;}
        internal void Failed(int provider,DateTime now,bool tls=false){excluded[Route(provider,tls)]=now.AddSeconds(30);}
        internal void Answer(int provider,bool tls=false){workingRoute=Route(provider,tls);excluded.Remove(workingRoute);ResetSignal();}
        internal AutoDnsResult Best(IEnumerable<AutoDnsResult> results)
        {return results.Where(r=>r.Ok&&!r.NeedsBrowser).OrderBy(r=>Route(r.Provider,r.Tls)==workingRoute?0:1).ThenBy(r=>r.Milliseconds).ThenBy(r=>r.Provider).FirstOrDefault();}
    }
    static class AgFailoverPreference
    {
        internal const string Value="AntigravityAutoDns";
        internal static bool Read(RegistryKey key){return key!=null&&object.Equals(key.GetValue(Value),1);}
        internal static bool Read(){try{using(var key=Registry.CurrentUser.OpenSubKey(CustomDns.RegistryPath))return Read(key);}catch{return false;}}
    }
}
