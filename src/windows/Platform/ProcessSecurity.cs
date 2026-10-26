using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HbAi.XboxDns
{
    static class ProcessSecurity
    {
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetDefaultDllDirectories(uint flags);

        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint processId);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,StringBuilder path,ref uint size);

        internal static string ImagePath(int processId)
        {
            if(processId<=0)throw new ArgumentOutOfRangeException("processId");
            // Only query the image path. Do not request PROCESS_VM_READ or enumerate modules.
            using(var process=OpenProcess(0x1000,false,(uint)processId)) {
                if(process.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
                var path=new StringBuilder(32768);uint size=(uint)path.Capacity;
                if(!QueryFullProcessImageNameW(process,0,path,ref size))throw new Win32Exception(Marshal.GetLastWin32Error());
                if(size==0)throw new IOException("Windows вернула пустой путь процесса.");
                return path.ToString();
            }
        }
        internal static void Initialize()
        {
            // No application-native plug-ins are used. Keep default native DLL
            // searches out of the executable directory, working directory and PATH.
            // Full-path framework loads and Windows loader redirection still apply.
            if (!SetDefaultDllDirectories(0x00000800))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось ограничить поиск системных библиотек.");
            Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.System));
        }
    }
}
