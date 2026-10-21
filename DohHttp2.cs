using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Linq;
using System.Threading;

namespace HbAi.XboxDns
{
    // .NET Framework HttpWebRequest only speaks HTTP/1.1. DNS-AI requires
    // a modern HTTP transport, supplied by Windows WinHTTP with HTTP/2 enabled.
    static class DohHttp2
    {
        internal const int RequestTimeoutMilliseconds=8000;
        internal static bool CanRetry(Exception error)
        {
            if(error is TimeoutException||error is SocketException)return true;
            if(error is IOException&&error.InnerException!=null)return CanRetry(error.InnerException);
            var native=error as Win32Exception;return native!=null&&(native.NativeErrorCode==12002||native.NativeErrorCode==12029||native.NativeErrorCode==12030||native.NativeErrorCode==12031);
        }
        internal static string[] ResolverAddresses(string endpoint,Func<string,string[]> resolve)
        {
            if(endpoint==EncryptedDns.Templates[0])return EncryptedDns.ServersFor(0);
            return resolve(new Uri(endpoint).Host).Where(AutoDnsProbe.PublicAddress).Take(6).ToArray();
        }
        internal static byte[] QueryReliable(string endpoint,byte[] dns,Action<string> trace=null)
        {
            int attempt=0;
            return QueryReliable(endpoint,dns,(url,packet)=> {
                attempt++;
                if(url==EncryptedDns.Templates[0])return QueryAlternateAddresses(url,packet,trace);
                if(attempt==2&&url==EncryptedDns.Templates[1])return QueryAlternateAddresses(url,packet,trace);
                return Query(url,packet,RequestTimeoutMilliseconds);
            },trace);
        }
        internal static byte[] QueryReliable(string endpoint,byte[] dns,Func<string,byte[],byte[]> send,Action<string> trace)
        {
            for(int attempt=1;;attempt++) {
                var clock=Stopwatch.StartNew();
                try {var response=send(endpoint,dns);if(trace!=null)trace("DoH; attempt="+attempt+"; elapsed="+clock.ElapsedMilliseconds+" ms; response bytes="+response.Length);return response;}
                catch(Exception error) {
                    if(trace!=null)trace("DoH; attempt="+attempt+"; elapsed="+clock.ElapsedMilliseconds+" ms\n"+error);
                    if(attempt>=2||!CanRetry(error))throw;
                }
            }
        }
        static byte[] QueryAlternateAddresses(string endpoint,byte[] dns,Action<string> trace)
        {
            // Comss uses the same bootstrap IP as Windows DoH. GeoHide can try
            // multiple resolved addresses. Neither path changes the TLS hostname.
            var uri=new Uri(endpoint);Exception failure=null;
            foreach(string address in ResolverAddresses(endpoint,host=>System.Net.Dns.GetHostAddresses(host).Select(ip=>ip.ToString()).ToArray())) {
                var ip=IPAddress.Parse(address);
                var clock=Stopwatch.StartNew();
                if(trace!=null)trace("HTTPS endpoint="+uri.Host+"; target IP="+address+"; SNI/certificate host="+uri.Host);
                try {
                    using(var tcp=new TcpClient(AddressFamily.InterNetwork)) {
                        var connection=tcp.BeginConnect(ip,443,null,null);
                        using(var wait=connection.AsyncWaitHandle)if(!wait.WaitOne(3500))throw new TimeoutException("Нет соединения с адресом DoH-сервера.");
                        tcp.EndConnect(connection);
                        using(var ssl=new SslStream(tcp.GetStream(),false)) {
                            ssl.ReadTimeout=RequestTimeoutMilliseconds;ssl.WriteTimeout=RequestTimeoutMilliseconds;
                            var auth=ssl.BeginAuthenticateAsClient(uri.Host,null,SslProtocols.Tls12,true,null,null);
                            using(var wait=auth.AsyncWaitHandle)if(!wait.WaitOne(RequestTimeoutMilliseconds))throw new TimeoutException("DoH-сервер не ответил при согласовании TLS.");
                            ssl.EndAuthenticateAsClient(auth);
                            string encoded=Convert.ToBase64String(dns).TrimEnd('=').Replace('+','-').Replace('/','_');
                            byte[] request=Encoding.ASCII.GetBytes("GET "+uri.AbsolutePath+"?dns="+encoded+" HTTP/1.1\r\nHost: "+uri.Host+"\r\nUser-Agent: KodVsego-SmartDNS\r\nAccept: application/dns-message\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n");
                            ssl.Write(request,0,request.Length);
                            using(var output=new MemoryStream()) {
                                string location;
                                int status=AppUpdate.ReadResponse(ssl,output,65535,CancellationToken.None,null,out location);
                                if(status!=200)throw new IOException("DoH HTTP "+status+"; redirect not followed");
                                var response=output.ToArray();DnsProbe.Validate(dns,response);
                                if(trace!=null)trace("HTTPS alternate address="+ip+"; elapsed="+clock.ElapsedMilliseconds+" ms; TLS/DNS validated");
                                return response;
                            }
                        }
                    }
                }catch(Exception error){
                    if(!(error is IOException||error is SocketException||error is AuthenticationException||error is TimeoutException))throw;
                    failure=error;if(trace!=null)trace("HTTPS alternate address="+ip+"; elapsed="+clock.ElapsedMilliseconds+" ms\n"+error);
                }
            }
            throw new IOException("Ни один из проверенных адресов DoH-сервера не ответил по HTTPS.",failure);
        }
        [DllImport("winhttp.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr WinHttpOpen(string agent,uint access,string proxy,string bypass,uint flags);
        [DllImport("winhttp.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr WinHttpConnect(IntPtr session,string host,ushort port,uint reserved);
        [DllImport("winhttp.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr WinHttpOpenRequest(IntPtr connect,string verb,string path,string version,string referer,IntPtr accepts,uint flags);
        [DllImport("winhttp.dll", ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WinHttpSetOption(IntPtr handle,uint option,ref uint value,uint length);
        [DllImport("winhttp.dll", ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WinHttpSetTimeouts(IntPtr handle,int resolve,int connect,int send,int receive);
        [DllImport("winhttp.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WinHttpSendRequest(IntPtr handle,string headers,uint headerLength,IntPtr optional,uint optionalLength,uint totalLength,IntPtr context);
        [DllImport("winhttp.dll", ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WinHttpReceiveResponse(IntPtr handle,IntPtr reserved);
        [DllImport("winhttp.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WinHttpQueryHeaders(IntPtr handle,uint level,string name,out uint buffer,ref uint size,IntPtr index);
        [DllImport("winhttp.dll", ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WinHttpReadData(IntPtr handle,byte[] buffer,uint length,out uint read);
        [DllImport("winhttp.dll", ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WinHttpCloseHandle(IntPtr handle);
        static void Check(bool success,string operation)
        {if(!success){int code=Marshal.GetLastWin32Error();throw new Win32Exception(code,"WinHTTP: "+operation+"; code="+code);}}
        internal static byte[] Query(string endpoint,byte[] dns,int timeoutMilliseconds=5000)
        {
            if(timeoutMilliseconds<1||timeoutMilliseconds>15000)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Uri uri;
            if(!Uri.TryCreate(endpoint,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")
                throw new IOException("Некорректный HTTPS endpoint DoH.");
            string encoded=Convert.ToBase64String(dns).TrimEnd('=').Replace('+','-').Replace('/','_');
            IntPtr session=IntPtr.Zero,connect=IntPtr.Zero,request=IntPtr.Zero;
            try
            {
                session=WinHttpOpen("KodVsego-SmartDNS",1,null,null,0); // NO_PROXY; no user credentials.
                Check(session!=IntPtr.Zero,"open");
                Check(WinHttpSetTimeouts(session,timeoutMilliseconds,timeoutMilliseconds,timeoutMilliseconds,timeoutMilliseconds),"timeouts");
                uint protocols=1;Check(WinHttpSetOption(session,133,ref protocols,4),"enable HTTP/2");
                uint tls12=0x800;Check(WinHttpSetOption(session,84,ref tls12,4),"TLS 1.2");
                connect=WinHttpConnect(session,uri.Host,443,0);Check(connect!=IntPtr.Zero,"connect");
                request=WinHttpOpenRequest(connect,"GET",uri.AbsolutePath+"?dns="+encoded,null,null,IntPtr.Zero,0x800000);
                Check(request!=IntPtr.Zero,"request");
                uint noRedirect=0;Check(WinHttpSetOption(request,88,ref noRedirect,4),"disable redirects");
                uint noAutoLogon=2;Check(WinHttpSetOption(request,77,ref noAutoLogon,4),"disable automatic credentials");
                const string headers="Accept: application/dns-message\r\n";
                Check(WinHttpSendRequest(request,headers,(uint)headers.Length,IntPtr.Zero,0,0,IntPtr.Zero),"send");
                Check(WinHttpReceiveResponse(request,IntPtr.Zero),"receive");
                uint size=4,status;Check(WinHttpQueryHeaders(request,19|0x20000000,null,out status,ref size,IntPtr.Zero),"status");
                if(status!=200)throw new IOException("DoH HTTP "+status);
                using(var output=new MemoryStream())
                {
                    var buffer=new byte[4096];uint count;
                    while(true) {
                        Check(WinHttpReadData(request,buffer,(uint)buffer.Length,out count),"read");
                        if(count==0)break;
                        if(output.Length+count>65535)throw new IOException("Слишком большой ответ DoH.");
                        output.Write(buffer,0,(int)count);
                    }
                    return output.ToArray();
                }
            }
            finally {if(request!=IntPtr.Zero)WinHttpCloseHandle(request);if(connect!=IntPtr.Zero)WinHttpCloseHandle(connect);if(session!=IntPtr.Zero)WinHttpCloseHandle(session);}
        }
    }
}
