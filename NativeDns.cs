using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    // Schema deliberately matches backups made by the earlier PowerShell build.
    public sealed class DnsBackup
    {
        public int Version { get; set; }
        public AdapterBackup[] Adapters { get; set; }
    }
    public sealed class AdapterBackup
    {
        public string Guid { get; set; }
        public FamilyBackup[] Families { get; set; }
        public bool? Ipv6Binding { get; set; }
    }
    public sealed class FamilyBackup
    {
        public string Family { get; set; }
        public bool Manual { get; set; }
        public string[] Servers { get; set; }
        public bool DohCaptured { get; set; }
        public DohConfig[] Doh { get; set; }
        public bool ProfilePresent { get; set; }
    }
    sealed class DnsAdapter
    {
        public Guid Id;
        public string Name;
        public bool V4, V6;
    }
    interface IDnsSystem
    {
        void EnsureSupported();
        DnsAdapter[] Adapters(bool activeOnly);
        FamilyBackup Read(Guid id, string family);
        void Set(Guid id, FamilyBackup settings);
        void Flush();
    }
    interface IDnsBackupStore
    {
        List<AdapterBackup> Load();
        void Save(List<AdapterBackup> records);
    }
    sealed class RegistryDnsBackupStore : IDnsBackupStore
    {
        internal const string Key = @"SOFTWARE\HbAi\SmartDns";
        public List<AdapterBackup> Load()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(Key))
            {
                if (key == null || key.GetValue("Backup") == null) return new List<AdapterBackup>();
                var raw = key.GetValue("Backup") as string;
                if (raw == null) throw new InvalidDataException("Неверный формат резервной копии DNS.");
                var state = new JavaScriptSerializer().Deserialize<DnsBackup>(raw);
                if (state == null || (state.Version != 1 && state.Version != 2) || state.Adapters == null)
                    throw new InvalidDataException("Неподдерживаемая резервная копия DNS.");
                var records = state.Adapters.ToList();
                Validate(records);
                return records;
            }
        }
        internal static void Validate(List<AdapterBackup> records)
        {
            var seen = new HashSet<Guid>();
            foreach (var record in records)
            {
                Guid id;
                if (record == null || !Guid.TryParse(record.Guid, out id) || !seen.Add(id) ||
                    record.Families == null || record.Families.Length == 0)
                    throw new InvalidDataException("Повреждена резервная копия адаптера DNS.");
                var families = new HashSet<string>();
                foreach (var family in record.Families)
                {
                    if (family == null || !families.Add(family.Family))
                        throw new InvalidDataException("Повреждена резервная копия семейства DNS.");
                    NativeDnsSystem.ValidateFamily(family);
                }
            }
        }
        public void Save(List<AdapterBackup> records)
        {
            Validate(records);
            if (records.Count == 0)
            {
                using (var key = Registry.LocalMachine.OpenSubKey(Key, true))
                    if (key != null) key.DeleteValue("Backup", false);
                return;
            }
            string json = new JavaScriptSerializer().Serialize(new DnsBackup { Version = SnapshotVersion(records), Adapters = records.ToArray() });
            using (var key = Registry.LocalMachine.CreateSubKey(Key))
            {
                key.SetValue("Backup", json, RegistryValueKind.String);
                key.Flush();
            }
        }
        internal static int SnapshotVersion(List<AdapterBackup> records)
        { return records.Any(r => r.Ipv6Binding.HasValue || r.Families.Any(f => f.Doh != null && f.Doh.Length > 0)) ? 2 : 1; }
    }
    sealed class NativeDnsSystem : IDnsSystem, IDnsBindingSystem
    {
        public bool ReadIpv6Binding(Guid id) { return EncryptedDns.ReadBinding(id); }
        public void SetIpv6Binding(Guid id, bool enabled) { EncryptedDns.SetBinding(id, enabled); }
        // netioapi.h DNS_INTERFACE_SETTINGS, version 1. Default packing is 8.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Settings
        {
            public uint Version;
            public ulong Flags;
            public IntPtr Domain, NameServer, SearchList;
            public uint RegistrationEnabled, RegisterAdapterName, EnableLLMNR, QueryAdapterName;
            public IntPtr ProfileNameServer;
        }
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint SetInterfaceDnsSettings(Guid id, ref Settings settings);
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetInterfaceDnsSettings(Guid id, ref Settings settings);
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void FreeInterfaceDnsSettings(ref Settings settings);
        [DllImport("dnsapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DnsFlushResolverCache();
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OsVersion
        {
            public uint Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int RtlGetVersion(ref OsVersion version);
        public void EnsureSupported()
        {
            var version = new OsVersion { Size = (uint)Marshal.SizeOf(typeof(OsVersion)) };
            if (RtlGetVersion(ref version) != 0 || version.Major < 10 || version.Build < 19041)
                throw new NotSupportedException("Нужна Windows 10 версии 2004 или новее, либо Windows 11. DNS не изменён.");
        }
        public DnsAdapter[] Adapters(bool activeOnly)
        {
            var result = new List<DnsAdapter>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                Guid id;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || !Guid.TryParse(ni.Id, out id) ||
                    (activeOnly && ni.OperationalStatus != OperationalStatus.Up)) continue;
                result.Add(new DnsAdapter { Id = id, Name = ni.Name,
                    V4 = ni.Supports(NetworkInterfaceComponent.IPv4), V6 = ni.Supports(NetworkInterfaceComponent.IPv6) });
            }
            return result.ToArray();
        }
        public FamilyBackup Read(Guid id, string family)
        {
            string service = family == "IPv4" ? "Tcpip" : "Tcpip6";
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service + @"\Parameters\Interfaces\" + id.ToString("B")))
            {
                if (key == null) throw new IOException("Не найдены настройки DNS адаптера " + id);
                object value = key.GetValue("NameServer");
                if (value != null && !(value is string)) throw new InvalidDataException("Неизвестный формат DNS.");
                var servers = ((string)value ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var result = new FamilyBackup { Family = family, Manual = servers.Length > 0, Servers = servers };
                EncryptedDns.Capture(id, result);
                ValidateFamily(result);
                return result;
            }
        }
        internal static void ValidateFamily(FamilyBackup settings)
        {
            if (settings == null || (settings.Family != "IPv4" && settings.Family != "IPv6") || settings.Servers == null ||
                settings.Manual != (settings.Servers.Length > 0)) throw new InvalidDataException("Некорректные настройки DNS.");
            foreach (string server in settings.Servers)
            {
                IPAddress ip;
                if (!IPAddress.TryParse(server, out ip) || ip.AddressFamily !=
                    (settings.Family == "IPv4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6))
                    throw new InvalidDataException("Некорректный адрес DNS: " + server);
            }
            EncryptedDns.Validate(settings);
        }
        public void Set(Guid id, FamilyBackup settings)
        {
            ValidateFamily(settings);
            if (settings.DohCaptured)
            {
                EncryptedDns.Write(id, settings);
                var verified = Read(id, settings.Family);
                if (verified.Manual != settings.Manual || !verified.Servers.SequenceEqual(settings.Servers)) throw new IOException("Windows не подтвердила DNS-адреса.");
                return;
            }
            // An empty static server list restores automatic DNS for this family.
            var native = new Settings { Version = 1, Flags = 2UL | (settings.Family == "IPv6" ? 1UL : 0UL),
                NameServer = Marshal.StringToHGlobalUni(string.Join(",", settings.Servers)) };
            try
            {
                uint error = SetInterfaceDnsSettings(id, ref native);
                if (error != 0) throw new Win32Exception((int)error, "Ошибка изменения DNS (" + id + ", " + settings.Family + "): " + error);
            }
            finally { Marshal.FreeHGlobal(native.NameServer); }
            var actual = Read(id, settings.Family);
            if (actual.Manual != settings.Manual || !actual.Servers.Select(IPAddress.Parse).SequenceEqual(settings.Servers.Select(IPAddress.Parse)))
                throw new IOException("Windows не подтвердила настройки DNS: " + id + ", " + settings.Family);
        }
        public void Flush()
        {
            if (!DnsFlushResolverCache()) throw new IOException("DNS изменён, но очистить кэш не удалось.");
        }
    }
    sealed class DnsSettingsEngine
    {
        readonly IDnsSystem system;
        readonly IDnsBackupStore store;
        internal readonly List<string> DeferredAdapters = new List<string>();
        internal bool RestoreSkipped;
        internal DnsSettingsEngine(IDnsSystem system, IDnsBackupStore store) { this.system = system; this.store = store; }
        internal void Change(bool enable, string[] servers, string dohTemplate = null)
        {
            DeferredAdapters.Clear();
            system.EnsureSupported();
            var records = store.Load();
            RegistryDnsBackupStore.Validate(records);
            if (!enable)
            {
                if (records.Count == 0) throw new InvalidOperationException("Нет сохранённых настроек DNS. Настройки старой версии восстановить автоматически невозможно.");
                Restore(records, true);
            }
            else
            {
                var adapters = system.Adapters(true);
                if (adapters.Length == 0) throw new InvalidOperationException("Нет активных сетевых адаптеров.");
                foreach (var adapter in adapters)
                {
                    if (records.Any(r => new Guid(r.Guid) == adapter.Id)) continue;
                    var families = new List<FamilyBackup>();
                    if (adapter.V4) families.Add(system.Read(adapter.Id, "IPv4"));
                    if (adapter.V6) families.Add(system.Read(adapter.Id, "IPv6"));
                    if (families.Count == 0) throw new IOException("Нет DNS у адаптера " + adapter.Name);
                    records.Add(new AdapterBackup { Guid = adapter.Id.ToString("B"), Families = families.ToArray() });
                }
                var bindings = system as IDnsBindingSystem;
                if (dohTemplate != null)
                {
                    if (bindings == null) throw new NotSupportedException("Системный DoH недоступен.");
                    foreach (var adapter in adapters)
                    {
                        var current = system.Read(adapter.Id, "IPv4");
                        if (!current.DohCaptured || current.ProfilePresent) throw new NotSupportedException("Для DoH нужна Windows 11 без отдельной DNS-настройки профиля сети. Настройки не изменены.");
                        var record = records.Single(r => new Guid(r.Guid) == adapter.Id);
                        foreach (var original in record.Families.Where(f => !f.DohCaptured))
                        {
                            var currentFamily = system.Read(adapter.Id, original.Family);
                            if (!currentFamily.DohCaptured || (currentFamily.Doh.Length > 0 && !currentFamily.Servers.SequenceEqual(original.Servers)))
                                throw new IOException("Старая копия не содержит параметров шифрования. Сначала выключите DNS для восстановления, затем включите DoH.");
                            original.DohCaptured = true; original.Doh = currentFamily.Doh;
                        }
                        if (!record.Ipv6Binding.HasValue) record.Ipv6Binding = bindings.ReadIpv6Binding(adapter.Id);
                    }
                }
                store.Save(records); // Durable backup before the first modification.
                try
                {
                    foreach (var adapter in adapters)
                    {
                        var record = records.Single(r => new Guid(r.Guid) == adapter.Id);
                        if (record.Ipv6Binding.HasValue)
                        {
                            if (bindings == null) throw new IOException("Не удалось восстановить состояние IPv6.");
                            bindings.SetIpv6Binding(adapter.Id, dohTemplate == null ? record.Ipv6Binding.Value : false);
                        }
                        foreach (var family in records.Single(r => new Guid(r.Guid) == adapter.Id).Families)
                        {
                            if (dohTemplate != null && family.Family == "IPv6") continue;
                            var selected = servers.Where(s => IPAddress.Parse(s).AddressFamily ==
                                (family.Family == "IPv4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6)).ToArray();
                            if (selected.Length > 0) system.Set(adapter.Id, new FamilyBackup { Family = family.Family, Manual = true, Servers = selected,
                                DohCaptured = family.DohCaptured || dohTemplate != null, Doh = dohTemplate == null ? new DohConfig[0] : EncryptedDns.Strict(selected.Length, dohTemplate) });
                            else if (family.Family == "IPv6")
                            {
                                // Remove only the app's previous Xbox IPv6 DNS when moving to a v4-only provider.
                                var current = system.Read(adapter.Id, "IPv6");
                                var xbox = new[] { IPAddress.Parse("2a00:ab00:1233:26::50"), IPAddress.Parse("2a00:ab00:1233:26::51") };
                                if (current.Servers.Length > 0 && current.Servers.All(s => xbox.Contains(IPAddress.Parse(s))) &&
                                    !current.Servers.SequenceEqual(family.Servers)) system.Set(adapter.Id, family);
                            }
                        }
                    }
                }
                catch (Exception applyError)
                {
                    try { Restore(records); }
                    catch (Exception restoreError) { throw new IOException(applyError.Message + "; восстановление не завершено: " + restoreError.Message); }
                    throw new IOException(applyError.Message + "; прежние настройки восстановлены.");
                }
            }
            system.Flush();
        }
        internal void ResetKnown(string[] knownServers)
        {
            system.EnsureSupported();
            var existing = store.Load();
            RegistryDnsBackupStore.Validate(existing);
            // A backup created since the UI check takes priority over automatic mode.
            if (existing.Count > 0) { Restore(existing); system.Flush(); return; }
            var known = new HashSet<IPAddress>(knownServers.Select(IPAddress.Parse));
            var records = new List<AdapterBackup>();
            foreach (var adapter in system.Adapters(true))
            {
                var families = new List<FamilyBackup>();
                foreach (string family in new[] { "IPv4", "IPv6" })
                {
                    if (family == "IPv4" ? !adapter.V4 : !adapter.V6) continue;
                    var current = system.Read(adapter.Id, family);
                    if (current.Manual && current.Servers.Length > 0 && current.Servers.All(s => known.Contains(IPAddress.Parse(s))))
                        families.Add(current);
                }
                if (families.Count > 0) records.Add(new AdapterBackup { Guid = adapter.Id.ToString("B"), Families = families.ToArray() });
            }
            if (records.Count == 0)
                throw new InvalidOperationException("Не найдены ручные настройки только с DNS из списка программы. Проверьте DNS в настройках Windows; смешанные и сторонние адреса не изменены.");
            store.Save(records);
            try
            {
                foreach (var record in records)
                    foreach (var family in record.Families)
                        system.Set(new Guid(record.Guid), new FamilyBackup { Family = family.Family, Manual = false, Servers = new string[0] });
            }
            catch (Exception applyError)
            {
                try { Restore(records); }
                catch (Exception restoreError) { throw new IOException(applyError.Message + "; восстановление не завершено: " + restoreError.Message); }
                throw new IOException(applyError.Message + "; прежние настройки восстановлены.");
            }
            store.Save(new List<AdapterBackup>());
            system.Flush();
        }
        internal void RestoreReturned()
        {
            DeferredAdapters.Clear(); RestoreSkipped = false;
            system.EnsureSupported();
            var records = store.Load(); RegistryDnsBackupStore.Validate(records);
            Restore(records, true, true);
            system.Flush();
        }
        internal static bool SafeToRestore(IDnsSystem system, AdapterBackup record)
        {
            foreach (var original in record.Families) {
                var current = system.Read(new Guid(record.Guid), original.Family);
                NativeDnsSystem.ValidateFamily(current);
                if (current.ProfilePresent) return false;
                bool same = current.Manual == original.Manual && current.Servers.SequenceEqual(original.Servers) &&
                    (current.Doh ?? new DohConfig[0]).Length == (original.Doh ?? new DohConfig[0]).Length &&
                    (original.Doh ?? new DohConfig[0]).All(d => (current.Doh ?? new DohConfig[0]).Any(c => c.Index == d.Index && c.Template == d.Template && c.Flags == d.Flags));
                bool ours = current.Manual && current.Servers.Length > 0 && current.Servers.All(Dns.KnownServers.Contains) &&
                    (current.Doh == null || current.Doh.All(d => d.Flags == 2 && EncryptedDns.Templates.Where(t => t != null).Contains(d.Template)));
                if (!same && !ours) return false;
            }
            return true;
        }
        void Restore(List<AdapterBackup> records, bool deferUnavailable = false, bool onlyUnchanged = false)
        {
            var available = system.Adapters(false);
            var pending = new List<AdapterBackup>();
            var errors = new List<string>();
            foreach (var record in records)
            {
                try
                {
                    var id = new Guid(record.Guid);
                    if (!available.Any(a => a.Id == id))
                    {
                        if (!deferUnavailable) throw new IOException("Адаптер недоступен: " + id);
                        pending.Add(record); DeferredAdapters.Add(id.ToString()); continue;
                    }
                    if (onlyUnchanged && !SafeToRestore(system, record)) { pending.Add(record); RestoreSkipped = true; continue; }
                    if (record.Ipv6Binding.HasValue)
                    {
                        var bindings = system as IDnsBindingSystem;
                        if (bindings == null) throw new IOException("Не удалось восстановить состояние IPv6.");
                        bindings.SetIpv6Binding(id, record.Ipv6Binding.Value);
                    }
                    foreach (var family in record.Families) system.Set(id, family);
                }
                catch (Exception error)
                {
                    pending.Add(record);
                    var native = error as Win32Exception;
                    if (deferUnavailable && native != null && (native.NativeErrorCode == 1167 || native.NativeErrorCode == 1168 || native.NativeErrorCode == 55))
                        DeferredAdapters.Add(record.Guid);
                    else errors.Add(error.Message);
                }
            }
            store.Save(pending);
            if (errors.Count > 0) throw new IOException(string.Join("; ", errors));
        }
        internal static string Run(bool enable, string[] servers, bool resetKnown = false, string dohTemplate = null, bool restoreReturned = false)
        {
            using (var mutex = new Mutex(false, @"Global\HbAi.SmartDns.Settings"))
            {
                bool locked = false;
                try
                {
                    try { locked = mutex.WaitOne(30000); }
                    catch (AbandonedMutexException) { locked = true; }
                    if (!locked) return "Другая операция DNS ещё выполняется.";
                    var engine = new DnsSettingsEngine(new NativeDnsSystem(), new RegistryDnsBackupStore());
                    if (restoreReturned) engine.RestoreReturned();
                    else if (resetKnown) engine.ResetKnown(servers);
                    else engine.Change(enable, servers, dohTemplate);
                    if (engine.RestoreSkipped) return "[DNS-RESTORE-SKIPPED]Настройки подключения изменены другой программой. Копия сохранена для ручного восстановления.";
                    if (engine.DeferredAdapters.Count > 0) return "[DNS-DEFERRED]DNS выключен на доступных подключениях. Копии недоступных подключений сохранены.";
                    return null;
                }
                catch (Exception error) { return error.Message; }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }
    }
}
