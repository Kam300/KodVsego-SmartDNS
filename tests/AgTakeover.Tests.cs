using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using HbAi.XboxDns;

sealed class MemoryAgTakeoverStore : IAgTakeoverStore
{
    internal string Json;
    internal bool FailSave, FailClear;
    public AgSavedRule[] Load()
    {
        if (Json == null) return new AgSavedRule[0];
        var saved = new JavaScriptSerializer().Deserialize<AgSavedState>(Json).Rules;
        RegistryAgTakeoverStore.Validate(saved); return saved;
    }
    public void Save(AgSavedRule[] rules)
    {
        RegistryAgTakeoverStore.Validate(rules);
        if (FailSave || (FailClear && rules.Length == 0)) throw new IOException("Simulated storage failure");
        Json = rules.Length == 0 ? null : new JavaScriptSerializer().Serialize(new AgSavedState { Version = 1, Rules = rules });
    }
}
static class TakeoverTests
{
    static FakeAgRules system;
    static MemoryAgTakeoverStore store;
    static int passed;
    static readonly string[] Original = { "127.0.0.53", "45.155.204.190" };
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Fails(Action action) { bool failed = false; try { action(); } catch (Exception) { failed = true; } Assert(failed, "Expected failure"); }
    static void Test(string name, Action action)
    {
        system = new FakeAgRules(); store = new MemoryAgTakeoverStore();
        foreach (string domain in AntigravityDns.Domains)
            system.Rules.Add(new AgRule { Id = Guid.NewGuid().ToString("B"), Names = new[] { domain },
                Comment = "AG_UNLOCKER_NRPT_V2", DisplayName = "AG Unlocker original", Servers = Original.ToArray() });
        action(); passed++; Console.WriteLine("PASS " + name);
    }
    static void Originals() { Assert(system.Rules.All(r => r.Servers.SequenceEqual(Original)), "Original servers lost"); }
    static void Main()
    {
        Test("Force preserves IDs and metadata, then restores both resolver lists", () => {
            var ids = system.Rules.Select(r => r.Id).ToArray();
            AgTakeover.Enable(system, store);
            Assert(store.Json != null && system.Rules.All(r => r.Servers.SequenceEqual(AntigravityDns.Servers)), "Takeover not applied");
            Assert(system.Rules.Select(r => r.Id).SequenceEqual(ids) && system.Rules.All(r => r.Comment == "AG_UNLOCKER_NRPT_V2" && r.DisplayName == "AG Unlocker original"), "Metadata changed");
            AgTakeover.Restore(system, store); Originals(); Assert(store.Json == null, "Copy not cleared");
        });
        Test("Durable backup failure blocks all changes", () => {
            store.FailSave = true; Fails(() => AgTakeover.Enable(system, store));
            Originals(); Assert(system.Sets == 0 && system.Adds == 0, "Wrote before backup");
        });
        Test("Second-rule write failure restores the first", () => {
            system.FailSets.Add(2); Fails(() => AgTakeover.Enable(system, store));
            Originals(); Assert(store.Json == null, "Rollback copy not cleared");
        });
        Test("Failure after native write is rolled back", () => {
            system.FailAfterSets.Add(1); Fails(() => AgTakeover.Enable(system, store));
            Originals(); Assert(store.Json == null, "Failed write recovery incomplete");
        });
        Test("Rollback failure keeps backup for another attempt", () => {
            system.FailSets.UnionWith(new[] { 2, 3 }); Fails(() => AgTakeover.Enable(system, store));
            Assert(store.Json != null, "Recovery copy lost"); AgTakeover.Restore(system, store); Originals();
        });
        Test("Partial original config creates a missing rule and restores original", () => {
            system.Rules.RemoveAt(1); AgTakeover.Enable(system, store);
            Assert(system.Rules.Count == 2 && system.Rules.Count(AntigravityDns.Owned) == 1, "Missing rule not created");
            AgTakeover.Restore(system, store); AntigravityDns.Change(system, false); Originals();
            Assert(system.Rules.Count == 1, "Original rule lost or our rule left behind");
        });
        Test("Failed creation removes only new rules and restores inherited rule", () => {
            system.Rules.RemoveAt(1); system.FailAdd = 1;
            Fails(() => AgTakeover.Enable(system, store)); Originals(); Assert(store.Json == null && system.Rules.Count == 1, "Partial rollback failed");
        });
        Test("Unknown application and suffix rules are refused", () => {
            system.Rules[0].Comment = "Unknown app";
            Fails(() => AgTakeover.Enable(system, store)); Assert(system.Sets == 0, "Unknown app changed");
            system.Rules[0].Comment = "AG_UNLOCKER_NRPT_V2"; system.Rules[0].Names = new[] { ".googleapis.com" };
            Fails(() => AgTakeover.Enable(system, store)); Assert(system.Sets == 0, "Broad rule changed");
        });
        Test("Multi-domain rules and duplicate rules are refused", () => {
            system.Rules[0].Names = new[] { AntigravityDns.Domains[0], "example.com" };
            Fails(() => AgTakeover.Enable(system, store)); Assert(system.Sets == 0, "Multi-domain rule changed");
            system.Rules[0].Names = new[] { AntigravityDns.Domains[0] }; system.Rules.Add(FakeAgRules.Own(AntigravityDns.Domains[0]));
            Fails(() => AgTakeover.Enable(system, store)); Assert(system.Sets == 0, "Duplicate policy changed");
        });
        Test("Effective organization policy cannot be forced", () => {
            system.Policy.Add(new AgRule { Id = "Organization", Names = new[] { ".googleapis.com" }, Servers = new[] { "192.0.2.53" } });
            Fails(() => AgTakeover.Enable(system, store)); Assert(system.Sets == 0 && store.Json == null, "Organization policy ignored");
        });
        Test("Restore refuses changes made by another application", () => {
            AgTakeover.Enable(system, store); system.Rules[0].Servers = new[] { "192.0.2.53" };
            Fails(() => AgTakeover.Restore(system, store));
            Assert(system.Rules[0].Servers[0] == "192.0.2.53" && store.Load().Length == 1, "External edits overwritten or recovery copy lost");
        });
        Test("Already-restored rules do not need another write", () => {
            AgTakeover.Enable(system, store); system.Rules[0].Servers = Original.ToArray();
            int count = system.Sets; AgTakeover.Restore(system, store); Originals();
            Assert(system.Sets == count + 1 && store.Json == null, "Already original rule rewritten");
        });
        Test("Deleted original rule retains recovery data", () => {
            AgTakeover.Enable(system, store); system.Rules.RemoveAt(0);
            Fails(() => AgTakeover.Restore(system, store)); Assert(store.Load().Length == 1, "Missing rule copy discarded");
        });
        Test("Backup cannot be replaced by a second force operation", () => {
            AgTakeover.Enable(system, store); var json = store.Json;
            Fails(() => AgTakeover.Enable(system, store)); Assert(store.Json == json, "Original backup replaced");
        });
        Test("Backup-clear failure remains recoverable", () => {
            AgTakeover.Enable(system, store); store.FailClear = true;
            Fails(() => AgTakeover.Restore(system, store)); Originals(); Assert(store.Json != null, "Recovery copy discarded");
            store.FailClear = false; AgTakeover.Restore(system, store); Assert(store.Json == null, "Clear retry failed");
        });
        Test("Only a valid narrow snapshot can be restored", () => {
            store.Json = "{\"Version\":1,\"Rules\":[{\"Id\":\"bad\"}]}";
            Fails(() => AgTakeover.Restore(system, store)); Assert(system.Sets == 0, "Invalid snapshot used");
        });
        Test("Force-selected provider restores originals after a new default context", () => {
            using (AntigravityDns.UseProvider(1)) AgTakeover.Enable(system, store);
            Assert(system.Rules.All(r => r.Servers.SequenceEqual(AntigravityDns.ServerSets[1])), "Comss not applied");
            Assert(store.Load().All(s => s.AppliedServers.SequenceEqual(AntigravityDns.ServerSets[1])), "Applied provider not persisted");
            AgTakeover.Restore(system, store); Originals(); Assert(store.Json == null, "Provider-specific restoration incomplete");
        });
        Test("Old GeoHide snapshots without applied-provider field remain restorable", () => {
            AgTakeover.Enable(system, store); var saved = store.Load();
            foreach (var rule in saved) rule.AppliedServers = null;
            store.Save(saved);
            using (AntigravityDns.UseProvider(2)) AgTakeover.Restore(system, store);
            Originals(); Assert(store.Json == null, "Legacy GeoHide restoration failed");
        });
        Test("External switch to a different known provider is preserved", () => {
            using (AntigravityDns.UseProvider(1)) AgTakeover.Enable(system, store);
            system.Rules[0].Servers = AntigravityDns.ServerSets[2].ToArray();
            Fails(() => AgTakeover.Restore(system, store));
            Assert(system.Rules[0].Servers.SequenceEqual(AntigravityDns.ServerSets[2]) && store.Load().Length == 1, "External provider overwritten");
        });
        Console.WriteLine("All " + passed + " takeover tests passed; no real DNS, NRPT or registry writes made.");
    }
}
