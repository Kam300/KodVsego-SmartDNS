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
    static void Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--image-path-fixture") {
            File.WriteAllText(args[1]+".ready","ready");
            var clock=Stopwatch.StartNew();
            while(!File.Exists(args[1])&&clock.ElapsedMilliseconds<15000)System.Threading.Thread.Sleep(20);
            return;
        }        string name = "KodVsegoSearchTest-" + Guid.NewGuid().ToString("N") + ".dll";
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
            using(var current=Process.GetCurrentProcess()) {
                string path=ProcessSecurity.ImagePath(current.Id);
                Assert(string.Equals(Path.GetFullPath(path),Path.GetFullPath(Assembly.GetExecutingAssembly().Location),StringComparison.OrdinalIgnoreCase),"Image path lookup did not match this test executable");
                Console.WriteLine("PASS limited-query image path matches the current executable");
            }
            string stop=Path.Combine(testDir,"child-stop.txt");
            string executable=Assembly.GetExecutingAssembly().Location;
            var start=new ProcessStartInfo(executable,"--image-path-fixture \""+stop+"\"") {UseShellExecute=false,CreateNoWindow=true};
            using(var child=Process.Start(start)) {
                try {
                    var ready=Stopwatch.StartNew();while(!File.Exists(stop+".ready")&&ready.ElapsedMilliseconds<5000)System.Threading.Thread.Sleep(20);
                    Assert(File.Exists(stop+".ready"),"Controlled path fixture did not become ready");
                    Assert(string.Equals(ProcessSecurity.ImagePath(child.Id),executable,StringComparison.OrdinalIgnoreCase),"Limited-query child image path differs from the known executable");
                    Console.WriteLine("PASS limited-query image path matches a controlled child process");
                } finally {File.WriteAllText(stop,"stop");Assert(child.WaitForExit(5000),"Controlled child did not finish");File.Delete(stop);File.Delete(stop+".ready");}
            }            bool rejected=false;try{ProcessSecurity.ImagePath(0);}catch(ArgumentOutOfRangeException){rejected=true;}
            Assert(rejected,"Invalid PID was accepted");Console.WriteLine("PASS invalid PID rejected before native lookup");
            rejected=false;try{ProcessSecurity.ImagePath(int.MaxValue);}catch(System.ComponentModel.Win32Exception){rejected=true;}
            Assert(rejected,"Missing process was accepted");Console.WriteLine("PASS unavailable process fails without requesting additional rights");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            File.Delete(adjacent);
            File.Delete(Path.Combine(testDir, name));
            Directory.Delete(testDir);
        }
        Console.WriteLine("All 7 process security checks passed. DNS and system settings were not changed.");
    }
}
