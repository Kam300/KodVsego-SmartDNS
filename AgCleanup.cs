using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace HbAi.XboxDns
{
    interface IAgCleanupSystem
    {
        AgRule[] Read();
        AgRule[] Organization();
        void Remove(AgRule rule);
        void Flush();
    }
    interface IAgCleanupBackup { string Save(AgRule[] rules); }
    sealed class WindowsAgCleanupSystem : IAgCleanupSystem
    {
        readonly WindowsAgRules rules = new WindowsAgRules();
        public AgRule[] Read() { return rules.Read(); }
        public AgRule[] Organization() { return NrptRegistryReader.ReadOrganization(); }
        public void Remove(AgRule rule) { rules.RemoveLegacy(rule); }
        public void Flush() { rules.Flush(); }
    }
    sealed class AgCleanupBackup : IAgCleanupBackup
    {
        internal static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"KodVsego\SmartDNS\RemovedAntigravityRules"); } }
        readonly string outputRoot;
        internal AgCleanupBackup(string directory = null) { outputRoot = directory ?? Root; }
        public string Save(AgRule[] rules)
        {
            if (rules.Length == 0 || rules.Length > 2 || rules.Any(r => !AgCleanup.IsLegacy(r)))
                throw new IOException("Неверный набор правил для резервной копии.");
            // A new immutable export per attempt. Never overwrite an earlier recovery copy.
            string directory = Path.Combine(outputRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Каталог копии перенаправлен. Удаление отменено.");
            foreach (var rule in rules)
            {
                string id = new Guid(rule.Id).ToString("B");
                string file = Path.Combine(directory, id + ".reg");
                string key = @"HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig\" + id;
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
                    "export \"" + key + "\" \"" + file + "\" /reg:64") {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
                };
                using (var process = Process.Start(start))
                {
                    if (process == null || !process.WaitForExit(15000)) throw new IOException("Не удалось завершить сохранение правил. Удаление отменено.");
                    if (process.ExitCode != 0) throw new IOException("Windows не сохранила копию правил. Удаление отменено (код " + process.ExitCode + ").");
                }
                string exported = File.Exists(file) ? File.ReadAllText(file) : "";
                if (!exported.StartsWith("Windows Registry Editor Version 5.00", StringComparison.Ordinal) ||
                    exported.IndexOf("[HKEY_LOCAL_MACHINE\\" + key.Substring(5) + "]", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new IOException("Копия правила не подтверждена. Удаление отменено.");
                using (var saved = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) saved.Flush(true);
            }
            File.WriteAllText(Path.Combine(directory, "Восстановление.txt"),
                "Здесь сохранены полные старые правила Antigravity до удаления.\r\n" +
                "Для ручного восстановления откройте файлы .reg и подтвердите импорт Windows. Это вернёт прежние DNS для Antigravity.\r\n" +
                "После импорта перезапустите компьютер. Не импортируйте копию поверх новых правил другой программы.\r\n" +
                "Автоматическое восстановление при выключении DNS не выполняется.\r\n",
                System.Text.Encoding.UTF8);
            return directory;
        }
    }
    static class AgCleanup
    {
        internal static bool IsLegacy(AgRule rule)
        {
            Guid id;
            return rule != null && Guid.TryParse(rule.Id, out id) && AgTakeover.KnownTag(rule.Comment) &&
                rule.Names != null && rule.Names.Length == 1 && AntigravityDns.Domains.Contains(rule.Names[0]);
        }
        internal static bool Same(AgRule a, AgRule b)
        { return a.Id == b.Id && a.Comment == b.Comment && a.DisplayName == b.DisplayName && a.Names.SequenceEqual(b.Names) && a.Servers.SequenceEqual(b.Servers); }
        static bool Relevant(AgRule r)
        { return r.Names.Any(n => AntigravityDns.Domains.Any(d => AntigravityDns.Matches(n, d))); }
        internal static string Change(IAgCleanupSystem system, IAgCleanupBackup store)
        {
            var before = system.Read();
            var selected = before.Where(IsLegacy).ToArray();
            if (selected.Length == 0)
            {
                if (before.Any(r => Relevant(r) && !AntigravityDns.Owned(r)))
                    throw new IOException("Эта кнопка удаляет только точные старые правила AG Unlocker. Неизвестные правила не изменены.");
                return null; // Safe retry after a completed removal.
            }
            if (selected.Length > 2 || selected.Any(r => before.Count(b => b.Names.Any(n => AntigravityDns.Matches(n, r.Names[0]))) != 1))
                throw new IOException("Есть дублирующиеся или общие правила Antigravity. Автоматическое удаление отменено.");
            if (system.Organization().Any(Relevant)) throw new IOException("Для Antigravity действует политика организации. Она не удаляется этой кнопкой.");
            string backup = store.Save(selected); // Full durable export before the first deletion.
            if (string.IsNullOrWhiteSpace(backup)) throw new IOException("Копия правил не подтверждена. Удаление отменено.");
            try
            {
                foreach (var expected in selected)
                {
                    var current = system.Read().SingleOrDefault(r => r.Id == expected.Id);
                    if (current == null) continue;
                    if (!Same(current, expected)) throw new IOException("Старые правила изменились во время операции. Повторите проверку.");
                    if (system.Organization().Any(Relevant)) throw new IOException("Во время операции появилась политика организации. Удаление остановлено.");
                    system.Remove(expected);
                }
                if (system.Read().Any(IsLegacy)) throw new IOException("Старый анлокер вернул свои правила. Отключите в нём DNS-настройку и повторите удаление.");
                system.Flush();
                return backup;
            }
            catch (Exception error) { throw new IOException(error.Message + "\n\nКопия правил сохранена: " + backup, error); }
        }
        internal static string Run()
        {
            using (var mutex = new Mutex(false, @"Global\KodVsego.SmartDns.Antigravity"))
            {
                bool locked = false;
                try
                {
                    try { locked = mutex.WaitOne(30000); } catch (AbandonedMutexException) { locked = true; }
                    if (!locked) return "Другая настройка Antigravity ещё выполняется.";
                    if (new RegistryAgTakeoverStore().Load().Length > 0)
                        return "Сначала снимите галочку Antigravity, чтобы завершить восстановление ранее заменённых правил.";
                    Change(new WindowsAgCleanupSystem(), new AgCleanupBackup());
                    return null;
                }
                catch (Exception error) { return error.Message; }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }
    }
}
