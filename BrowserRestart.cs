using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using System.Management;

namespace HbAi.XboxDns
{
    static class RestartPrompts
    {
        internal const string BrowserValue="HideDnsReminder", AntigravityValue="HideAntigravityRestartDialog";
        internal static bool Hidden(RegistryKey key,string name)
        {return key!=null&&object.Equals(key.GetValue(name),1);}
        internal static void Save(RegistryKey key,bool browser,bool antigravity)
        {
            key.SetValue(BrowserValue,browser?0:1,RegistryValueKind.DWord);
            key.SetValue(AntigravityValue,antigravity?0:1,RegistryValueKind.DWord);
        }
    }
    sealed class BrowserChoice
    {
        internal string Name, Exe;
        public override string ToString() { return Name; }
    }
    static class BrowserCatalog
    {
        internal static string ExeFromCommand(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = Environment.ExpandEnvironmentVariables(value.Trim());
            string path;
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = value.IndexOf('"', 1);
                if (end < 0) return null;
                path = value.Substring(1, end - 1);
            }
            else
            {
                int end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end < 0) return null;
                path = value.Substring(0, end + 4);
            }
            if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            try { return Path.GetFullPath(path); } catch { return null; }
        }
        internal static string NameFor(string path)
        {
            switch (Path.GetFileName(path).ToLowerInvariant())
            {
                case "chrome.exe": return "Google Chrome";
                case "msedge.exe": return "Microsoft Edge";
                case "firefox.exe": return "Mozilla Firefox";
                case "brave.exe": return "Brave";
                case "vivaldi.exe": return "Vivaldi";
                case "browser.exe": return path.IndexOf("Yandex", StringComparison.OrdinalIgnoreCase) >= 0 ? "Яндекс Браузер" : null;
                case "opera.exe": case "launcher.exe": return path.IndexOf("Opera", StringComparison.OrdinalIgnoreCase) >= 0 ? "Opera" : null;
                default: return null;
            }
        }
        internal static BrowserChoice[] Find()
        {
            var found = new List<BrowserChoice>();
            Action<string> add = command => {
                string path = ExeFromCommand(command);
                if (path == null || !File.Exists(path)) return;
                string name = NameFor(path);
                if (name != null && !found.Any(b => string.Equals(b.Exe, path, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new BrowserChoice { Name = name, Exe = path });
            };
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        {
                            using (var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet"))
                                if (clients != null) foreach (string client in clients.GetSubKeyNames())
                                    using (var key = clients.OpenSubKey(client + @"\shell\open\command"))
                                        if (key != null) add(key.GetValue(null) as string);
                            foreach (string exe in new[] { "chrome.exe", "msedge.exe", "firefox.exe", "browser.exe", "brave.exe", "vivaldi.exe", "opera.exe" })
                                using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                                    if (key != null) add(key.GetValue(null) as string);
                        }
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                foreach (string relative in new[] { @"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe",
                    @"Mozilla Firefox\firefox.exe", @"Yandex\YandexBrowser\Application\browser.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe",
                    @"Vivaldi\Application\vivaldi.exe", @"Programs\Opera\opera.exe", @"Opera\opera.exe", @"Programs\Opera\launcher.exe" })
                    if (!string.IsNullOrEmpty(root)) add(Path.Combine(root, relative));
            return found.GroupBy(b => b.Name).Select(g => g.First()).OrderBy(b => b.Name).ToArray();
        }
        internal static bool Matches(BrowserChoice browser, string path)
        {
            if (string.Equals(browser.Exe, path, StringComparison.OrdinalIgnoreCase)) return true;
            if (browser.Name == "Antigravity")
                return string.Equals(path, Path.Combine(Path.GetDirectoryName(browser.Exe), @"resources\bin\language_server.exe"), StringComparison.OrdinalIgnoreCase);
            // Opera's launcher starts opera.exe from a version subdirectory.
            string root = Path.GetDirectoryName(browser.Exe).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return browser.Name == "Opera" && Path.GetFileName(path).Equals("opera.exe", StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
    interface IBrowserProcess : IDisposable
    {
        bool Exited { get; }
        bool HasWindow { get; }
        bool CloseWindow();
    }
    interface IBrowserSystem
    {
        void EnsureOrdinaryUser();
        IBrowserProcess[] Running(BrowserChoice browser);
        BrowserLaunch[] Capture(BrowserChoice browser, IBrowserProcess[] windows);
        void Open(BrowserChoice browser, BrowserLaunch[] launches);
        void Wait();
    }
    sealed class WindowsBrowserSystem : IBrowserSystem
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RestartProcess { internal uint Id; internal System.Runtime.InteropServices.ComTypes.FILETIME Start; }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int RmStartSession(out uint handle, uint flags, System.Text.StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int RmRegisterResources(uint handle, uint files, IntPtr names, uint applications, RestartProcess[] processes, uint services, IntPtr serviceNames);
        [DllImport("rstrtmgr.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int RmShutdown(uint handle, uint flags, IntPtr callback);
        [DllImport("rstrtmgr.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int RmEndSession(uint handle);
        static bool CloseYandex(Process process)
        {
            uint handle;
            if (RmStartSession(out handle, 0, new System.Text.StringBuilder(33)) != 0) return false;
            try
            {
                long time = process.StartTime.ToFileTime();
                var item = new RestartProcess { Id = (uint)process.Id, Start = new System.Runtime.InteropServices.ComTypes.FILETIME {
                    dwLowDateTime = (int)time, dwHighDateTime = (int)(time >> 32) } };
                // Register only this root PID and its creation time; no files/services and no force flag.
                return RmRegisterResources(handle, 0, IntPtr.Zero, 1, new[] { item }, 0, IntPtr.Zero) == 0 && RmShutdown(handle, 0, IntPtr.Zero) == 0;
            }
            finally { RmEndSession(handle); }
        }
        delegate bool WindowCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool EnumWindows(WindowCallback callback, IntPtr data);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        static IntPtr[] Windows(int pid)
        {
            var windows = new List<IntPtr>();
            if (!EnumWindows((window, data) => {
                uint owner; GetWindowThreadProcessId(window, out owner);
                if (owner == (uint)pid && IsWindowVisible(window)) windows.Add(window);
                return true;
            }, IntPtr.Zero)) throw new IOException("Не удалось прочитать окна браузера.");
            return windows.ToArray();
        }
        sealed class Instance : IBrowserProcess
        {
            readonly Process process;
            readonly bool yandex;
            internal bool Background;
            internal Instance(Process process, bool yandex) { this.process = process; this.yandex = yandex; }
            public bool Exited { get { return process.HasExited; } }
            public bool HasWindow { get { return !Background && !Exited && Windows(process.Id).Length > 0; } }
            public bool CloseWindow()
            {
                if (Background) return false;
                if (yandex) return CloseYandex(process);
                foreach (var window in Windows(process.Id))
                {
                    uint owner; GetWindowThreadProcessId(window, out owner);
                    if (owner == (uint)process.Id && !PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) return false;
                }
                return true;
            }
            internal string Command()
            {
                if (Exited) throw new IOException("Браузер завершился во время определения профиля. Повторите попытку.");
                string command = null;
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + process.Id))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(10);
                    using (var rows = searcher.Get())
                        foreach (ManagementBaseObject row in rows)
                            using (row) command = row["CommandLine"] as string;
                }
                if (Exited) throw new IOException("Браузер завершился во время определения профиля. Повторите попытку.");
                return command;
            }
            public void Dispose() { process.Dispose(); }
        }
        public void EnsureOrdinaryUser()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("Для перезапуска браузера откройте переключатель DNS обычным способом, без запуска от имени администратора.");
        }
        public IBrowserProcess[] Running(BrowserChoice browser)
        {
            if (!File.Exists(browser.Exe)) throw new IOException("Файл браузера больше не найден. Перезапустите переключатель DNS для обновления списка.");
            var found = new List<IBrowserProcess>();
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            try
            {
                foreach (string name in new[] { Path.GetFileNameWithoutExtension(browser.Exe), browser.Name == "Opera" ? "opera" : null, browser.Name == "Antigravity" ? "language_server" : null }.Where(n => n != null).Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        bool keep = false;
                        try
                        {
                            if (process.HasExited || process.SessionId != session) continue;
                            string image = ProcessSecurity.ImagePath(process.Id);
                            if (BrowserCatalog.Matches(browser, image))
                            {
                                var instance = new Instance(process, browser.Name == "Яндекс Браузер");
                                bool desktop = browser.Name == "Antigravity";
                                bool backend = desktop && !string.Equals(image, browser.Exe, StringComparison.OrdinalIgnoreCase);
                                string type = backend ? null : BrowserProfiles.Option(BrowserProfiles.Split(instance.Command()), "--type");
                                // Desktop's localhost UI is served by a separate language_server.
                                // Wait for its server/renderers too; never send them window-close messages.
                                // Detached crash reporters may intentionally survive a clean shutdown.
                                if (backend || type == null || desktop && AntigravityRestart.WaitForType(type))
                                { instance.Background = backend || type != null; found.Add(instance); keep = true; }
                            }
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception)
                        { throw new IOException("Не удалось проверить процессы браузера. Закройте его вручную и повторите попытку."); }
                        finally { if (!keep) process.Dispose(); }
                    }
                return found.ToArray();
            }
            catch { foreach (var item in found) item.Dispose(); throw; }
        }
        static bool Locked(string path)
        {
            if (!File.Exists(path)) return false;
            try { using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { } return false; }
            catch (IOException e)
            {
                int code = e.HResult & 0xffff;
                if (code == 32 || code == 33) return true;
                throw;
            }
        }
        public BrowserLaunch[] Capture(BrowserChoice browser, IBrowserProcess[] windows)
        {
            if (windows.Length == 0) return new[] { new BrowserLaunch { Arguments = "" } };
            var commands = windows.Cast<Instance>().Select(p => p.Command()).ToArray();
            if (browser.Name == "Antigravity") return AntigravityRestart.Capture(commands, Directory.Exists);
            var roots = commands.Where(c => BrowserProfiles.Option(BrowserProfiles.Split(c), "--type") == null).ToArray();
            if (roots.Length == 0) throw new IOException("Не удалось определить основной процесс браузера. Окна не закрыты.");
            var launches = BrowserProfiles.Capture(browser, roots, File.ReadAllText, Directory.Exists, Locked);
            if (launches.Length == 0) throw new IOException("Не удалось определить профиль браузера. Окна не закрыты.");
            return launches;
        }
        public void Open(BrowserChoice browser, BrowserLaunch[] launches)
        {
            // Only newly constructed profile arguments, never the original command line.
            foreach (var launch in launches)
            {
                var start = new ProcessStartInfo(browser.Exe, launch.Arguments) {
                    UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(browser.Exe)
                };
                if (browser.Name == "Antigravity") AntigravityRestart.PrepareEnvironment(start);
                using (var process = Process.Start(start)) { }
            }
        }
        public void Wait() { Thread.Sleep(100); }
    }
    static class BrowserRestart
    {
        internal static void Run(BrowserChoice browser, IBrowserSystem system, bool onlyRunning = false)
        {
            system.EnsureOrdinaryUser();
            var processes = system.Running(browser);
            try
            {
                if (onlyRunning && !processes.Any(p => !p.Exited)) return;
                var windows = processes.Where(p => !p.Exited && (p.HasWindow || browser.Name == "Яндекс Браузер")).ToArray();
                if (processes.Any(p => !p.Exited) && windows.Length == 0)
                    throw new IOException("Браузер работает в фоне. Полностью завершите его через меню браузера, затем откройте снова.");
                var launches = system.Capture(browser, windows);
                if (launches == null || launches.Length == 0) throw new IOException("Не удалось сохранить профиль для перезапуска. Окна не закрыты.");
                foreach (var window in windows)
                    if (!window.Exited && !window.CloseWindow()) throw new IOException("Браузер не принял запрос закрытия. Закройте его вручную.");
                bool desktop = browser.Name == "Antigravity";
                if (desktop) DiagnosticReport.Record("Antigravity restart", "graceful close; windows="+windows.Length+"; tracked processes="+processes.Length);
                int waitLimit = desktop ? 300 : 150;
                for (int i = 0; i < waitLimit && processes.Any(p => !p.Exited); i++) system.Wait();
                if (processes.Any(p => !p.Exited))
                    throw new IOException(desktop ? "Antigravity или его фоновый сервер не завершился за 30 секунд. Закройте приложение обычным способом и повторите попытку. Повторный запуск отменён." :
                        "Браузер не завершился. Возможно, он ждёт сохранения данных или остался в фоне. Закройте его вручную; принудительное завершение не выполняется.");
                var remaining = system.Running(browser);
                try
                {
                    for (int i = 0; i < 50 && remaining.Any(p => !p.Exited); i++) system.Wait();
                    if (remaining.Any(p => !p.Exited)) throw new IOException(browser.Name == "Яндекс Браузер" ?
                        "Яндекс остался в фоновом режиме. Нажмите правой кнопкой на его значке возле часов → Закрыть браузер. Затем нажмите «Перезапустить» ещё раз. В настройках Яндекса → Системные можно отключить фоновый режим." :
                        "Браузер снова запущен или ещё работает в фоне. Повторный запуск отменён.");
                }
                finally { foreach (var item in remaining) item.Dispose(); }
                if (desktop)
                {
                    // An exited process can leave IPC/image cleanup in flight. Give it a
                    // short quiet interval, then catch an updater/backend starting again.
                    for (int i = 0; i < 5; i++) system.Wait();
                    var settled = system.Running(browser);
                    try { if (settled.Any(p => !p.Exited)) throw new IOException("Фоновые процессы Antigravity запустились снова. Повторный запуск отменён; дождитесь завершения обновления приложения."); }
                    finally { foreach (var item in settled) item.Dispose(); }
                    DiagnosticReport.Record("Antigravity restart", "all tracked processes exited; requesting fresh Desktop launch");
                }
                system.Open(browser, launches);
            }
            finally { foreach (var item in processes) item.Dispose(); }
        }
    }
    static class AntigravityRestart
    {
        internal static bool WaitForType(string type)
        { return !string.Equals(type, "crashpad-handler", StringComparison.OrdinalIgnoreCase); }
        internal static void PrepareEnvironment(ProcessStartInfo start)
        {
            // A GUI launch must not inherit Electron's helper/Node execution mode.
            // Only this child's environment is changed; user network settings remain intact.
            start.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
            start.EnvironmentVariables.Remove("ELECTRON_NO_ASAR");
        }
        internal static bool Ready(bool enable, bool readOnly, string error, AgStatus status, int provider,bool restartAllowed=true)
        { return restartAllowed && enable && !readOnly && error == null && status.Error == null && status.Checked && status.Complete && status.Provider == provider; }
        internal static BrowserLaunch[] Capture(string[] commands, Func<string, bool> exists)
        {
            return commands.Select(command => {
                var args = BrowserProfiles.Split(command);
                if (BrowserProfiles.Option(args, "--type") != null) throw new IOException("Не удалось определить главное окно Antigravity.");
                string profile = BrowserProfiles.Option(args, "--user-data-dir");
                string extensions = BrowserProfiles.Option(args, "--extensions-dir");
                string result = "";
                foreach (var option in new[] { new[] {"--user-data-dir", profile}, new[] {"--extensions-dir", extensions} })
                {
                    if (option[1] == null) continue;
                    if (!Path.IsPathRooted(option[1]) || option[1].StartsWith(@"\\", StringComparison.Ordinal) || !exists(option[1]))
                        throw new IOException("Не удалось сохранить каталог настроек Antigravity. Окна не закрыты.");
                    result += option[0] + "=" + BrowserProfiles.Quote(Path.GetFullPath(option[1])) + " ";
                }
                if (args.Skip(1).TakeWhile(arg => arg != "--").Any(arg => arg.Equals("--disable-gpu", StringComparison.OrdinalIgnoreCase)))
                    result += "--disable-gpu ";
                return result.TrimEnd();
            }).Distinct(StringComparer.OrdinalIgnoreCase).Select(arguments => new BrowserLaunch { Arguments = arguments }).ToArray();
        }
        internal static bool IsRunning()
        {
            try
            {
                int session;
                using (var current = Process.GetCurrentProcess()) session = current.SessionId;
                foreach (var process in Process.GetProcessesByName("Antigravity"))
                    using (process)
                        if (!process.HasExited && process.SessionId == session && process.MainWindowHandle != IntPtr.Zero)
                            return true;
            }
            catch { }
            return false;
        }
        internal static void Run()
        {
            BrowserChoice choice = null;
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            foreach (var process in Process.GetProcessesByName("Antigravity"))
                using (process)
                {
                    if (process.HasExited || process.SessionId != session || process.MainWindowHandle == IntPtr.Zero) continue;
                    string path = ProcessSecurity.ImagePath(process.Id);
                    if (choice != null && !string.Equals(choice.Exe, path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Открыты разные версии Antigravity. Перезапустите их вручную.");
                    choice = new BrowserChoice {Name="Antigravity", Exe=path};
                }
            if (choice == null) return; // Never start an application the user was not running.
            if (DnsElevation.IsAdministrator()) throw new IOException("Для автоматического перезапуска Antigravity запустите Smart DNS обычным способом, без прав администратора.");
            try { BrowserRestart.Run(choice, new WindowsBrowserSystem(), true); }
            catch (Exception error) {
                DiagnosticReport.Record("Antigravity restart", "failed: "+error.Message);
                throw new IOException("Antigravity не перезапущен.\n\n"+error.Message, error);
            }
        }
    }
}
