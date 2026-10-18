using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace HbAi.XboxDns
{
    static class ProcessSecurity
    {
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetDefaultDllDirectories(uint flags);

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
