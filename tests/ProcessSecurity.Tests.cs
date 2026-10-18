using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HbAi.XboxDns;

static class ProcessSecurityTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern IntPtr LoadLibraryExW(string name, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool FreeLibrary(IntPtr module);
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Main()
    {
        string name = "KodVsegoSearchTest-" + Guid.NewGuid().ToString("N") + ".dll";
        string testDir = Path.Combine(Path.GetTempPath(), "KodVsegoSearchTest-" + Guid.NewGuid().ToString("N"));
        string adjacent = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), name);
        string originalDirectory = Environment.CurrentDirectory;
        string originalPath = Environment.GetEnvironmentVariable("PATH");
        Directory.CreateDirectory(testDir);
        try
        {
            // Invalid text placeholders: cannot execute code, even on test failure.
            File.WriteAllText(Path.Combine(testDir, name), "Harmless invalid DLL search test placeholder", Encoding.ASCII);
            File.WriteAllText(adjacent, "Harmless invalid DLL search test placeholder", Encoding.ASCII);
            ProcessSecurity.Initialize();
            Assert(string.Equals(Environment.CurrentDirectory.TrimEnd('\\'), Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "Working directory is not the system directory");
            Console.WriteLine("PASS system working directory");
            Directory.SetCurrentDirectory(testDir);
            Environment.SetEnvironmentVariable("PATH", testDir + ";" + originalPath);
            IntPtr found = LoadLibraryExW(name, IntPtr.Zero, 0);
            int error = Marshal.GetLastWin32Error();
            if (found != IntPtr.Zero) FreeLibrary(found);
            Assert(found == IntPtr.Zero && error == 126, "Loader searched an untrusted directory: error=" + error);
            Console.WriteLine("PASS adjacent, current and PATH directories excluded from default native DLL search");
            IntPtr system = LoadLibraryExW("version.dll", IntPtr.Zero, 0);
            Assert(system != IntPtr.Zero, "System DLL failed to load");
            FreeLibrary(system);
            Console.WriteLine("PASS system DLL loads normally");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            File.Delete(adjacent);
            File.Delete(Path.Combine(testDir, name));
            Directory.Delete(testDir);
        }
        Console.WriteLine("All 3 loader checks passed. DNS and system settings were not changed.");
    }
}
