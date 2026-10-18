using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    public sealed class AgSavedRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Comment { get; set; }
        public string DisplayName { get; set; }
        public string[] Servers { get; set; }
        public string[] AppliedServers { get; set; }
        internal bool Matches(AgRule rule)
        {
            return rule.Id == Id && rule.Names.Length == 1 && rule.Names[0] == Name &&
                rule.Comment == Comment && rule.DisplayName == DisplayName;
        }
    }
    public sealed class AgSavedState
    {
        public int Version { get; set; }
        public AgSavedRule[] Rules { get; set; }
    }
    interface IAgTakeoverStore
    {
        AgSavedRule[] Load();
        void Save(AgSavedRule[] rules);
    }
    sealed class RegistryAgTakeoverStore : IAgTakeoverStore
    {
        const string Path = @"SOFTWARE\KodVsego\SmartDns", Value = "AntigravityPreviousRules";
        internal static void Validate(AgSavedRule[] rules)
        {
            if (rules == null || rules.Length > 2) throw new InvalidDataException("Повреждена копия правил Antigravity.");
            var ids = new HashSet<Guid>();
            var names = new HashSet<string>();
            foreach (var r in rules)
            {
                Guid id;
                if (r == null || !Guid.TryParse(r.Id, out id) || !ids.Add(id) || !names.Add(r.Name) ||
                    !AntigravityDns.Domains.Contains(r.Name) || !AgTakeover.KnownTag(r.Comment) ||
                    r.Servers == null || r.Servers.Length == 0 || r.Servers.Length > 16 ||
                    r.Servers.Any(s => { IPAddress ip; return !IPAddress.TryParse(s, out ip); }) ||
                    (r.AppliedServers != null && !AntigravityDns.ServerSets.Any(s => s.SequenceEqual(r.AppliedServers))))
                    throw new InvalidDataException("Повреждена копия правил Antigravity.");
            }
        }
        public AgSavedRule[] Load()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(Path))
            {
                if (key == null || key.GetValue(Value) == null) return new AgSavedRule[0];
                var raw = key.GetValue(Value) as string;
                if (raw == null) throw new InvalidDataException("Неверный формат копии Antigravity.");
                var state = new JavaScriptSerializer().Deserialize<AgSavedState>(raw);
                if (state == null || state.Version != 1) throw new InvalidDataException("Неизвестная версия копии Antigravity.");
                Validate(state.Rules); return state.Rules;
            }
        }
        public void Save(AgSavedRule[] rules)
        {
            Validate(rules);
            if (rules.Length == 0)
            {
                using (var key = Registry.LocalMachine.OpenSubKey(Path, true))
                    if (key != null) { key.DeleteValue(Value, false); key.Flush(); }
                return;
            }
            using (var key = Registry.LocalMachine.CreateSubKey(Path))
            {
                key.SetValue(Value, new JavaScriptSerializer().Serialize(new AgSavedState { Version = 1, Rules = rules }), RegistryValueKind.String);
                key.Flush();
            }
        }
    }
    static class AgTakeover
    {
        internal static bool KnownTag(string tag) { return tag == "AG_UNLOCKER_NRPT_V2" || tag == "AG_UNLOCKER_NRPT"; }
        static bool Relevant(AgRule r) { return r.Names.Any(n => AntigravityDns.Domains.Any(d => AntigravityDns.Matches(n, d))); }
        internal static void Enable(IAgRules system, IAgTakeoverStore store)
        {
            if (store.Load().Length > 0) throw new IOException("Сначала отключите нашу опцию для восстановления сохранённых правил Antigravity.");
            var before = system.Read();
            var foreign = before.Where(r => !AntigravityDns.Owned(r) && Relevant(r)).ToArray();
            if (foreign.Length == 0) { AntigravityDns.Change(system, true); return; }
            // Force is deliberately limited to exact FQDN rules of the known unlocker.
            if (foreign.Any(r => !KnownTag(r.Comment) || r.Names.Length != 1 || !AntigravityDns.Domains.Contains(r.Names[0])) ||
                AntigravityDns.Domains.Any(d => before.Count(r => r.Names.Any(n => AntigravityDns.Matches(n, d))) > 1))
                throw new IOException("Принудительная замена доступна только для точных правил AG Unlocker. Общие правила, VPN и политики организации не изменены.");
            if (system.Effective().Any(r => Relevant(r) && !before.Any(b => b.Names.SequenceEqual(r.Names) && b.Servers.SequenceEqual(r.Servers))))
                throw new IOException("Действует другая DNS-политика Windows. Принудительная замена отменена.");
            var saved = foreign.Select(r => new AgSavedRule { Id = r.Id, Name = r.Names[0], Comment = r.Comment,
                DisplayName = r.DisplayName, Servers = r.Servers.ToArray(), AppliedServers = AntigravityDns.Servers.ToArray() }).ToArray();
            RegistryAgTakeoverStore.Validate(saved);
            store.Save(saved); // Machine-wide durable snapshot before the first change.
            try
            {
                foreach (var rule in foreign) system.SetServers(rule, AntigravityDns.Servers);
                foreach (string domain in AntigravityDns.Domains)
                    if (!before.Any(r => r.Names.Length == 1 && r.Names[0] == domain)) system.Add(domain);
                var effective = system.Effective();
                if (effective.Any(r => Relevant(r) && (r.Names.Length != 1 || !AntigravityDns.Domains.Contains(r.Names[0]) || !r.Servers.SequenceEqual(AntigravityDns.Servers))) ||
                    AntigravityDns.Domains.Any(d => !effective.Any(r => r.Names.Length == 1 && r.Names[0] == d && r.Servers.SequenceEqual(AntigravityDns.Servers))))
                    throw new IOException("Windows не подтвердила DNS Antigravity.");
                system.Flush();
            }
            catch (Exception error)
            {
                try
                {
                    foreach (var rule in system.Read().Where(AntigravityDns.Owned).Where(r => !before.Any(b => b.Id == r.Id))) system.Remove(rule.Id);
                    Restore(system, store);
                }
                catch (Exception rollback) { throw new IOException(error.Message + "; восстановление или очистка кэша не завершены: " + rollback.Message); }
                throw new IOException(error.Message + "; прежние DNS-правила восстановлены.");
            }
        }
        internal static void Restore(IAgRules system, IAgTakeoverStore store)
        {
            var saved = store.Load();
            RegistryAgTakeoverStore.Validate(saved);
            if (saved.Length == 0) return;
            var pending = new List<AgSavedRule>();
            var errors = new List<string>();
            foreach (var snapshot in saved)
            {
                try
                {
                    var current = system.Read().SingleOrDefault(r => snapshot.Matches(r));
                    if (current == null) throw new IOException("Исходное правило удалено или изменено: " + snapshot.Name);
                    if (current.Servers.SequenceEqual(snapshot.Servers)) continue;
                    if (!current.Servers.SequenceEqual(snapshot.AppliedServers ?? AntigravityDns.ServerSets[0])) throw new IOException("DNS изменён другой программой: " + snapshot.Name);
                    system.SetServers(current, snapshot.Servers);
                }
                catch (Exception error) { pending.Add(snapshot); errors.Add(error.Message); }
            }
            store.Save(pending.ToArray());
            if (errors.Count > 0) throw new IOException(string.Join("; ", errors) + ". Копия для повторного восстановления сохранена.");
            system.Flush();
        }
    }
}
