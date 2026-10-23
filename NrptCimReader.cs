using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Win32;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace HbAi.XboxDns
{
    static class NrptCimReader
    {
        internal static AgRule[] Read(bool effective)
        {
            using (var session = CimSession.Create(null))
            using (var options = new CimOperationOptions { Timeout = TimeSpan.FromSeconds(15) })
            {
                var parameters = new CimMethodParametersCollection();
                if (effective) parameters.Add(CimMethodParameter.Create("Effective", true, CimType.Boolean, CimFlags.In));
                using (var result = session.InvokeMethod(@"root/Microsoft/Windows/DNS", effective ? "PS_DnsClientNrptPolicy" : "PS_DnsClientNrptRule", "Get", parameters, options))
                {
                    if (result == null || result.ReturnValue == null || result.ReturnValue.Value == null)
                        return ConfirmEmpty(() => NrptRegistryReader.Read(effective));
                    if (Convert.ToUInt32(result.ReturnValue.Value) != 0)
                        throw new IOException("Ошибка CIM NRPT: " + Convert.ToString(result.ReturnValue.Value));
                    var output = result.OutParameters["cmdletOutput"];
                    if (output == null) throw new IOException("CIM не вернул поле правил NRPT.");
                    if (output.Value == null) return new AgRule[0]; // Successful, explicit empty result.
                    var rows = output.Value as CimInstance[];
                    if (rows == null) throw new IOException("Неизвестный формат CIM-правил NRPT.");
                    try
                    {
                        return rows.Select(row => new AgRule {
                            Id = effective ? null : Convert.ToString(Value(row,"Name")),
                            Comment = effective ? null : Convert.ToString(Value(row,"Comment")),
                            DisplayName = effective ? null : Convert.ToString(Value(row,"DisplayName")),
                            Names = WindowsAgRules.Strings(Value(row,"Namespace")),
                            Servers = WindowsAgRules.Strings(Value(row,"NameServers"))
                        }).ToArray();
                    }
                    finally { foreach (var row in rows) row.Dispose(); }
                }
            }
        }
        internal static AgRule[] ConfirmEmpty(Func<AgRule[]> readPersisted)
        {
            // Some DNS cmdlet providers omit the entire result when no rules exist.
            // Confirm absence independently; never turn an unverified read failure
            // or a nonzero Windows return code into an empty policy.
            var stored = readPersisted();
            if (stored.Length == 0) return stored;
            throw new IOException("CIM не вернула результат NRPT, но в реестре есть правила.");
        }
        static object Value(CimInstance row, string name)
        {
            var property = row.CimInstanceProperties[name];
            if (property == null) throw new IOException("CIM-правило не содержит поле " + name);
            return property.Value;
        }
    }

    // MS-GPNRPT specifies both stores. The union is deliberately conservative for
    // conflict detection: it does not claim to reproduce effective GPO precedence.
    // Registry reads do not depend on the DNS PowerShell/CIM provider being healthy.
    static class NrptRegistryReader
    {
        const string Local = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig";
        const string Policy = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig";

        internal static AgRule[] Read(bool includePolicies)
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                return ReadStores(machine, includePolicies ? new[] { Local, Policy } : new[] { Local });
        }
        internal static AgRule[] ReadOrganization()
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                return ReadStores(machine, new[] { Policy });
        }
        internal static AgRule[] ReadStores(RegistryKey root, string[] paths)
        {
            var rules = new List<AgRule>();
            foreach (var path in paths)
                using (var store = root.OpenSubKey(path, false))
                {
                    if (store == null) continue; // An absent store means no persisted rules.
                    foreach (var id in store.GetSubKeyNames())
                        using (var row = store.OpenSubKey(id, false))
                        {
                            if (row == null) throw new IOException("DNS-правило изменилось во время чтения. Повторите попытку.");
                            rules.Add(Parse(id, name => row.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                                string.Equals(path, Local, StringComparison.OrdinalIgnoreCase)));
                        }
                }
            return rules.ToArray();
        }
        internal static AgRule Parse(string id, Func<string, object> value, bool local = false)
        {
            // Foreign entries can have a named registry key rather than a GUID.
            // Keep them visible for conflict checks; mutation/backup ownership stays GUID-only.
            if (string.IsNullOrWhiteSpace(id) || id.Length > 255 || id.IndexOf('\\') >= 0 || id.Any(char.IsControl))
                throw new InvalidDataException("Неизвестный идентификатор правила NRPT.");
            var names = value("Name") as string[];
            if (names == null || names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Не удалось прочитать домены правила NRPT: " + id);
            var servers = value("GenericDNSServers");
            if (servers != null && !(servers is string)) throw new InvalidDataException("Неизвестный формат серверов NRPT: " + id);
            return new AgRule { Id = id, Names = names, Servers = WindowsAgRules.Strings(servers),
                Local = local, Version = value("Version") as int?, ConfigOptions = value("ConfigOptions") as int?,
                Comment = Text(value("Comment")), DisplayName = Text(value("DisplayName")) };
        }
        static string Text(object value)
        {
            if (value == null) return "";
            var text = value as string;
            if (text == null) throw new InvalidDataException("Неизвестный формат описания NRPT.");
            return text;
        }
    }
}
