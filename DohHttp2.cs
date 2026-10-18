using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace HbAi.XboxDns
{
    // .NET Framework HttpWebRequest only speaks HTTP/1.1. DNS-AI requires
    // a modern HTTP transport, supplied by Windows WinHTTP with HTTP/2 enabled.
    static class DohHttp2
    {
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
        {if(!success)throw new Win32Exception(Marshal.GetLastWin32Error(),"WinHTTP: "+operation);}
        internal static byte[] Query(string endpoint,byte[] dns)
        {
            Uri uri;
            if(!Uri.TryCreate(endpoint,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")
                throw new IOException("Некорректный HTTPS endpoint DoH.");
            string encoded=Convert.ToBase64String(dns).TrimEnd('=').Replace('+','-').Replace('/','_');
            IntPtr session=IntPtr.Zero,connect=IntPtr.Zero,request=IntPtr.Zero;
            try
            {
                session=WinHttpOpen("KodVsego-SmartDNS",1,null,null,0); // NO_PROXY; no user credentials.
                Check(session!=IntPtr.Zero,"open");
                Check(WinHttpSetTimeouts(session,5000,5000,5000,5000),"timeouts");
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
