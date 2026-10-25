using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using HbAi.XboxDns;

sealed class MemoryStore : IDnsBackupStore
{
    public string Json;
    public bool FailSave;
    public List<AdapterBackup> Load()
    {
        return Json == null ? new List<AdapterBackup>() :
            new JavaScriptSerializer().Deserialize<DnsBackup>(Json).Adapters.ToList();
    }
    public void Save(List<AdapterBackup> records)
    {
        if (FailSave) throw new IOException("Storage unavailable");
        RegistryDnsBackupStore.Validate(records);
        Json = records.Count == 0 ? null : new JavaScriptSerializer().Serialize(new DnsBackup { Version = 1, Adapters = records.ToArray() });
    }
}
sealed class FakeSystem : IDnsSystem, IDnsBindingSystem
{
    public bool CaptureDoh, Ipv6Enabled = true, ProfilePresent;
    public int BindingWrites;
    public bool FailBinding;
    public Dictionary<string, DohConfig[]> Encryption = new Dictionary<string, DohConfig[]>();
    public bool ReadIpv6Binding(Guid id) { return Ipv6Enabled; }
    public void SetIpv6Binding(Guid id, bool enabled) { BindingWrites++; if (FailBinding) throw new IOException("Binding write failed"); Ipv6Enabled = enabled; }
    public readonly Guid Id = new Guid("00000000-0000-0000-0000-000000000001");
    public List<DnsAdapter> All = new List<DnsAdapter>();
    public Dictionary<string, string[]> Values = new Dictionary<string, string[]>();
    public HashSet<int> FailWrites = new HashSet<int>();
    public int Writes;
    public bool Unsupported;
    public FakeSystem() { Add(Id); }
    public void Add(Guid id)
    {
        All.Add(new DnsAdapter { Id = id, Name = "Test adapter", V4 = true, V6 = true });
        Values[id + "/IPv4"] = new[] { "192.0.2.53", "192.0.2.54" };
        Values[id + "/IPv6"] = new string[0];
    }
    public void EnsureSupported() { if (Unsupported) throw new NotSupportedException(); }
    public DnsAdapter[] Adapters(bool activeOnly) { return All.ToArray(); }
    public FamilyBackup Read(Guid id, string family)
    {
        var values = Values[id + "/" + family];
        return new FamilyBackup { Family = family, Manual = values.Length > 0, Servers = values.ToArray(), DohCaptured = CaptureDoh, ProfilePresent = ProfilePresent,
            Doh = CaptureDoh ? (Encryption.ContainsKey(id + "/" + family) ? Encryption[id + "/" + family] : new DohConfig[0]) : null };
    }
    public void Set(Guid id, FamilyBackup settings)
    {
        Writes++;
        if (FailWrites.Contains(Writes)) throw new IOException("Simulated adapter failure");
        NativeDnsSystem.ValidateFamily(settings);
        Values[id + "/" + settings.Family] = settings.Servers.ToArray();
        if (settings.DohCaptured) Encryption[id + "/" + settings.Family] = settings.Doh.ToArray();
    }
    public void Flush() { }
}
static class NativeDnsTests
{
    static int passed;
    static FakeSystem system;
    static MemoryStore store;
    static DnsSettingsEngine engine;
    static readonly string[] Smart = { "83.220.169.155", "212.109.195.93" };
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Fail(Action action)
    {
        bool failed = false;
        try { action(); } catch { failed = true; }
        Assert(failed, "Expected failure");
    }
    static void Test(string name, Action action)
    {
        system = new FakeSystem(); store = new MemoryStore(); engine = new DnsSettingsEngine(system, store);
        action(); passed++; Console.WriteLine("PASS " + name);
    }
    static void Original() { Assert(string.Join(",", system.Values[system.Id + "/IPv4"]) == "192.0.2.53,192.0.2.54", "Original DNS lost"); }
    static void Main()
    {
        Test("Manual DNS restored after a new engine instance", () => {
            engine.Change(true, Smart);
            new DnsSettingsEngine(system, store).Change(false, new string[0]);
            Original(); Assert(store.Json == null, "Backup not cleared");
        });
        Test("Automatic DNS remains automatic", () => {
            system.Values[system.Id + "/IPv4"] = new string[0];
            engine.Change(true, Smart); engine.Change(false, new string[0]);
            Assert(system.Values[system.Id + "/IPv4"].Length == 0, "Automatic mode lost");
        });
        Test("Switching provider preserves original backup", () => {
            engine.Change(true, Smart); engine.Change(true, new[] { "159.194.200.33" });
            engine.Change(false, new string[0]); Original();
        });
        Test("Unrelated new adapter untouched on restore", () => {
            engine.Change(true, Smart); var id = Guid.NewGuid(); system.Add(id);
            engine.Change(false, new string[0]);
            Assert(system.Values[id + "/IPv4"][0] == "192.0.2.53", "Unrelated adapter modified");
        });
        Test("Missing adapter retains backup and recovers later", () => {
            engine.Change(true, Smart); var adapter = system.All[0]; system.All.Clear();
            engine.Change(false, new string[0]); Assert(store.Json != null && engine.DeferredAdapters.Count == 1, "Deferred backup lost");
            system.All.Add(adapter); engine.Change(false, new string[0]); Original();
        });
        Test("No backup prevents reset", () => {
            Fail(() => engine.Change(false, new string[0])); Assert(system.Writes == 0, "Unexpected DNS write");
        });
        Test("Strict DoH pauses IPv6 only after a durable backup", () => {
            system.CaptureDoh = true;
            engine.Change(true, Smart, "https://geohide.ru/dns-query");
            Assert(!system.Ipv6Enabled && store.Load()[0].Ipv6Binding == true, "IPv6 state not saved");
            Assert(system.Encryption[system.Id + "/IPv4"].Length == 2 && system.Encryption[system.Id + "/IPv4"].All(d => d.Flags == 2), "UDP fallback allowed");
            Assert(RegistryDnsBackupStore.SnapshotVersion(store.Load()) == 2, "Old app could discard IPv6/DoH recovery metadata");
        });
        Test("Comss DoH sets one dedicated address and restores the original DNS", () => {
            system.CaptureDoh = true;
            engine.Change(true, EncryptedDns.ServersFor(0), EncryptedDns.Templates[0]);
            Assert(system.Values[system.Id + "/IPv4"].SequenceEqual(new[] {"195.133.25.16"}), "Plain Comss addresses used for encryption");
            var doh = system.Encryption[system.Id + "/IPv4"];
            Assert(doh.Length == 1 && doh[0].Index == 0 && doh[0].Flags == 2 && doh[0].Template == EncryptedDns.Templates[0], "Wrong encrypted settings");
            new DnsSettingsEngine(system, store).Change(false, new string[0]);
            Original(); Assert(system.Ipv6Enabled, "IPv6 binding not restored");
        });
        Test("DNS-AI and MagicDNS use strict encryption for both addresses and restore originals", () => {
            foreach(int provider in new[]{5,6}) {
                system.CaptureDoh=true;
                engine.Change(true,EncryptedDns.ServersFor(provider),EncryptedDns.Templates[provider]);
                Assert(system.Values[system.Id+"/IPv4"].SequenceEqual(Dns.V4[provider]),"Wrong provider addresses");
                Assert(system.Encryption[system.Id+"/IPv4"].Length==2 && system.Encryption[system.Id+"/IPv4"].All(d=>d.Flags==2 && d.Template==EncryptedDns.Templates[provider]),"Plaintext fallback configured");
                engine.Change(false,new string[0]);Original();Assert(system.Ipv6Enabled,"IPv6 not restored");
            }
        });
        Test("Moving from retired DNS-AI addresses preserves the original backup", () => {
            system.CaptureDoh=true;
            engine.Change(true,new[]{"194.87.196.13","186.246.49.127"},EncryptedDns.Templates[5]);
            string backup=store.Json;
            new DnsSettingsEngine(system,store).Change(true,EncryptedDns.ServersFor(5),EncryptedDns.Templates[5]);
            Assert(store.Json==backup,"Migration replaced the pre-SmartDNS backup");
            Assert(system.Values[system.Id+"/IPv4"].SequenceEqual(new[]{"194.87.196.13","45.151.102.219"}),"Retired address still applied");
            new DnsSettingsEngine(system,store).Change(false,new string[0]);Original();Assert(system.Ipv6Enabled,"Migration lost IPv6 backup");
        });
        Test("Retired DNS-AI addresses can be reset without an old app backup", () => {
            system.Values[system.Id+"/IPv4"]=new[]{"194.87.196.13","186.246.49.127"};
            engine.ResetKnown(Dns.KnownServers);
            Assert(system.Values[system.Id+"/IPv4"].Length==0,"Retired resolver was not included in known-address recovery");
        });
        Test("Existing DoH and IPv6 state survive a new engine instance", () => {
            system.CaptureDoh = true;
            system.Encryption[system.Id + "/IPv4"] = EncryptedDns.Strict(2, "https://original.example/dns-query");
            engine.Change(true, Smart, "https://geohide.ru/dns-query");
            new DnsSettingsEngine(system, store).Change(false, new string[0]);
            Original(); Assert(system.Ipv6Enabled && system.Encryption[system.Id + "/IPv4"].All(d => d.Template == "https://original.example/dns-query"), "Original encryption lost");
        });
        Test("Originally disabled IPv6 remains disabled after restore", () => {
            system.CaptureDoh = true; system.Ipv6Enabled = false;
            engine.Change(true, Smart, "https://geohide.ru/dns-query"); engine.Change(false, new string[0]);
            Assert(!system.Ipv6Enabled, "Disabled IPv6 was enabled");
        });
        Test("Switch to ordinary DNS clears app DoH and restores IPv6", () => {
            system.CaptureDoh = true;
            engine.Change(true, Smart, "https://geohide.ru/dns-query"); engine.Change(true, Smart);
            Assert(system.Ipv6Enabled && system.Encryption[system.Id + "/IPv4"].Length == 0, "Strict mode remained after switching off");
            engine.Change(false, new string[0]); Original();
        });
        Test("Backup failure prevents IPv6 and DoH writes", () => {
            system.CaptureDoh = true; store.FailSave = true;
            Fail(() => engine.Change(true, Smart, "https://geohide.ru/dns-query"));
            Assert(system.BindingWrites == 0 && system.Writes == 0, "Wrote before snapshot");
        });
        Test("Unsupported Windows refuses DoH without writes", () => {
            Fail(() => engine.Change(true, Smart, "https://geohide.ru/dns-query"));
            Assert(system.BindingWrites == 0 && system.Writes == 0 && store.Json == null, "Unsupported system modified");
        });
        Test("Legacy backup gains reversible encryption metadata before enabling DoH", () => {
            engine.Change(true, Smart);
            system.CaptureDoh = true;
            engine.Change(true, Smart, "https://geohide.ru/dns-query");
            Assert(store.Load()[0].Families.All(f => f.DohCaptured), "Legacy snapshot not upgraded");
            engine.Change(false, new string[0]); Original();
            Assert(system.Encryption[system.Id + "/IPv4"].Length == 0 && system.Ipv6Enabled, "Legacy restoration left encryption active");
        });
        Test("DoH failure restores the original IPv6 binding", () => {
            system.CaptureDoh = true; system.FailWrites.Add(1);
            Fail(() => engine.Change(true, Smart, "https://geohide.ru/dns-query"));
            Assert(system.Ipv6Enabled && store.Json == null, "IPv6 not rolled back"); Original();
        });
        Test("Failed IPv6 restoration retains the snapshot", () => {
            system.CaptureDoh = true; system.FailBinding = true;
            Fail(() => engine.Change(true, Smart, "https://geohide.ru/dns-query"));
            Assert(store.Json != null && system.Writes == 0, "Recovery snapshot lost");
        });
        Test("Encrypted provider switch preserves the first snapshot", () => {
            system.CaptureDoh = true;
            engine.Change(true, Smart, "https://geohide.ru/dns-query");
            engine.Change(true, new[] { "143.20.64.55", "31.58.216.201" }, "https://dns.bezmezhau.com/dns-query");
            Assert(store.Load()[0].Ipv6Binding == true, "IPv6 original overwritten");
            engine.Change(false, new string[0]); Original(); Assert(system.Ipv6Enabled, "IPv6 original lost");
        });
        Test("DoH native layout and strict flags are valid", () => {
            Assert(Marshal.SizeOf(typeof(EncryptedDns.Settings3)) == (IntPtr.Size == 8 ? 112 : 72), "Wrong DNS settings v3 size");
            Assert(Marshal.SizeOf(typeof(EncryptedDns.Property)) == (IntPtr.Size == 8 ? 24 : 16), "Wrong DNS property size");
            Assert(EncryptedDns.Strict(2,"https://geohide.ru/dns-query").All(d => d.Flags == 2), "Fallback bit set");
            var invalid = new FamilyBackup {Family="IPv4", Manual=true, Servers=Smart, DohCaptured=true,Doh=new[]{new DohConfig{Index=3,Flags=2,Template="https://example.com"}}};
            Fail(() => NativeDnsSystem.ValidateFamily(invalid));
        });
        Test("Missing old adapter does not prevent restoring the new network adapter", () => {
            engine.Change(true, Smart); var old = system.All[0]; system.All.Clear();
            var newId = Guid.NewGuid(); system.Add(newId); engine.Change(true, Smart);
            engine.Change(false, new string[0]);
            Assert(system.Values[newId + "/IPv4"][0] == "192.0.2.53", "New network not restored");
            Assert(store.Load().Count == 1 && engine.DeferredAdapters.Count == 1, "Wrong pending recovery");
            system.All.Add(old); engine.Change(false, new string[0]); Original();
            Assert(store.Json == null && engine.DeferredAdapters.Count == 0, "Deferred restoration did not complete");
        });
        Test("Apply error rolls back", () => {
            system.FailWrites.Add(1); Fail(() => engine.Change(true, Smart)); Original();
            Assert(store.Json == null, "Stale backup");
        });
        Test("Failed rollback retains backup for retry", () => {
            system.FailWrites.UnionWith(new[] { 1, 2 }); Fail(() => engine.Change(true, Smart));
            Assert(store.Json != null, "Backup lost"); engine.Change(false, new string[0]); Original();
        });
        Test("Backup storage failure blocks modifications", () => {
            store.FailSave = true; Fail(() => engine.Change(true, Smart)); Assert(system.Writes == 0, "Wrote before backup");
        });
        Test("Switch from Xbox removes its previous IPv6 DNS", () => {
            system.Values[system.Id + "/IPv6"] = new[] { "2001:db8::53" };
            engine.Change(true, Smart.Concat(new[] { "2a00:ab00:1233:26::50", "2a00:ab00:1233:26::51" }).ToArray());
            engine.Change(true, Smart);
            Assert(system.Values[system.Id + "/IPv6"].SequenceEqual(new[] { "2001:db8::53" }), "Old Xbox IPv6 remains");
        });
        Test("IPv6 is preserved for IPv4-only provider", () => {
            system.Values[system.Id + "/IPv6"] = new[] { "2001:db8::53" };
            engine.Change(true, Smart); Assert(system.Values[system.Id + "/IPv6"][0] == "2001:db8::53", "IPv6 changed");
            engine.Change(false, new string[0]);
        });
        Test("Dual-stack provider restores both families", () => {
            system.Values[system.Id + "/IPv6"] = new[] { "2001:db8::53" };
            engine.Change(true, new[] { "111.88.96.54", "2a00:ab00:1233:26::50" });
            Assert(system.Values[system.Id + "/IPv6"][0] == "2a00:ab00:1233:26::50", "IPv6 not applied");
            engine.Change(false, new string[0]); Original();
            Assert(system.Values[system.Id + "/IPv6"][0] == "2001:db8::53", "IPv6 not restored");
        });
        Test("Partial error on second adapter rolls back first", () => {
            system.Add(Guid.NewGuid()); system.FailWrites.Add(2);
            Fail(() => engine.Change(true, Smart)); Original(); Assert(store.Json == null, "Rollback incomplete");
        });
        Test("Corrupt backup fails closed", () => {
            store.Json = "{broken"; Fail(() => engine.Change(true, Smart)); Assert(system.Writes == 0, "Corrupt backup ignored");
        });
        Test("Legacy PowerShell JSON remains compatible", () => {
            store.Json = "{\"Version\":1,\"Adapters\":[{\"Guid\":\"{00000000-0000-0000-0000-000000000001}\",\"Families\":[{\"Family\":\"IPv4\",\"Manual\":false,\"Servers\":[]},{\"Family\":\"IPv6\",\"Manual\":true,\"Servers\":[\"2001:db8::53\"]}]}]}";
            engine.Change(false, new string[0]);
            Assert(system.Values[system.Id + "/IPv4"].Length == 0, "Legacy automatic mode lost");
            Assert(system.Values[system.Id + "/IPv6"][0] == "2001:db8::53", "Legacy manual DNS lost");
        });
        Test("Invalid address family is rejected before modifications", () => {
            store.Json = "{\"Version\":1,\"Adapters\":[{\"Guid\":\"00000000-0000-0000-0000-000000000001\",\"Families\":[{\"Family\":\"IPv4\",\"Manual\":true,\"Servers\":[\"2001:db8::53\"]}]}]}";
            Fail(() => engine.Change(false, new string[0])); Assert(system.Writes == 0, "Invalid backup applied");
        });
        Test("Unsupported OS fails before writing backup or DNS", () => {
            system.Unsupported = true; Fail(() => engine.Change(true, Smart));
            Assert(system.Writes == 0 && store.Json == null, "Unsupported OS modified");
        });
        Test("Explicit reset changes known DNS only, preserving unrelated adapter and IPv6", () => {
            system.Values[system.Id + "/IPv4"] = Smart;
            system.Values[system.Id + "/IPv6"] = new[] { "2001:db8::53" };
            var other = Guid.NewGuid(); system.Add(other);
            engine.ResetKnown(Smart);
            Assert(system.Values[system.Id + "/IPv4"].Length == 0, "Known DNS not reset");
            Assert(system.Values[system.Id + "/IPv6"][0] == "2001:db8::53", "Custom IPv6 changed");
            Assert(system.Values[other + "/IPv4"][0] == "192.0.2.53" && system.Writes == 1 && store.Json == null, "Unrelated state changed");
        });
        Test("Mixed known and custom DNS cannot be reset", () => {
            system.Values[system.Id + "/IPv4"] = new[] { Smart[0], "192.0.2.53" };
            Fail(() => engine.ResetKnown(Smart)); Assert(system.Writes == 0 && store.Json == null, "Mixed DNS modified");
        });
        Test("No known manual DNS makes no changes", () => {
            Fail(() => engine.ResetKnown(Smart)); Assert(system.Writes == 0 && store.Json == null, "Unknown DNS modified");
        });
        Test("Reset honors a backup created since the UI check", () => {
            engine.Change(true, Smart); engine.ResetKnown(Smart); Original();
            Assert(store.Json == null, "Original backup not restored");
        });
        Test("Reset storage failure prevents DNS writes", () => {
            system.Values[system.Id + "/IPv4"] = Smart; store.FailSave = true;
            Fail(() => engine.ResetKnown(Smart)); Assert(system.Writes == 0, "Reset before durable backup");
        });
        Test("Missing old adapter backups cannot mask unbacked active Comss", () => {
            var old1=Guid.NewGuid();var old2=Guid.NewGuid();
            store.Save(new List<AdapterBackup>{
                new AdapterBackup{Guid=old1.ToString("B"),Ipv6Binding=true,Families=new[]{new FamilyBackup{Family="IPv4",Servers=new string[0]}}},
                new AdapterBackup{Guid=old2.ToString("B"),Ipv6Binding=true,Families=new[]{new FamilyBackup{Family="IPv4",Servers=new string[0]}}}
            });
            string saved=store.Json;system.Values[system.Id+"/IPv4"]=Smart;system.Values[system.Id+"/IPv6"]=new[]{"2001:db8::53"};
            Assert(engine.NeedsKnownReset(Smart)&&system.Writes==0,"Unbacked active adapter was hidden by unrelated copies");
            engine.ResetKnown(Smart);
            Assert(system.Values[system.Id+"/IPv4"].Length==0,"Current Comss remained set");
            Assert(system.Values[system.Id+"/IPv6"].SequenceEqual(new[]{"2001:db8::53"})&&system.BindingWrites==0,"Unrelated IPv6 or its binding changed");
            Assert(store.Json==saved&&engine.DeferredAdapters.Count==2,"Missing adapter copies were deleted or changed");
            Assert(!engine.NeedsKnownReset(Smart),"Completed reset still requests another reset");
        });
        Test("Mixed backed and unbacked adapters restore or reset independently", () => {
            engine.Change(true,Smart);var fresh=Guid.NewGuid();system.Add(fresh);system.Values[fresh+"/IPv4"]=Smart;
            Assert(engine.NeedsKnownReset(Smart),"New adapter incorrectly covered by another GUID");
            engine.ResetKnown(Smart);Original();
            Assert(system.Values[fresh+"/IPv4"].Length==0&&store.Json==null,"Unbacked adapter not reset or covered original not restored");
        });
        Test("Known-reset failure preserves old missing copies while rolling back current DNS", () => {
            var missing=Guid.NewGuid();store.Save(new List<AdapterBackup>{new AdapterBackup{Guid=missing.ToString("B"),Families=new[]{new FamilyBackup{Family="IPv4",Servers=new string[0]}}}});
            string saved=store.Json;system.Values[system.Id+"/IPv4"]=Smart;system.FailWrites.Add(1);
            Fail(()=>engine.ResetKnown(Smart));
            Assert(system.Values[system.Id+"/IPv4"].SequenceEqual(Smart)&&store.Json==saved,"Rollback lost current DNS or an unrelated old copy");
        });
        Test("Reset and rollback failure keep both old and current recovery data", () => {
            var missing=Guid.NewGuid();store.Save(new List<AdapterBackup>{new AdapterBackup{Guid=missing.ToString("B"),Families=new[]{new FamilyBackup{Family="IPv4",Servers=new string[0]}}}});
            system.Values[system.Id+"/IPv4"]=Smart;system.FailWrites.UnionWith(new[]{1,2});Fail(()=>engine.ResetKnown(Smart));
            Assert(store.Load().Count==2&&store.Load().Any(r=>new Guid(r.Guid)==missing)&&store.Load().Any(r=>new Guid(r.Guid)==system.Id),"Failure discarded recovery data");
        });
        Test("Durable combined snapshot failure blocks writes with old missing copies", () => {
            var missing=Guid.NewGuid();store.Save(new List<AdapterBackup>{new AdapterBackup{Guid=missing.ToString("B"),Families=new[]{new FamilyBackup{Family="IPv4",Servers=new string[0]}}}});
            string saved=store.Json;system.Values[system.Id+"/IPv4"]=Smart;store.FailSave=true;Fail(()=>engine.ResetKnown(Smart));
            Assert(system.Writes==0&&store.Json==saved,"DNS changed before the combined recovery snapshot");
        });
        Test("Explicit unbacked reset clears known DoH without guessing the IPv6 binding", () => {
            system.CaptureDoh=true;system.Ipv6Enabled=false;system.Values[system.Id+"/IPv4"]=Dns.V4[5];
            system.Encryption[system.Id+"/IPv4"]=EncryptedDns.Strict(2,EncryptedDns.Templates[5]);engine.ResetKnown(Dns.KnownServers);
            Assert(system.Values[system.Id+"/IPv4"].Length==0&&system.Encryption[system.Id+"/IPv4"].Length==0,"Known DoH settings survived explicit reset");
            Assert(system.BindingWrites==0&&!system.Ipv6Enabled,"An unknown original IPv6 binding was guessed");
        });
        Test("Custom encryption on known addresses is not treated as app-owned DNS", () => {
            system.CaptureDoh=true;system.Values[system.Id+"/IPv4"]=Smart;
            system.Encryption[system.Id+"/IPv4"]=EncryptedDns.Strict(2,"https://custom.example/dns-query");
            Assert(!engine.NeedsKnownReset(Smart),"Custom encryption selected for reset");Fail(()=>engine.ResetKnown(Smart));Assert(system.Writes==0,"Custom encryption modified");
        });
        Test("Profile DNS is excluded from unbacked reset", () => {
            system.ProfilePresent=true;system.Values[system.Id+"/IPv4"]=Smart;
            Assert(!engine.NeedsKnownReset(Smart),"Profile settings selected for reset");Fail(()=>engine.ResetKnown(Smart));Assert(system.Writes==0,"Profile settings modified");
        });
        Test("Partial reset failure rolls back changed adapters", () => {
            var other = Guid.NewGuid(); system.Add(other);
            system.Values[system.Id + "/IPv4"] = Smart; system.Values[other + "/IPv4"] = Smart;
            system.FailWrites.Add(2); Fail(() => engine.ResetKnown(Smart));
            Assert(system.Values[system.Id + "/IPv4"].SequenceEqual(Smart) && system.Values[other + "/IPv4"].SequenceEqual(Smart), "Reset rollback lost DNS");
            Assert(store.Json == null, "Rollback backup not cleared");
        });
        Test("Reset failure and rollback failure keep backup for retry", () => {
            system.Values[system.Id + "/IPv4"] = Smart; system.FailWrites.UnionWith(new[] { 1, 2 });
            Fail(() => engine.ResetKnown(Smart)); Assert(store.Json != null, "Recovery backup lost");
            engine.Change(false, new string[0]); Assert(system.Values[system.Id + "/IPv4"].SequenceEqual(Smart), "Retry lost DNS");
        });
        Test("Known IPv6 addresses are compared canonically", () => {
            system.Values[system.Id + "/IPv6"] = new[] { "2a00:ab00:1233:0026:0:0:0:50" };
            engine.ResetKnown(new[] { "2a00:ab00:1233:26::50" }); Original();
            Assert(system.Values[system.Id + "/IPv6"].Length == 0 && system.Writes == 1, "Canonical IPv6 reset failed");
        });
        Test("Native structure and read-only API smoke check", () => {
            Assert(Marshal.SizeOf(typeof(NativeDnsSystem.Settings)) == (IntPtr.Size == 8 ? 64 : 48), "Invalid native structure size");
            Assert(Marshal.OffsetOf(typeof(NativeDnsSystem.Settings), "NameServer").ToInt32() == (IntPtr.Size == 8 ? 24 : 20), "Invalid native structure offset");
            var native = new NativeDnsSystem(); native.EnsureSupported();
            var adapter = native.Adapters(true).First();
            var settings = new NativeDnsSystem.Settings { Version = 1 };
            uint error = NativeDnsSystem.GetInterfaceDnsSettings(adapter.Id, ref settings);
            Assert(error == 0, "Native DNS read failed: " + error);
            NativeDnsSystem.FreeInterfaceDnsSettings(ref settings);
            if (adapter.V4) NativeDnsSystem.ValidateFamily(native.Read(adapter.Id, "IPv4"));
            if (adapter.V6) NativeDnsSystem.ValidateFamily(native.Read(adapter.Id, "IPv6"));
        });
        Console.WriteLine("All " + passed + " tests passed. No real DNS or backup registry changes made.");
    }
}
