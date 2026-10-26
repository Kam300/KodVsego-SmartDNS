using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    sealed class StartupFrame
    {
        internal string Url,Unreachable;
        internal bool Failed {get{return Url=="chrome-error://chromewebdata/";}}
        internal Uri LocalPage(string fallback)
        {
            Uri uri;return Failed&&Uri.TryCreate(string.IsNullOrEmpty(Unreachable)?fallback:Unreachable,UriKind.Absolute,out uri)&&LocalHttps(uri)?uri:null;
        }
        internal static bool LocalHttps(Uri uri){return uri!=null&&uri.Scheme=="https"&&uri.Host=="127.0.0.1"&&uri.Port>0&&uri.UserInfo.Length==0;}
    }
    sealed class StartupRecoveryPolicy
    {
        sealed class Attempt {internal int Ready,Reloads;internal long Seen,Last=-60000;}
        readonly Dictionary<string,Attempt> attempts=new Dictionary<string,Attempt>();
        internal bool Visit(string key,long now,Func<StartupFrame> read,Func<Uri,bool> ready,Action reload,string fallback,Func<bool> cancelled)
        {
            if(cancelled())return false;var first=read();if(first==null)return false;
            Attempt state;if(!attempts.TryGetValue(key,out state)){
                if(attempts.Count>=32)attempts.Clear();attempts[key]=state=new Attempt{Seen=now};
            }
            var uri=first.LocalPage(fallback);
            if(uri==null){state.Ready=0;state.Seen=now;return false;}
            if(state.Reloads>=2||now-state.Seen<5000||now-state.Last<20000||cancelled())return false;
            if(!ready(uri)){state.Ready=0;return false;}
            if(++state.Ready<2||cancelled())return false;
            // A user or the application may already have recovered the page while
            // the HTTP check ran. Never reload a newly opened working conversation.
            var fresh=read();var freshUri=fresh==null?null:fresh.LocalPage(fallback);
            if(freshUri==null||freshUri!=uri||cancelled()){state.Ready=0;return false;}
            state.Last=now;state.Reloads++;state.Ready=0;reload();return true;
        }
    }
    sealed class AntigravityStartupRecovery : IDisposable
    {
        internal const string Preference="AntigravityStartupRecovery";
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();readonly StartupRecoveryPolicy policy=new StartupRecoveryPolicy();
        readonly Stopwatch clock=Stopwatch.StartNew();int busy;long previous=-10000;volatile bool enabled;
        string lastFailure;volatile string observation="Не проверялось";
        internal string Observation {get{return observation;}}
        internal static bool ReadEnabled(RegistryKey key){return key==null||!object.Equals(key.GetValue(Preference),0);}
        internal static bool ReadEnabled(){try{using(var key=Registry.CurrentUser.OpenSubKey(CustomDns.RegistryPath))return ReadEnabled(key);}catch{return true;}}
        internal void Tick(bool enable)
        {
            enabled=enable;if(!enable||lifetime.IsCancellationRequested||clock.ElapsedMilliseconds-previous<5000||Interlocked.CompareExchange(ref busy,1,0)!=0)return;
            previous=clock.ElapsedMilliseconds;
            ThreadPool.QueueUserWorkItem(_=>{try{Check();lastFailure=null;}catch(Exception error){string kind=error.GetType().Name;if(lastFailure!=kind){DiagnosticReport.Record("Запуск Antigravity","Проверка окна отложена: "+kind);lastFailure=kind;}}finally{Interlocked.Exchange(ref busy,0);}});
        }
        bool Cancelled(){return !enabled||lifetime.IsCancellationRequested;}
        void Check()
        {
            if(Cancelled())return;
            string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Antigravity"),portFile=Path.Combine(profile,"DevToolsActivePort");
            if(!File.Exists(portFile))return;var info=new FileInfo(portFile);if(info.Length>4096)return;
            int port;if(!int.TryParse(File.ReadLines(portFile).FirstOrDefault(),out port)||port<1||port>65535)return;
            int owner=LoopbackTcpOwner.Find(port);if(owner<1)return;
            string image;int session;using(var current=Process.GetCurrentProcess())session=current.SessionId;
            using(var app=Process.GetProcessById(owner)){
                if(!string.Equals(app.ProcessName,"Antigravity",StringComparison.OrdinalIgnoreCase)||app.SessionId!=session||app.StartTime.ToUniversalTime()>info.LastWriteTimeUtc.AddSeconds(5))return;
                image=ProcessSecurity.ImagePath(owner);
            }
            string serverImage=Path.Combine(Path.GetDirectoryName(image),"resources","bin","language_server.exe");if(!File.Exists(serverImage))return;
            var endpoint=new Uri("http://127.0.0.1:"+port+"/json/list");
            var targets=new JavaScriptSerializer{MaxJsonLength=524288}.DeserializeObject(ReadJson(endpoint)) as object[];if(targets==null)return;
            foreach(var raw in targets.Take(8)){
                if(Cancelled()||LoopbackTcpOwner.Find(port)!=owner)return;
                var target=raw as Dictionary<string,object>;if(target==null||Text(target,"type")!="page")continue;
                string address=Text(target,"url"),socket=Text(target,"webSocketDebuggerUrl"),id=Text(target,"id");Uri ws;
                Uri targetUri;if(address!="chrome-error://chromewebdata/"&&(!Uri.TryCreate(address,UriKind.Absolute,out targetUri)||!StartupFrame.LocalHttps(targetUri)))continue;
                if(!Uri.TryCreate(socket,UriKind.Absolute,out ws)||ws.Scheme!="ws"||ws.Host!="127.0.0.1"||ws.Port!=port||!ws.AbsolutePath.StartsWith("/devtools/page/",StringComparison.Ordinal))continue;
                using(var rpc=new StartupPageRpc(ws,lifetime.Token)){
                    bool restored=policy.Visit(owner+":"+id,clock.ElapsedMilliseconds,()=>{var frame=rpc.Frame();observation=frame==null?"Состояние недоступно":frame.Failed?"Окно с ошибкой загрузки":"Страница загружена";return frame;},uri=>BackendReady(uri,serverImage,session),()=>{
                        if(Cancelled()||LoopbackTcpOwner.Find(port)!=owner)throw new OperationCanceledException();
                        rpc.Reload();
                    },address,Cancelled);
                    if(restored)DiagnosticReport.Record("Запуск Antigravity","Локальный сервер готов; повторена загрузка окна с chrome-error. DNS, профиль и процессы не менялись.");
                }
            }
        }
        internal static string Text(Dictionary<string,object> obj,string name){object value;return obj!=null&&obj.TryGetValue(name,out value)?value as string:null;}
        internal static bool BackendReady(Uri uri,string expectedImage,int session,Action<string> trace=null)
        {
            if(!StartupFrame.LocalHttps(uri))return false;int owner=LoopbackTcpOwner.Find(uri.Port);if(owner<1)return false;
            try{using(var process=Process.GetProcessById(owner))if(process.SessionId!=session||!string.Equals(ProcessSecurity.ImagePath(owner),expectedImage,StringComparison.OrdinalIgnoreCase))return false;
                using(var tcp=new TcpClient(AddressFamily.InterNetwork)){
                    tcp.ReceiveTimeout=1500;tcp.SendTimeout=1500;var pending=tcp.BeginConnect(IPAddress.Loopback,uri.Port,null,null);
                    using(var wait=pending.AsyncWaitHandle)if(!wait.WaitOne(1500))return false;tcp.EndConnect(pending);
                    // Desktop uses an ephemeral certificate. Trust is local to
                    // this socket and a verified Language Server listener only.
                    using(var tls=new SslStream(tcp.GetStream(),false,(sender,cert,chain,errors)=>LoopbackTcpOwner.Find(uri.Port)==owner)){
                        tls.ReadTimeout=1500;tls.WriteTimeout=1500;tls.AuthenticateAsClient("localhost",null,SslProtocols.Tls12,false);
                        var request=Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1:"+uri.Port+"\r\nConnection: close\r\n\r\n");tls.Write(request,0,request.Length);
                        var line=new StringBuilder();for(int n=0;n<256;n++){int next=tls.ReadByte();if(next<0||next=='\n')break;line.Append((char)next);}
                        return (line.ToString().StartsWith("HTTP/1.1 200 ",StringComparison.Ordinal)||line.ToString().StartsWith("HTTP/1.0 200 ",StringComparison.Ordinal))&&LoopbackTcpOwner.Find(uri.Port)==owner;
                    }
                }
            }catch(Exception error){if(trace!=null)trace("Local readiness: "+error.GetType().Name+(error is WebException?"; "+((WebException)error).Status:""));return false;}
        }
        static string ReadJson(Uri uri)
        {
            var request=(HttpWebRequest)WebRequest.Create(uri);request.Proxy=null;request.AllowAutoRedirect=false;request.Timeout=1500;request.ReadWriteTimeout=1500;request.KeepAlive=false;
            using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){
                var result=new StringBuilder();var chunk=new char[4096];int size;while((size=reader.Read(chunk,0,chunk.Length))>0){result.Append(chunk,0,size);if(result.Length>524288)throw new IOException("Local debug reply too large");}return result.ToString();
            }
        }
        public void Dispose(){enabled=false;lifetime.Cancel();}
    }
    sealed class StartupPageRpc : IDisposable
    {
        readonly ClientWebSocket socket=new ClientWebSocket();readonly CancellationTokenSource timeout;int sequence;
        internal StartupPageRpc(Uri address,CancellationToken cancel){timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(5000);socket.Options.Proxy=null;try{socket.ConnectAsync(address,timeout.Token).GetAwaiter().GetResult();}catch{Dispose();throw;}}
        Dictionary<string,object> Call(string method)
        {
            int id=++sequence;var serializer=new JavaScriptSerializer{MaxJsonLength=262144};var bytes=Encoding.UTF8.GetBytes(serializer.Serialize(new{id=id,method=method}));
            socket.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,timeout.Token).GetAwaiter().GetResult();
            for(int n=0;n<32;n++){
                using(var data=new MemoryStream()){
                    WebSocketReceiveResult result;var buffer=new byte[4096];do{result=socket.ReceiveAsync(new ArraySegment<byte>(buffer),timeout.Token).GetAwaiter().GetResult();if(result.MessageType!=WebSocketMessageType.Text)throw new IOException("Unexpected local debug message");data.Write(buffer,0,result.Count);if(data.Length>262144)throw new IOException("Local debug frame too large");}while(!result.EndOfMessage);
                    var reply=serializer.DeserializeObject(Encoding.UTF8.GetString(data.ToArray())) as Dictionary<string,object>;object replyId;
                    if(reply==null||!reply.TryGetValue("id",out replyId)||Convert.ToInt32(replyId)!=id)continue;
                    if(reply.ContainsKey("error"))throw new IOException("Local page command rejected");object value;return reply.TryGetValue("result",out value)?value as Dictionary<string,object>:null;
                }
            }throw new IOException("Local page response missing");
        }
        internal StartupFrame Frame()
        {
            var result=Call("Page.getFrameTree");object tree,frame;if(result==null||!result.TryGetValue("frameTree",out tree))return null;
            var map=tree as Dictionary<string,object>;if(map==null||!map.TryGetValue("frame",out frame))return null;var values=frame as Dictionary<string,object>;
            return new StartupFrame{Url=AntigravityStartupRecovery.Text(values,"url"),Unreachable=AntigravityStartupRecovery.Text(values,"unreachableUrl")};
        }
        internal void Reload(){Call("Page.reload");}
        public void Dispose(){socket.Dispose();timeout.Dispose();}
    }
    static class LoopbackTcpOwner
    {
        [DllImport("iphlpapi.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int kind,uint reserved);
        internal static int Find(int port)
        {
            int size=0;uint status=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);if((status!=0&&status!=122)||size<4||size>2097152)return -1;
            var table=Marshal.AllocHGlobal(size);try{if(GetExtendedTcpTable(table,ref size,false,2,3,0)!=0)return -1;int count=Marshal.ReadInt32(table);
                if(count<0||count>(size-4)/24)return -1;
                for(int n=0;n<count;n++){int row=4+n*24;if(Marshal.ReadInt32(table,row)!=2||Marshal.ReadByte(table,row+4)!=127||Marshal.ReadByte(table,row+5)!=0||Marshal.ReadByte(table,row+6)!=0||Marshal.ReadByte(table,row+7)!=1)continue;int current=Marshal.ReadByte(table,row+8)*256+Marshal.ReadByte(table,row+9);if(current==port)return Marshal.ReadInt32(table,row+20);}
                return -1;
            }finally{Marshal.FreeHGlobal(table);}
        }
    }
}
