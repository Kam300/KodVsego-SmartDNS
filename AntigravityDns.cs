using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

namespace HbAi.XboxDns
{
    sealed class AgRule
    {
        internal string Id, Comment, DisplayName;
        internal string[] Names, Servers;
        internal bool Local;
        internal int? Version, ConfigOptions;
    }
    interface IAgRules
    {
        AgRule[] Read();
        AgRule[] Effective();
        void Add(string name);
        void Remove(string id);
        void SetServers(AgRule expected, string[] servers);
        void Flush();
    }
    sealed class WindowsAgRules : IAgRules
    {
        internal static void CheckResult(string method, ManagementBaseObject result)
        {
            bool write = method == "Add" || method == "Set" || method == "Remove";
            // Mutation providers may finish successfully without returning an object.
            // Each mutation below therefore verifies the actual persisted state.
            if (result == null)
            {
                if (write) return;
                throw new IOException("Windows не вернула результат чтения NRPT.");
            }
            var code = result.Properties["ReturnValue"];
            if (code == null || code.Value == null) throw new IOException("Windows не вернула код результата NRPT.");
            if (Convert.ToUInt32(code.Value) != 0)
                throw new IOException("Ошибка NRPT Windows: " + Convert.ToString(code.Value));
        }
        // Invoke the Windows DNS CIM provider directly, without a shell or downloaded code.
        static ManagementBaseObject Invoke(string className, string method, Action<ManagementBaseObject> configure)
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\DNS");
            scope.Options.Timeout = TimeSpan.FromSeconds(15);
            using (var type = new ManagementClass(scope, new ManagementPath(className), null))
            using (var args = type.GetMethodParameters(method))
            {
                configure(args);
                if (method == "Add" || method == "Set" || method == "Remove") args["PassThru"] = true;
                var result = type.InvokeMethod(method, args, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(15) });
                try { CheckResult(method, result); }
                catch
                {
                    if (result != null) result.Dispose();
                    throw;
                }
                return result;
            }
        }
        internal static string[] Strings(object value)
        {
            if (value == null) return new string[0];
            var array = value as string[];
            if (array != null) return array;
            var text = value as string;
            if (text != null) return text.Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            throw new InvalidDataException("Неизвестный формат DNS-политики Windows.");
        }
        internal static AgRule[] ReadWithFallback(Func<AgRule[]> primary, Func<AgRule[]> secondary)
        {
            try { return primary(); }
            catch (Exception first)
            {
                if (!(first is IOException || first is ManagementException || first is InvalidOperationException)) throw;
                try { return secondary(); }
                catch (Exception second) { throw new IOException("Не удалось прочитать правила DNS Windows обоими способами. WMI: " + first.Message + " CIM: " + second.Message, second); }
            }
        }
        static AgRule[] Read(bool effective)
        {
            // Stored rule identity is needed for ownership and restoration. Read the
            // documented store directly, even when the cmdlet provider is unavailable.
            if (!effective) return NrptRegistryReader.Read(false);
            return ReadWithFallback(() => ReadWithFallback(() => ReadWmi(true), () => NrptCimReader.Read(true)), () => NrptRegistryReader.Read(true));
        }
        static AgRule[] ReadWmi(bool effective)
        {
            using (var result = Invoke(effective ? "PS_DnsClientNrptPolicy" : "PS_DnsClientNrptRule", "Get",
                p => { if (effective) p["Effective"] = true; }))
            {
                var rows = result["cmdletOutput"] as ManagementBaseObject[];
                if (rows == null) return new AgRule[0];
                return rows.Select(r => new AgRule {
                    Id = effective ? null : Convert.ToString(r["Name"]),
                    Comment = effective ? null : Convert.ToString(r["Comment"]),
                    DisplayName = effective ? null : Convert.ToString(r["DisplayName"]),
                    Names = Strings(r["Namespace"]),
                    Servers = Strings(r["NameServers"])
                }).ToArray();
            }
        }
        public AgRule[] Read() { return Read(false); }
        public AgRule[] Effective() { return Read(true); }
        public void Add(string name)
        {
            if (!AntigravityDns.Domains.Contains(name)) throw new ArgumentException("Неизвестный домен.");
            using (Invoke("PS_DnsClientNrptRule", "Add", p => {
                p["Namespace"] = new[] { name }; p["NameServers"] = AntigravityDns.Servers;
                p["Comment"] = AntigravityDns.Tag; p["DisplayName"] = AntigravityDns.Title;
            })) { }
            if (Read().Count(r => AntigravityDns.Owned(r) && r.Names[0] == name && r.Servers.SequenceEqual(AntigravityDns.Servers)) != 1)
                throw new IOException("Windows не подтвердила создание DNS-правила Antigravity: " + name);
        }
        public void Remove(string id)
        {
            // Recheck ownership immediately before removal, including the namespace.
            var rule = Read().SingleOrDefault(r => r.Id == id);
            if (rule == null) return;
            if (!AntigravityDns.Owned(rule)) throw new IOException("Правило NRPT изменено другой программой; удаление отменено.");
            using (Invoke("PS_DnsClientNrptRule", "Remove", p => { p["Name"] = id; p["Force"] = true; })) { }
            if (Read().Any(r => r.Id == id)) throw new IOException("Windows не подтвердила удаление DNS-правила Antigravity.");
        }
        public void Flush() { new NativeDnsSystem().Flush(); }
        internal void RemoveLegacy(AgRule expected)
        {
            if (!AgCleanup.IsLegacy(expected)) throw new IOException("Удаление доступно только для старых правил AG Unlocker.");
            var current = Read().SingleOrDefault(r => r.Id == expected.Id);
            if (current == null) return;
            if (!AgCleanup.Same(current, expected)) throw new IOException("Старое правило изменилось. Удаление отменено.");
            using (Invoke("PS_DnsClientNrptRule", "Remove", p => { p["Name"] = expected.Id; p["Force"] = true; })) { }
            if (Read().Any(r => r.Id == expected.Id)) throw new IOException("Windows не подтвердила удаление старого правила.");
        }
        public void SetServers(AgRule expected, string[] servers)
        {
            var current = Read().SingleOrDefault(r => r.Id == expected.Id);
            if (current == null || current.Comment != expected.Comment || current.DisplayName != expected.DisplayName ||
                !current.Names.SequenceEqual(expected.Names) || !current.Servers.SequenceEqual(expected.Servers))
                throw new IOException("Правило Antigravity изменилось. Замена отменена.");
            using (Invoke("PS_DnsClientNrptRule", "Set", p => { p["Name"] = expected.Id; p["NameServers"] = servers; })) { }
            var actual = Read().SingleOrDefault(r => r.Id == expected.Id);
            if (actual == null || !actual.Servers.SequenceEqual(servers)) throw new IOException("Windows не подтвердила замену DNS Antigravity.");
        }
    }
    sealed class AgStatus
    {
        internal bool Checked, Complete, External, RecoveryNeeded, WindowsUpdateException;
        internal int Provider = -1;
        internal string Error;
    }
    static class AntigravityDns
    {
        internal const string Tag = "KodVsego.SmartDNS.Antigravity.v1";
        internal const string Title = "Код Всего · Antigravity DNS";
        internal static readonly string[] Domains = { "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" };
        // Append only: saved rules and elevated operation IDs use this historical order.
        static readonly int[] ExtraSets = ProviderCatalog.All.Where(p=>p.id>=7&&!p.encryptedOnly&&p.autoGoals.Contains("Antigravity")).Select(p=>p.id).ToArray();
        static readonly int[] MainSets = new[]{1,0,2,4,6}.Concat(ExtraSets).ToArray();
        internal static readonly string[] ProviderNames = new[]{"GeoHide","Comss.one","GeoHide EU","Bezmezhau","MagicDNS"}.Concat(ExtraSets.Select(p=>Dns.Name[p])).ToArray();
        internal static readonly string[][] ServerSets = new[]{
            new[] { "159.194.200.33", "193.233.112.67" },
            new[] { "83.220.169.155", "212.109.195.93" },
            new[] { "143.20.64.192", "45.13.237.213" },
            new[] { "143.20.64.55", "31.58.216.201" },
            ProviderCatalog.All[6].ipv4 }.Concat(ExtraSets.Select(p=>Dns.V4[p])).ToArray();
        [ThreadStatic] static int? selectedProvider;
        internal static string[] Servers { get { return ServerSets[selectedProvider ?? 0]; } }
        sealed class ProviderScope : IDisposable
        {
            readonly int? previous;
            internal ProviderScope(int provider) { previous = selectedProvider; selectedProvider = provider; }
            public void Dispose() { selectedProvider = previous; }
        }
        internal static IDisposable UseProvider(int provider)
        {
            if (provider < 0 || provider >= ServerSets.Length) throw new ArgumentOutOfRangeException("provider");
            return new ProviderScope(provider);
        }
        // Retain the historical Comss slot for identifying/restoring existing rules,
        // but do not offer new activations: coverage of both API hosts is unconfirmed.
        internal static int ProviderForMainSet(int set)
        { return Dns.IsAvailable(set) && set != 0 ? Array.IndexOf(MainSets, set) : -1; }
        static int DetectProvider(AgRule[] rules)
        {
            for (int provider = 0; provider < ServerSets.Length; provider++)
                if (Domains.All(d => rules.Count(r => r.Names.Length == 1 && r.Names[0] == d && r.Servers.SequenceEqual(ServerSets[provider])) == 1)) return provider;
            return -1;
        }
        internal static bool Owned(AgRule rule)
        {
            Guid id;
            return rule.Comment == Tag && rule.DisplayName == Title && Guid.TryParse(rule.Id, out id) &&
                rule.Names != null && rule.Names.Length == 1 && Domains.Contains(rule.Names[0]);
        }
        internal static bool Matches(string pattern, string domain)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            pattern = pattern.Trim().ToLowerInvariant();
            domain = domain.ToLowerInvariant();
            if (pattern == "." || pattern == "*") return true;
            if (pattern.StartsWith("*.", StringComparison.Ordinal)) pattern = pattern.Substring(1);
            if (pattern.StartsWith(".", StringComparison.Ordinal))
                return domain.EndsWith(pattern, StringComparison.Ordinal) || domain == pattern.Substring(1);
            if (pattern.EndsWith("*", StringComparison.Ordinal)) return domain.StartsWith(pattern.TrimEnd('*'), StringComparison.Ordinal);
            return domain == pattern.TrimEnd('.');
        }
        static bool Relevant(AgRule rule) { return rule.Names.Any(n => Domains.Any(d => Matches(n, d))); }
        internal static AgStatus Status(IAgRules system)
        {
            var all = system.Read();
            var ours = all.Where(Owned).ToArray();
            int provider = DetectProvider(ours);
            return new AgStatus { Checked = ours.Length > 0,
                Complete = provider >= 0, Provider = provider,
                WindowsUpdateException = all.Any(EncryptedDns.IsWindowsUpdateException),
                External = all.Any(r => !Owned(r) && Relevant(r)) };
        }
        internal static AgStatus GetStatus()
        {
            try
            {
                var system = new WindowsAgRules();
                var status = Status(system);
                var saved = new RegistryAgTakeoverStore().Load();
                if (saved.Length > 0)
                {
                    var all = system.Read();
                    status.Checked = true;
                    status.Provider = DetectProvider(all.Where(r => Owned(r) || saved.Any(s => s.Matches(r))).ToArray());
                    status.Complete = status.Provider >= 0 && saved.All(s => all.Any(r => s.Matches(r) && r.Servers.SequenceEqual(s.AppliedServers ?? ServerSets[0])));
                    status.External = all.Any(r => !Owned(r) && !saved.Any(s => s.Matches(r)) && Relevant(r));
                    status.RecoveryNeeded = !status.Complete;
                }
                return status;
            }
            catch (Exception e) { return new AgStatus { Error = e.Message }; }
        }
        internal static void Change(IAgRules system, bool enable)
        {
            var before = system.Read();
            var ours = before.Where(Owned).ToArray();
            if (!enable)
            {
                foreach (var rule in ours) system.Remove(rule.Id);
                if (system.Read().Any(Owned)) throw new IOException("Не все правила Antigravity удалены. Повторите отключение.");
                system.Flush(); return;
            }
            if (before.Any(r => !Owned(r) && Relevant(r)))
                throw new IOException("Для доменов Antigravity уже есть DNS-правила другой программы. Отключите их в этой программе перед включением нашей опции. Существующие правила не изменены.");
            if (system.Effective().Any(r => Relevant(r) && !ours.Any(o => o.Names.SequenceEqual(r.Names) && o.Servers.SequenceEqual(r.Servers))))
                throw new IOException("Для Antigravity действует другая DNS-политика Windows. Изменение отменено; проверьте настройки VPN или политики организации.");
            if (ours.Any(r => !r.Servers.SequenceEqual(Servers)) || Domains.Any(d => ours.Count(r => r.Names[0] == d) > 1))
                throw new IOException("Наши правила Antigravity изменены или дублируются. Сначала снимите галочку, затем включите заново.");
            try
            {
                foreach (string name in Domains)
                    if (!ours.Any(r => r.Names[0] == name)) system.Add(name);
                var status = Status(system);
                if (!status.Complete || status.External) throw new IOException("Windows не подтвердила оба DNS-правила Antigravity или появился конфликт.");
                var effective = system.Effective();
                if (effective.Any(r => Relevant(r) && !Domains.Any(d => r.Names.Length == 1 && r.Names[0] == d && r.Servers.SequenceEqual(Servers))) ||
                    Domains.Any(d => !effective.Any(r => r.Names.Length == 1 && r.Names[0] == d && r.Servers.SequenceEqual(Servers))))
                    throw new IOException("Правила созданы, но Windows применяет другую DNS-политику. Включение отменено.");
                system.Flush();
            }
            catch (Exception applyError)
            {
                try
                {
                    foreach (var rule in system.Read().Where(Owned).Where(r => !ours.Any(o => o.Id == r.Id))) system.Remove(rule.Id);
                    system.Flush();
                }
                catch (Exception rollbackError) { throw new IOException(applyError.Message + "; откат не завершён: " + rollbackError.Message); }
                throw new IOException(applyError.Message + "; новые правила отменены.");
            }
        }
        internal static string Run(bool enable, bool force = false, int provider = 0)
        {
            if(enable&&provider>=0&&provider<MainSets.Length&&!Dns.IsAvailable(MainSets[provider]))return "Провайдер временно недоступен. Выберите другой DNS для Antigravity.";
            if (enable && provider == 1) return "Comss пока отключён для доп. DNS Antigravity: работа обоих API-доменов не подтверждена. Выберите другой DNS. Общий DNS Comss остаётся доступен.";
            using (UseProvider(provider))
            using (var mutex = new Mutex(false, @"Global\KodVsego.SmartDns.Antigravity"))
            {
                bool locked = false;
                try
                {
                    try { locked = mutex.WaitOne(30000); } catch (AbandonedMutexException) { locked = true; }
                    if (!locked) return "Другая настройка Antigravity ещё выполняется.";
                    var system = new WindowsAgRules();
                    var store = new RegistryAgTakeoverStore();
                    if (!enable) { AgTakeover.Restore(system, store); Change(system, false); }
                    else if (force) AgTakeover.Enable(system, store);
                    else
                    {
                        if (store.Load().Length > 0) return "Есть копия заменённых правил Antigravity. Сначала снимите галочку для восстановления.";
                        Change(system, true);
                    }
                    return null;
                }
                catch (Exception e) { return e.Message; }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }
    }
}
