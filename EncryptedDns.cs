using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;

namespace HbAi.XboxDns
{
    public sealed class DohConfig
    {
        public uint Index { get; set; }
        public string Template { get; set; }
        public ulong Flags { get; set; }
        public uint Kind { get; set; } // 0/1: HTTPS (legacy backups), 2: TLS.
        public ushort Port { get; set; }
    }
    interface IDnsBindingSystem
    {
        bool ReadIpv6Binding(Guid id);
        void SetIpv6Binding(Guid id, bool enabled);
    }
    static class EncryptedDns
    {
        internal static string Run(int provider, bool keepAntigravity = false, bool tls = false)
        {
            if(provider>=0&&provider<Templates.Length&&!Dns.IsAvailable(provider))return "Провайдер временно недоступен. Выберите другой DNS.";
            if (provider < 0 || provider >= Templates.Length || (tls ? !DotDns.Available(provider) : Templates[provider] == null)) return "Для DoH выберите Comss.one, Bezmezhau, DNS-AI или MagicDNS.";
            try
            {
                // Inspect persisted local AND organization policies, without depending on
                // WMI/CIM. Do not mistake a broken provider for incompatible Windows.
                var policies = NrptRegistryReader.Read(true);
                string conflict = PolicyConflict(policies);
                if (conflict != null && !(keepAntigravity && CanKeepAntigravity(NrptRegistryReader.Read(false), policies))) return conflict;
                return DnsSettingsEngine.Run(true, tls ? DotDns.Servers(provider) : ServersFor(provider), false, tls ? DotDns.Host(provider) : Templates[provider], tls);
            }
            catch (Exception error) { return "Не удалось проверить совместимость DoH: " + error.Message; }
        }
        // Consent only covers the known unlocker's two exact local namespaces.
        // Organization policies, broad matches and unrelated rules cannot be bypassed.
        internal static bool CanKeepAntigravity(AgRule[] local, AgRule[] all)
        {
            local = local.Where(r => !IsWindowsUpdateException(r)).ToArray();
            all = all.Where(r => !IsWindowsUpdateException(r)).ToArray();
            return all.Length > 0 && all.Length <= 2 && local.Length == all.Length &&
                all.SelectMany(r => r.Names).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Length &&
                all.All(r => r.Names.Length == 1 && AntigravityDns.Domains.Contains(r.Names[0]) &&
                    (AgTakeover.KnownTag(r.Comment) || AntigravityDns.Owned(r)) &&
                    local.Any(l => l.Id == r.Id && l.Comment == r.Comment && l.Names.SequenceEqual(r.Names) && l.Servers.SequenceEqual(r.Servers)));
        }
        // Preserve this exact local, service-scoped resiliency entry. It still follows
        // its NRPT resolver; allowing adapter DoH does not encrypt or rewrite that exception.
        // A matching hostname alone is insufficient, especially for organization policies.
        internal static bool IsWindowsUpdateException(AgRule rule)
        {
            const string host = "slscr.update.microsoft.com";
            return rule != null && rule.Local && rule.Version == 2 && rule.ConfigOptions == 8 &&
                string.Equals(rule.Id, "DNS_RESILIENCY_" + host, StringComparison.OrdinalIgnoreCase) &&
                rule.Names != null && rule.Names.Length == 1 && string.Equals(rule.Names[0], host, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(rule.Comment) && string.IsNullOrEmpty(rule.DisplayName) &&
                rule.Servers != null && rule.Servers.Length > 0 && rule.Servers.All(s => { IPAddress ip; return IPAddress.TryParse(s, out ip); });
        }
        internal static string PolicyConflict(AgRule[] policies)
        {
            policies = policies.Where(r => !IsWindowsUpdateException(r)).ToArray();
            if (policies.Length == 0) return null;
            string domains = string.Join(", ", policies.SelectMany(p => p.Names).Distinct().Take(4));
            return "Эти домены используют отдельные DNS-настройки: " + domains + ".\n\nОни могут обходить выбранный DoH. Проверьте правила в программе или политике Windows, которая их создала, и повторите подключение. Настройки не изменены.";
        }
        // Only provider/IP combinations with a verified HTTPS endpoint are offered.
        internal static string[] Templates {get{return RuntimeProviders.Snapshot.Templates;}}
        internal static bool ExplicitlyConfigured(FamilyBackup settings, int provider)
        {
            if (settings.DohCaptured && settings.Servers.Length>0 && settings.Doh!=null && settings.Doh.Length==settings.Servers.Length &&
                settings.Servers.SequenceEqual(DotDns.Servers(provider)) && settings.Doh.All(d=>d.Kind==2 && IsStrict(d) && d.Template==DotDns.Host(provider)) &&
                settings.Doh.Select(d=>d.Index).Distinct().Count()==settings.Servers.Length) return true;
            return settings.DohCaptured && settings.Servers.Length > 0 && settings.Doh != null && settings.Doh.Length == settings.Servers.Length &&
                settings.Doh.All(d => d.Index < settings.Servers.Length && d.Kind != 2 && d.Flags == 2 && d.Template == Templates[provider]) &&
                settings.Doh.Select(d => d.Index).Distinct().Count() == settings.Servers.Length;
        }
        internal static string[] ServersFor(int provider)
        { return RuntimeProviders.All[provider].windowsDohServers; }
        internal static DohConfig[] Strict(int count, string template, bool tls = false)
        { return Enumerable.Range(0, count).Select(i => new DohConfig { Index = (uint)i, Template = template, Flags = tls ? 1UL : 2UL, Kind = tls ? 2U : 0U, Port = tls ? (ushort)853 : (ushort)0 }).ToArray(); }
        internal static bool IsStrict(DohConfig entry)
        { return entry.Kind == 2 ? entry.Flags == 1 && (entry.Port == 0 || entry.Port == 853) : entry.Flags == 2; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Settings3
        {
            public uint Version; public ulong Flags;
            public IntPtr Domain, NameServer, SearchList;
            public uint RegistrationEnabled, RegisterAdapterName, EnableLLMNR, QueryAdapterName;
            public IntPtr ProfileNameServer;
            public uint DisableUnconstrainedQueries; public IntPtr SupplementalSearchList;
            public uint Count; public IntPtr Properties;
            public uint ProfileCount; public IntPtr ProfileProperties;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Property { public uint Version, Index, Type; public IntPtr Value; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct DohSettings { public IntPtr Template; public ulong Flags; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct DotSettings { public IntPtr Hostname; public ulong Flags; public ushort Port; }
        [DllImport("iphlpapi.dll", EntryPoint = "GetInterfaceDnsSettings", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint Get(Guid id, ref Settings3 settings);
        [DllImport("iphlpapi.dll", EntryPoint = "SetInterfaceDnsSettings", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint Set(Guid id, ref Settings3 settings);
        [DllImport("iphlpapi.dll", EntryPoint = "FreeInterfaceDnsSettings", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern void Free(ref Settings3 settings);

        internal static void Capture(Guid id, FamilyBackup family)
        {
            var native = new Settings3 { Version = 3, Flags = family.Family == "IPv6" ? 1UL : 0UL };
            uint result = Get(id, ref native);
            if (result == 87 || result == 50 || result == 120) return; // Older Windows: ordinary DNS is still supported.
            if (result != 0) throw new Win32Exception((int)result, "Не удалось прочитать DoH адаптера.");
            try
            {
                family.ProfilePresent = !string.IsNullOrWhiteSpace(Marshal.PtrToStringUni(native.ProfileNameServer)) || native.ProfileCount > 0;
                if (native.Count > 32 || (native.Count > 0 && native.Properties == IntPtr.Zero)) throw new IOException("Неизвестный формат DoH Windows.");
                var entries = new List<DohConfig>();
                for (int i = 0; i < native.Count; i++)
                {
                    var property = (Property)Marshal.PtrToStructure(IntPtr.Add(native.Properties, i * Marshal.SizeOf(typeof(Property))), typeof(Property));
                    if (property.Version != 1 || (property.Type != 1 && property.Type != 2) || property.Value == IntPtr.Zero || property.Index >= family.Servers.Length)
                        throw new IOException("Существующие DNS используют неподдерживаемые свойства. Настройки не изменены.");
                    if (property.Type == 2) {
                        var dot = (DotSettings)Marshal.PtrToStructure(property.Value, typeof(DotSettings));
                        if (dot.Flags != 0) entries.Add(new DohConfig { Index = property.Index, Template = Marshal.PtrToStringUni(dot.Hostname), Flags = dot.Flags, Kind = 2, Port = dot.Port });
                        continue;
                    }
                    var doh = (DohSettings)Marshal.PtrToStructure(property.Value, typeof(DohSettings));
                    if (doh.Flags == 0) continue; // Windows can enumerate disabled properties for ordinary DNS.
                    entries.Add(new DohConfig { Index = property.Index, Template = Marshal.PtrToStringUni(doh.Template), Flags = doh.Flags });
                }
                family.Doh = entries.ToArray(); family.DohCaptured = true;
            }
            finally { Free(ref native); }
        }
        internal static void Validate(FamilyBackup family)
        {
            if (!family.DohCaptured) { if (family.Doh != null && family.Doh.Length > 0) throw new InvalidDataException("Некорректная копия DoH."); return; }
            if (family.Doh == null || family.Doh.Length > family.Servers.Length * 2) throw new InvalidDataException("Некорректная копия шифрования DNS.");
            var indices = new HashSet<string>();
            foreach (var entry in family.Doh)
            {
                if (entry == null || entry.Index >= family.Servers.Length || entry.Kind > 2 ||
                    !indices.Add(entry.Index + "/" + (entry.Kind == 2 ? 2 : 1))) throw new InvalidDataException("Повреждены параметры шифрования DNS.");
                if (entry.Kind == 2) {
                    if ((entry.Flags & ~63UL) != 0 || Uri.CheckHostName(entry.Template ?? "") != UriHostNameType.Dns)
                        throw new InvalidDataException("Повреждены параметры TLS.");
                } else {
                    Uri uri;
                    if ((entry.Flags & ~15UL) != 0 || ((entry.Flags & 3) == 3) || ((entry.Flags & 12) != 0 && (entry.Flags & 3) == 0) ||
                        ((entry.Flags & 2) != 0 && (!Uri.TryCreate(entry.Template, UriKind.Absolute, out uri) || uri.Scheme != "https")) ||
                        ((entry.Flags & 1) != 0 && entry.Template != null)) throw new InvalidDataException("Повреждены параметры DoH.");
                }
            }
        }

        internal static void Write(Guid id, FamilyBackup family)
        {
            Validate(family);
            var allocated = new List<IntPtr>();
            Func<string, IntPtr> str = value => { var ptr = Marshal.StringToHGlobalUni(value ?? ""); allocated.Add(ptr); return ptr; };
            var settings = new Settings3 { Version = 3, Flags = 0x11002UL | (family.Family == "IPv6" ? 1UL : 0UL), NameServer = str(string.Join(",", family.Servers)), Count = (uint)family.Doh.Length };
            try
            {
                if (settings.Count > 0)
                {
                    settings.Properties = Marshal.AllocHGlobal(family.Doh.Length * Marshal.SizeOf(typeof(Property))); allocated.Add(settings.Properties);
                    for (int i = 0; i < family.Doh.Length; i++)
                    {
                        var entry = family.Doh[i];
                        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(entry.Kind == 2 ? typeof(DotSettings) : typeof(DohSettings))); allocated.Add(ptr);
                        if (entry.Kind == 2) Marshal.StructureToPtr(new DotSettings { Hostname = str(entry.Template), Flags = entry.Flags, Port = entry.Port }, ptr, false);
                        else Marshal.StructureToPtr(new DohSettings { Template = entry.Template == null ? IntPtr.Zero : str(entry.Template), Flags = entry.Flags }, ptr, false);
                        Marshal.StructureToPtr(new Property { Version = 1, Index = entry.Index, Type = entry.Kind == 2 ? 2U : 1U, Value = ptr }, IntPtr.Add(settings.Properties, i * Marshal.SizeOf(typeof(Property))), false);
                    }
                }
                uint error = Set(id, ref settings);
                if ((error == 87 || error == 50) && !family.Doh.Any(e => e.Kind == 2)) { settings.Flags &= ~0x10000UL; error = Set(id, ref settings); }
                if (error != 0) throw new Win32Exception((int)error, "Windows не применила DoH: " + error);
            }
            finally { foreach (var ptr in allocated) Marshal.FreeHGlobal(ptr); }
            var actual = new FamilyBackup { Family = family.Family, Servers = family.Servers };
            Capture(id, actual);
            if (!actual.DohCaptured || actual.Doh.Length != family.Doh.Length || !family.Doh.All(e => actual.Doh.Any(a => a.Index == e.Index && a.Flags == e.Flags && a.Template == e.Template && (a.Kind == 2) == (e.Kind == 2) && a.Port == e.Port)))
                throw new IOException("Windows не подтвердила параметры шифрования DNS.");
        }
        internal static ManagementObject Binding(Guid id)
        {
            using (var search = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT * FROM MSFT_NetAdapterBindingSettingData WHERE ComponentID='ms_tcpip6'"))
            {
                search.Options.Timeout = TimeSpan.FromSeconds(10);
                using (var rows = search.Get())
                    foreach (ManagementObject row in rows)
                    {
                        string instance = Convert.ToString(row["InstanceID"]);
                        if (instance.StartsWith(id.ToString("B") + "::", StringComparison.OrdinalIgnoreCase)) return row;
                        row.Dispose();
                    }
            }
            throw new IOException("Не найдена привязка IPv6 адаптера " + id);
        }
        internal static bool ReadBinding(Guid id) { using (var binding = Binding(id)) return Convert.ToBoolean(binding["Enabled"]); }
        internal static void SetBinding(Guid id, bool enabled)
        {
            if (ReadBinding(id) == enabled) return;
            using (var binding = Binding(id))
            using (var result = binding.InvokeMethod(enabled ? "Enable" : "Disable", null, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(15) }))
                if (result == null || Convert.ToUInt32(result["ReturnValue"]) != 0) throw new IOException("Windows не изменила состояние IPv6.");
            if (ReadBinding(id) != enabled) throw new IOException("Windows не подтвердила состояние IPv6.");
        }
    }
}
