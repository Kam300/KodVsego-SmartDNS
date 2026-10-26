using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace HbAi.XboxDns
{
    sealed class BrowserLaunch { internal string Arguments; }
    static class BrowserProfiles
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr LocalFree(IntPtr memory);
        internal static string[] Split(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) throw new IOException("Не удалось прочитать параметры работающего браузера. Перезапуск отменён до закрытия окон.");
            int count;
            IntPtr memory = CommandLineToArgvW(command, out count);
            if (memory == IntPtr.Zero) throw new IOException("Не удалось разобрать параметры браузера.");
            try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size))).ToArray(); }
            finally { LocalFree(memory); }
        }
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
        internal static string Option(string[] args, params string[] names)
        {
            string result = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--") break;
                int equal = args[i].IndexOf('=');
                string key = equal < 0 ? args[i] : args[i].Substring(0, equal);
                if (!names.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase))) continue;
                string value;
                if (equal >= 0) value = args[i].Substring(equal + 1);
                else
                {
                    if (i + 1 == args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal)) throw new IOException("Пустой параметр профиля браузера.");
                    value = args[++i];
                }
                if (string.IsNullOrWhiteSpace(value)) throw new IOException("Пустой параметр профиля браузера.");
                if (result != null && !string.Equals(result, value, StringComparison.OrdinalIgnoreCase)) throw new IOException("Неоднозначные параметры профиля браузера.");
                result = value;
            }
            return result;
        }
        static string LocalDirectory(string value, Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(value) || !Path.IsPathRooted(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Не удалось определить локальный каталог профиля. Браузер не закрыт.");
            string path = Path.GetFullPath(value);
            if (!exists(path)) throw new IOException("Каталог профиля не найден. Браузер не закрыт.");
            return path;
        }
        static string DefaultRoot(BrowserChoice browser)
        {
            string relative;
            switch (browser.Name)
            {
                case "Google Chrome": relative = @"Google\Chrome\User Data"; break;
                case "Microsoft Edge": relative = @"Microsoft\Edge\User Data"; break;
                case "Яндекс Браузер": relative = @"Yandex\YandexBrowser\User Data"; break;
                case "Brave": relative = @"BraveSoftware\Brave-Browser\User Data"; break;
                case "Vivaldi": relative = @"Vivaldi\User Data"; break;
                default: throw new IOException("Не удалось определить каталог этого браузера. Перезапустите его вручную.");
            }
            // Nonstandard channels/portable builds must supply their data path explicitly.
            if (browser.Exe.IndexOf("Beta", StringComparison.OrdinalIgnoreCase) >= 0 || browser.Exe.IndexOf("Dev\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                browser.Exe.IndexOf("Canary", StringComparison.OrdinalIgnoreCase) >= 0 || browser.Exe.IndexOf("SxS", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new IOException("У этой версии браузера нестандартный каталог профилей. Перезапустите её вручную.");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), relative);
        }
        internal static string[] ActiveChromiumProfiles(string json, string explicitProfile)
        {
            var document = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
            object value;
            var profile = document != null && document.TryGetValue("profile", out value) ? value as Dictionary<string, object> : null;
            var names = new List<string>();
            if (profile != null && profile.TryGetValue("last_active_profiles", out value))
            {
                var array = value as IEnumerable;
                if (array == null || value is string) throw new IOException("Неверный список активных профилей браузера.");
                foreach (var item in array)
                {
                    string name = item as string;
                    if (name == null) throw new IOException("Неверное имя активного профиля.");
                    names.Add(name);
                }
            }
            if (names.Count == 0 && !string.IsNullOrEmpty(explicitProfile)) names.Add(explicitProfile);
            if (names.Count == 0 && profile != null && profile.TryGetValue("last_used", out value) && value is string) names.Add((string)value);
            if (names.Count == 0 || names.Any(n => string.IsNullOrWhiteSpace(n) || n == "." || n == ".." || n.IndexOfAny(new[] { '\\', '/', ':', '"', '\r', '\n' }) >= 0))
                throw new IOException("Не удалось однозначно определить профиль браузера. Окна не закрыты.");
            if (names.Any(n => n == "Guest Profile" || n == "System Profile")) throw new IOException("Гостевой или системный профиль автоматически не перезапускается.");
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        internal static BrowserLaunch[] Capture(BrowserChoice browser, string[] commands,
            Func<string, string> read, Func<string, bool> exists, Func<string, bool> locked)
        {
            var launches = new List<BrowserLaunch>();
            foreach (string command in commands)
            {
                var args = Split(command);
                if (args.Any(a => new[] { "--incognito", "--inprivate", "--guest", "-private", "--private-window", "-private-window" }
                    .Any(flag => a.Equals(flag, StringComparison.OrdinalIgnoreCase) || a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))))
                    throw new IOException("Приватные окна не могут быть восстановлены автоматически. Перезапустите браузер вручную.");
                if (browser.Name == "Mozilla Firefox")
                {
                    string path = Option(args, "-profile", "--profile");
                    if (path != null) launches.Add(new BrowserLaunch { Arguments = "-profile " + Quote(LocalDirectory(path, exists)) });
                    else
                    {
                        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox");
                        string requested = Option(args, "-P");
                        var profiles = FirefoxPaths(read(Path.Combine(root, "profiles.ini")), root, requested);
                        var active = requested == null ? profiles.Where(p => locked(Path.Combine(p, "parent.lock"))).ToArray() : profiles;
                        if (active.Length == 0) throw new IOException("Не удалось определить активный профиль Firefox. Браузер не закрыт.");
                        foreach (string profile in active) launches.Add(new BrowserLaunch { Arguments = "-profile " + Quote(LocalDirectory(profile, exists)) });
                    }
                }
                else
                {
                    string supplied = Option(args, "--user-data-dir");
                    if (browser.Name == "Opera" && supplied == null) throw new IOException("Для Opera без явного каталога профиля используйте ручной перезапуск. Браузер не закрыт.");
                    string root = LocalDirectory(supplied ?? DefaultRoot(browser), exists);
                    string json = read(Path.Combine(root, "Local State"));
                    foreach (string profile in ActiveChromiumProfiles(json, Option(args, "--profile-directory")))
                    {
                        LocalDirectory(Path.Combine(root, profile), exists);
                        launches.Add(new BrowserLaunch { Arguments = "--user-data-dir=" + Quote(root) + " --profile-directory=" + Quote(profile) + " --restore-last-session" });
                    }
                }
            }
            var distinct = launches.GroupBy(l => l.Arguments, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            if (browser.Name == "Mozilla Firefox" && distinct.Length > 1)
                foreach (var launch in distinct) launch.Arguments = "-no-remote " + launch.Arguments;
            return distinct;
        }
        internal static string[] FirefoxPaths(string ini, string root, string requested)
        {
            var sections = new List<Dictionary<string, string>>(); Dictionary<string, string> current = null;
            foreach (string row in ini.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = row.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    current = line.StartsWith("[Profile", StringComparison.Ordinal) ? new Dictionary<string, string>() : null;
                    if (current != null) sections.Add(current);
                }
                else if (current != null && !line.StartsWith(";", StringComparison.Ordinal))
                { int equal = line.IndexOf('='); if (equal > 0) current[line.Substring(0, equal)] = line.Substring(equal + 1); }
            }
            return sections.Where(s => s.ContainsKey("Path") && (requested == null || (s.ContainsKey("Name") && s["Name"] == requested)))
                .Select(s => s.ContainsKey("IsRelative") && s["IsRelative"] == "1" ? Path.GetFullPath(Path.Combine(root, s["Path"].Replace('/', '\\'))) : s["Path"])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
