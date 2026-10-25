using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HbAi.XboxDns
{
    // Only the unmodified, hash-pinned upstream Wintun 0.14.1 API is loaded.
    sealed class DnsTunnelNative : IDisposable
    {
        internal static readonly Guid AdapterId=new Guid("6b765364-6e73-4b55-988d-33647b409331");
        internal const string Name="Smart DNS Tunnel beta", V4="198.18.254.2", V6="fd6b:7653:646e::2";
        internal const string X64Hash="e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce";
        internal const string X86Hash="d694fa46ab4cfebcb2632d094c7aa97278eef2f8052438621766d863ae98a931";
        [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern IntPtr LoadLibraryExW(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Ansi,SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.StdCall,CharSet=CharSet.Unicode,SetLastError=true)] delegate IntPtr Create(string name,string type,ref Guid id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Close(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Luid(IntPtr adapter,out ulong luid);
        [UnmanagedFunctionPointer(CallingConvention.StdCall,SetLastError=true)] delegate IntPtr Start(IntPtr adapter,uint capacity);
        [UnmanagedFunctionPointer(CallingConvention.StdCall,SetLastError=true)] delegate IntPtr Receive(IntPtr session,out uint size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Packet(IntPtr session,IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.StdCall,SetLastError=true)] delegate IntPtr Allocate(IntPtr session,uint size);
        [StructLayout(LayoutKind.Explicit,Size=80)] internal struct AddressRow {
            [FieldOffset(0)] public ushort Family;
            [FieldOffset(4)] public uint Ipv4;
            [FieldOffset(8)] public ulong Ipv6Low;
            [FieldOffset(16)] public ulong Ipv6High;
            [FieldOffset(32)] public ulong InterfaceLuid;
            [FieldOffset(60)] public byte Prefix;
            [FieldOffset(64)] public int DadState;
        }
        [DllImport("iphlpapi.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern void InitializeUnicastIpAddressEntry(out AddressRow row);
        [DllImport("iphlpapi.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] static extern uint CreateUnicastIpAddressEntry(ref AddressRow row);
        IntPtr library,adapter,session;
        Close close,end; Receive receive; Packet release,send; Allocate allocate;
        readonly object gate=new object();
        FileStream pinnedFile;
        string loadedPath;
        T Export<T>(string name) where T:class {var p=GetProcAddress(library,"Wintun"+name);if(p==IntPtr.Zero)throw new IOException("Неполная библиотека Wintun.");return Marshal.GetDelegateForFunctionPointer(p,typeof(T)) as T;}
        internal static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        internal static string ExpectedHash{get{return IntPtr.Size==8?X64Hash:X86Hash;}}
        internal static string[] CandidatePaths(string baseDirectory=null)
        {
            string root=string.IsNullOrEmpty(baseDirectory)?AppDomain.CurrentDomain.BaseDirectory:baseDirectory;
            string architecture=IntPtr.Size==8?"x64":"x86";
            return new[]{
                Path.Combine(root,"wintun.dll"),
                Path.Combine(root,"wintun-"+architecture+".dll"),
                Path.Combine(root,"res","wintun","wintun-"+architecture+".dll")
            };
        }
        internal static bool IsAvailable(string baseDirectory=null)
        {
            string expected=ExpectedHash;
            foreach(string path in CandidatePaths(baseDirectory))
                try{if(File.Exists(path)&&Hash(File.ReadAllBytes(path))==expected)return true;}
                catch(IOException){}catch(UnauthorizedAccessException){}
            return false;
        }
        internal static byte[] ReadVerifiedBytes(string baseDirectory=null)
        {
            string expected=ExpectedHash;
            bool foundAny=false;
            foreach(string path in CandidatePaths(baseDirectory))
            {
                if(!File.Exists(path))continue;
                foundAny=true;
                byte[] bytes=File.ReadAllBytes(path);
                if(Hash(bytes)==expected)return bytes;
            }
            if(foundAny)throw new IOException("Файл wintun.dll рядом с программой имеет неподходящую разрядность или контрольную сумму (нужен официальный Wintun 0.14.1 "+(IntPtr.Size==8?"x64":"x86")+"). DNS не изменён.");
            throw new IOException("В папке программы нет wintun.dll (одиночный EXE). Выберите «Способ подключения → 2 · Через приложение» или скачайте ZIP / MSI со страницы релиза.");
        }
        internal void Open()
        {
            string expected=ExpectedHash;
            byte[] bytes=ReadVerifiedBytes();
            string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"KodVsego-SmartDNS-Wintun",expected);
            Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"wintun.dll");
            if(!File.Exists(path))using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
            pinnedFile=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            using(var memory=new MemoryStream()){pinnedFile.CopyTo(memory);if(Hash(memory.ToArray())!=expected)throw new IOException("Файл Wintun изменён. DNS не изменён.");}
            loadedPath=path;
            library=LoadLibraryExW(path,IntPtr.Zero,0x1100);
            if(library==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Не удалось загрузить Wintun.");
            close=Export<Close>("CloseAdapter");end=Export<Close>("EndSession");
            receive=Export<Receive>("ReceivePacket");release=Export<Packet>("ReleaseReceivePacket");send=Export<Packet>("SendPacket");allocate=Export<Allocate>("AllocateSendPacket");
            var id=AdapterId;adapter=Export<Create>("CreateAdapter")(Name,"Smart DNS",ref id);
            if(adapter==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Не удалось создать DNS-адаптер.");
            ulong luid;Export<Luid>("GetAdapterLUID")(adapter,out luid);
            AddAddress(luid,"198.18.254.1",30);AddAddress(luid,"fd6b:7653:646e::1",64);
            session=Export<Start>("StartSession")(adapter,0x400000);
            if(session==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Не удалось открыть DNS-туннель.");
        }
        static void AddAddress(ulong luid,string address,byte prefix)
        {
            AddressRow row;InitializeUnicastIpAddressEntry(out row);row.InterfaceLuid=luid;row.Prefix=prefix;row.DadState=4;
            byte[] bytes=IPAddress.Parse(address).GetAddressBytes();row.Family=(ushort)(bytes.Length==4?2:23);
            if(bytes.Length==4)row.Ipv4=BitConverter.ToUInt32(bytes,0);else{row.Ipv6Low=BitConverter.ToUInt64(bytes,0);row.Ipv6High=BitConverter.ToUInt64(bytes,8);}
            uint code=CreateUnicastIpAddressEntry(ref row);if(code!=0)throw new Win32Exception((int)code,"Не удалось настроить адрес DNS-туннеля.");
        }
        internal byte[] Read()
        {
            lock(gate){uint size;IntPtr packet=receive(session,out size);
                if(packet==IntPtr.Zero){int error=Marshal.GetLastWin32Error();if(error==259)return null;throw new Win32Exception(error);}
                try{if(size>65535)throw new IOException("Слишком большой пакет TUN.");var bytes=new byte[size];Marshal.Copy(packet,bytes,0,(int)size);return bytes;}finally{release(session,packet);}}
        }
        internal void Write(byte[] bytes)
        {
            lock(gate){if(session==IntPtr.Zero)return;IntPtr packet=allocate(session,(uint)bytes.Length);if(packet==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());Marshal.Copy(bytes,0,packet,bytes.Length);send(session,packet);}
        }
        public void Dispose(){lock(gate){if(session!=IntPtr.Zero){end(session);session=IntPtr.Zero;}if(adapter!=IntPtr.Zero){close(adapter);adapter=IntPtr.Zero;}if(library!=IntPtr.Zero){FreeLibrary(library);library=IntPtr.Zero;}if(pinnedFile!=null){pinnedFile.Dispose();pinnedFile=null;}
            // Delete only this exact extracted copy; never uninstall the shared driver.
            if(loadedPath!=null)try{File.Delete(loadedPath);string directory=Path.GetDirectoryName(loadedPath);Directory.Delete(directory,false);Directory.Delete(Path.GetDirectoryName(directory),false);}catch(IOException){}catch(UnauthorizedAccessException){}
        }}
    }
}
