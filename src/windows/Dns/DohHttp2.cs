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
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace HbAi.XboxDns
{
    // .NET Framework HttpWebRequest only speaks HTTP/1.1. DNS-AI requires
    // a modern HTTP transport, supplied by Windows WinHTTP with HTTP/2 enabled.
    static class DohHttp2
    {
        internal const int RequestTimeoutMilliseconds=8000;
        static readonly DohAddressHealth AddressHealth=new DohAddressHealth();
        static int pinningUnavailable;
        static DohHttp2()
        {
            NetworkChange.NetworkAddressChanged+=(sender,args)=>ResetAddressHealth();
            NetworkChange.NetworkAvailabilityChanged+=(sender,args)=>ResetAddressHealth();
        }
        internal static void ResetAddressHealth() { AddressHealth.Reset(); }
        internal sealed class AddressPinningUnavailableException : NotSupportedException { }
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
            if(endpoint==EncryptedDns.Templates[5]&&Interlocked.CompareExchange(ref pinningUnavailable,0,0)==0) {
                try {return QueryHealthyAddresses(endpoint,dns,EncryptedDns.ServersFor(5),AddressHealth,
                    (url,address,packet)=>Query(url,packet,RequestTimeoutMilliseconds,address),trace);}
                catch(AddressPinningUnavailableException) {
                    // Windows before 10 21H1 cannot override resolution per request.
                    // Retain the original WinHTTP/TLS/HTTP2 path on those systems.
                    Interlocked.Exchange(ref pinningUnavailable,1);
                    if(trace!=null)trace("DoH: per-address resolution unsupported; using normal WinHTTP hostname resolution with certificate validation");
                }
            }
            int attempt=0;
            return QueryReliable(endpoint,dns,(url,packet)=> {
                attempt++;
                if(url==EncryptedDns.Templates[0])return QueryAlternateAddresses(url,packet,trace);
                if(attempt==2&&url==EncryptedDns.Templates[1])return QueryAlternateAddresses(url,packet,trace);
                return Query(url,packet,RequestTimeoutMilliseconds);
            },trace);
        }
        internal static byte[] QueryHealthyAddresses(string endpoint,byte[] dns,string[] addresses,DohAddressHealth health,
            Func<string,string,byte[],byte[]> send,Action<string> trace)
        {
            long generation;var ready=health.Ready(endpoint,addresses,out generation);Exception failure=null;
            if(ready.Length==0) {
                if(trace!=null)trace("DoH endpoint="+new Uri(endpoint).Host+"; all addresses in temporary cooldown; skipped="+string.Join(", ",addresses));
                throw new IOException("Адреса DoH-сервера временно пропущены после сетевых ошибок. Повторите проверку позже или нажмите «Тест DNS».");
            }
            if(trace!=null&&ready.Length<addresses.Length)trace("DoH: temporarily skipped addresses="+string.Join(", ",addresses.Except(ready)));
            foreach(string address in ready) {
                var clock=Stopwatch.StartNew();
                if(trace!=null)trace("HTTPS endpoint="+new Uri(endpoint).Host+"; target IP="+address+"; SNI/certificate host="+new Uri(endpoint).Host+"; HTTP/2 enabled");
                try {
                    byte[] response=send(endpoint,address,dns);DnsProbe.Validate(dns,response);
                    health.Success(endpoint,address,generation);
                    if(trace!=null)trace("DoH address="+address+"; elapsed="+clock.ElapsedMilliseconds+" ms; TLS/DNS validated");
                    return response;
                }catch(Exception error) {
                    if(trace!=null)trace("DoH address="+address+"; elapsed="+clock.ElapsedMilliseconds+" ms\n"+error);
                    // Certificate, HTTP and invalid DNS errors are never hidden by
                    // switching addresses or by falling back to unencrypted DNS.
                    if(!CanRetry(error))throw;
                    health.Failure(endpoint,address,generation);failure=error;
                }
            }
            throw new IOException("Ни один из адресов DoH-сервера не ответил по HTTPS.",failure);
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
        [DllImport("winhttp.dll", EntryPoint="WinHttpSetOption", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WinHttpSetOptionString(IntPtr handle,uint option,string value,uint length);
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
        internal static byte[] Query(string endpoint,byte[] dns,int timeoutMilliseconds=5000,string address=null)
        {
            if(timeoutMilliseconds<1||timeoutMilliseconds>15000)throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Uri uri;
            if(!Uri.TryCreate(endpoint,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")
                throw new IOException("Некорректный HTTPS endpoint DoH.");
            IPAddress target;
            if(address!=null&&(!IPAddress.TryParse(address,out target)||target.AddressFamily!=AddressFamily.InterNetwork||!AutoDnsProbe.PublicAddress(address)))
                throw new IOException("Некорректный IPv4 адрес DoH-сервера.");
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
                if(address!=null&&!WinHttpSetOptionString(request,165,address,(uint)((address.Length+1)*2))) {
                    int code=Marshal.GetLastWin32Error();
                    if(code==12009)throw new AddressPinningUnavailableException();
                    throw new Win32Exception(code,"WinHTTP: resolution hostname; code="+code);
                }
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

    // Process-local resolver health. A monotonic clock avoids clock changes;
    // generations prevent old requests poisoning a cache reset after reconnect.
    sealed class DohAddressHealth
    {
        sealed class State { internal int Failures;internal long RetryAt; }
        readonly object gate=new object();
        readonly Dictionary<string,State> states=new Dictionary<string,State>(StringComparer.Ordinal);
        readonly Func<long> now;long generation;
        internal DohAddressHealth() {var clock=Stopwatch.StartNew();now=()=>clock.ElapsedMilliseconds;}
        internal DohAddressHealth(Func<long> clock) {now=clock;}
        static string Key(string endpoint,string address) {return endpoint+"|"+address;}
        internal string[] Ready(string endpoint,string[] addresses,out long version)
        {
            lock(gate) {version=generation;long time=now();return addresses.Where(address=> {
                State state;return !states.TryGetValue(Key(endpoint,address),out state)||state.RetryAt<=time;
            }).ToArray();}
        }
        internal void Success(string endpoint,string address,long version)
        {lock(gate)if(version==generation)states.Remove(Key(endpoint,address));}
        internal void Failure(string endpoint,string address,long version)
        {
            lock(gate) {
                if(version!=generation)return;
                string key=Key(endpoint,address);State state;
                if(!states.TryGetValue(key,out state)) {
                    if(states.Count>=128) {states.Clear();generation++;return;}
                    states[key]=state=new State();
                }
                state.Failures=Math.Min(6,state.Failures+1);
                state.RetryAt=now()+Math.Min(900000,30000L<<(state.Failures-1));
            }
        }
        internal void Reset() {lock(gate){states.Clear();generation++;}}
    }
}
