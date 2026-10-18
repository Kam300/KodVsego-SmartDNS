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
    }
    interface IDnsBindingSystem
    {
        bool ReadIpv6Binding(Guid id);
        void SetIpv6Binding(Guid id, bool enabled);
    }
    static class EncryptedDns
    {
        internal static string Run(int provider, bool keepAntigravity = false)
        {
            if (provider < 0 || provider >= Templates.Length || Templates[provider] == null) return "Для DoH выберите Comss.one, GeoHide, Bezmezhau или DNS-AI.";
            try
            {
                // Inspect persisted local AND organization policies, without depending on
                // WMI/CIM. Do not mistake a broken provider for incompatible Windows.
                var policies = NrptRegistryReader.Read(true);
                string conflict = PolicyConflict(policies);
                if (conflict != null && !(keepAntigravity && CanKeepAntigravity(NrptRegistryReader.Read(false), policies))) return conflict;
                return DnsSettingsEngine.Run(true, ServersFor(provider), false, Templates[provider]);
            }
            catch (Exception error) { return "Не удалось проверить совместимость DoH: " + error.Message; }
        }
        // Consent only covers the known unlocker's two exact local namespaces.
        // Organization policies, broad matches and unrelated rules cannot be bypassed.
        internal static bool CanKeepAntigravity(AgRule[] local, AgRule[] all)
        {
            return all.Length > 0 && all.Length <= 2 && local.Length == all.Length &&
                all.SelectMany(r => r.Names).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Length &&
                all.All(r => r.Names.Length == 1 && AntigravityDns.Domains.Contains(r.Names[0]) &&
                    (AgTakeover.KnownTag(r.Comment) || AntigravityDns.Owned(r)) &&
                    local.Any(l => l.Id == r.Id && l.Comment == r.Comment && l.Names.SequenceEqual(r.Names) && l.Servers.SequenceEqual(r.Servers)));
        }
        internal static string PolicyConflict(AgRule[] policies)
        {
            if (policies.Length == 0) return null;
            string domains = string.Join(", ", policies.SelectMany(p => p.Names).Distinct().Take(4));
            return "Обнаружены отдельные DNS-правила: " + domains + ".\n\nОтключите DNS-настройку прежнего анлокера и повторите попытку. Для полного шифрования эти правила должны быть отключены.";
        }
        // Only provider/IP combinations with a verified HTTPS endpoint are offered.
        internal static readonly string[] Templates = { "https://dns.comss.one/dns-query", "https://geohide.ru/dns-query", null, null, "https://dns.bezmezhau.com/dns-query", "https://dns.dns-ai.ru/dns-query" };
        internal static bool ExplicitlyConfigured(FamilyBackup settings, int provider)
        {
            return settings.DohCaptured && settings.Servers.Length > 0 && settings.Doh != null && settings.Doh.Length == settings.Servers.Length &&
                settings.Doh.All(d => d.Index < settings.Servers.Length && d.Flags == 2 && d.Template == Templates[provider]) &&
                settings.Doh.Select(d => d.Index).Distinct().Count() == settings.Servers.Length;
        }
        internal static string[] ServersFor(int provider)
        { return provider == 0 ? new[] { "195.133.25.16" } : Dns.V4[provider]; }
        internal static DohConfig[] Strict(int count, string template)
        { return Enumerable.Range(0, count).Select(i => new DohConfig { Index = (uint)i, Template = template, Flags = 2 }).ToArray(); }

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
                    if (property.Version != 1 || property.Type != 1 || property.Value == IntPtr.Zero || property.Index >= family.Servers.Length)
                        throw new IOException("Существующие DNS используют неподдерживаемые свойства. Настройки не изменены.");
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
            if (family.Doh == null || family.Doh.Length > family.Servers.Length) throw new InvalidDataException("Некорректная копия DoH.");
            var indices = new HashSet<uint>();
            foreach (var entry in family.Doh)
            {
                Uri uri;
                if (entry == null || entry.Index >= family.Servers.Length || !indices.Add(entry.Index) || (entry.Flags & ~15UL) != 0 ||
                    ((entry.Flags & 3) == 3) || ((entry.Flags & 12) != 0 && (entry.Flags & 3) == 0) || ((entry.Flags & 2) != 0 && (!Uri.TryCreate(entry.Template, UriKind.Absolute, out uri) || uri.Scheme != "https")) ||
                    ((entry.Flags & 1) != 0 && entry.Template != null)) throw new InvalidDataException("Повреждены параметры DoH.");
            }
        }
        internal static void Write(Guid id, FamilyBackup family)
        {
            Validate(family);
            var allocated = new List<IntPtr>();
            Func<string, IntPtr> str = value => { var ptr = Marshal.StringToHGlobalUni(value ?? ""); allocated.Add(ptr); return ptr; };
            var settings = new Settings3 { Version = 3, Flags = 0x1002UL | (family.Family == "IPv6" ? 1UL : 0UL), NameServer = str(string.Join(",", family.Servers)), Count = (uint)family.Doh.Length };
            try
            {
                if (settings.Count > 0)
                {
                    settings.Properties = Marshal.AllocHGlobal(family.Doh.Length * Marshal.SizeOf(typeof(Property))); allocated.Add(settings.Properties);
                    for (int i = 0; i < family.Doh.Length; i++)
                    {
                        var entry = family.Doh[i];
                        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(DohSettings))); allocated.Add(ptr);
                        Marshal.StructureToPtr(new DohSettings { Template = entry.Template == null ? IntPtr.Zero : str(entry.Template), Flags = entry.Flags }, ptr, false);
                        Marshal.StructureToPtr(new Property { Version = 1, Index = entry.Index, Type = 1, Value = ptr }, IntPtr.Add(settings.Properties, i * Marshal.SizeOf(typeof(Property))), false);
                    }
                }
                uint error = Set(id, ref settings);
                if (error != 0) throw new Win32Exception((int)error, "Windows не применила DoH: " + error);
            }
            finally { foreach (var ptr in allocated) Marshal.FreeHGlobal(ptr); }
            var actual = new FamilyBackup { Family = family.Family, Servers = family.Servers };
            Capture(id, actual);
            if (!actual.DohCaptured || actual.Doh.Length != family.Doh.Length || !family.Doh.All(e => actual.Doh.Any(a => a.Index == e.Index && a.Flags == e.Flags && a.Template == e.Template)))
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
