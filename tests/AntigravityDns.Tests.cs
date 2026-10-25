using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HbAi.XboxDns;

sealed class FakeAgRules : IAgRules
{
    internal List<AgRule> Rules = new List<AgRule>(), Policy = new List<AgRule>();
    internal int Adds, Removes, FailAdd, FailRemove;
    internal int Sets;
    internal HashSet<int> FailSets = new HashSet<int>();
    internal HashSet<int> FailAfterSets = new HashSet<int>();
    internal bool SilentAdd, FailFlushOnce, HideLocalFromPolicy;
    public AgRule[] Read() { return Rules.ToArray(); }
    public AgRule[] Effective() { return (HideLocalFromPolicy ? Policy : Rules.Concat(Policy)).ToArray(); }
    public void Add(string name)
    {
        Adds++;
        if (Adds == FailAdd) throw new IOException("Simulated add failure");
        if (!SilentAdd) Rules.Add(Own(name));
    }
    internal static AgRule Own(string name)
    {
        return new AgRule { Id = Guid.NewGuid().ToString("B"), Comment = AntigravityDns.Tag,
            DisplayName = AntigravityDns.Title, Names = new[] { name }, Servers = AntigravityDns.Servers.ToArray() };
    }
    public void Remove(string id)
    {
        Removes++;
        if (Removes == FailRemove) throw new IOException("Simulated removal failure");
        Rules.RemoveAll(r => r.Id == id);
    }
    public void Flush() { if (FailFlushOnce) { FailFlushOnce = false; throw new IOException("Simulated cache failure"); } }
    public void SetServers(AgRule expected, string[] servers)
    {
        Sets++;
        if (FailSets.Contains(Sets)) throw new IOException("Simulated set failure");
        var actual = Rules.Single(r => r.Id == expected.Id);
        actual.Servers = servers.ToArray();
        if (FailAfterSets.Contains(Sets)) throw new IOException("Simulated verification failure after write");
    }
}
static class AntigravityTests
{
    static int passed;
    static FakeAgRules system;
    static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    static void Fails(Action action) { bool failed = false; try { action(); } catch (IOException) { failed = true; } catch (InvalidDataException) { failed = true; } Assert(failed, "Expected error"); }
    static void Test(string name, Action action) { system = new FakeAgRules(); action(); passed++; Console.WriteLine("PASS " + name); }
    static AgRule Foreign(string pattern)
    {
        return new AgRule { Id = Guid.NewGuid().ToString("B"), Comment = "Another app", DisplayName = "Another app",
            Names = new[] { pattern }, Servers = new[] { "192.0.2.53" } };
    }
    static AgRule WindowsUpdateRule()
    {
        return new AgRule { Id="DNS_RESILIENCY_slscr.update.microsoft.com", Names=new[]{"slscr.update.microsoft.com"},
            Servers=new[]{"162.159.36.2"}, Local=true, Version=2, ConfigOptions=8 };
    }
    static void Main()
    {
        Test("Opt-in creates exactly two FQDN rules with expected resolvers", () => {
            AntigravityDns.Change(system, true);
            Assert(system.Rules.Count == 2 && system.Rules.All(AntigravityDns.Owned), "Wrong scope");
            Assert(AntigravityDns.Status(system).Complete, "Incomplete state");
        });
        Test("Enabling twice is idempotent", () => {
            AntigravityDns.Change(system, true); AntigravityDns.Change(system, true);
            Assert(system.Adds == 2 && system.Rules.Count == 2, "Duplicate rules");
        });
        Test("Disabling touches only our rules", () => {
            AntigravityDns.Change(system, true); var other = Foreign(".googleapis.com"); system.Rules.Add(other);
            AntigravityDns.Change(system, false);
            Assert(system.Rules.Count == 1 && system.Rules[0] == other, "Foreign policy removed");
        });
        Test("Foreign exact rules are not overwritten", () => {
            system.Rules.Add(Foreign(AntigravityDns.Domains[0]));
            Fails(() => AntigravityDns.Change(system, true)); Assert(system.Adds == 0 && system.Removes == 0, "Foreign rule touched");
        });
        Test("Foreign suffix and root policies block activation", () => {
            foreach (string pattern in new[] { ".googleapis.com", "*.googleapis.com", ".", "*", "cloudcode-*" })
            {
                system.Rules.Clear(); system.Rules.Add(Foreign(pattern));
                Fails(() => AntigravityDns.Change(system, true));
            }
            Assert(system.Adds == 0, "Broad conflict ignored");
        });
        Test("Effective organization policy blocks activation", () => {
            system.Policy.Add(Foreign(AntigravityDns.Domains[1]));
            Fails(() => AntigravityDns.Change(system, true)); Assert(system.Adds == 0, "Effective policy ignored");
        });
        Test("Unrelated policy remains unchanged", () => {
            var other = Foreign(".example.com"); system.Rules.Add(other);
            AntigravityDns.Change(system, true); Assert(system.Rules.Contains(other), "Unrelated policy lost");
        });
        Test("Failure on second rule rolls back the first", () => {
            system.FailAdd = 2; Fails(() => AntigravityDns.Change(system, true));
            Assert(system.Rules.Count == 0, "Partial rules not rolled back");
        });
        Test("Rollback retains rules that existed before the attempt", () => {
            var original = FakeAgRules.Own(AntigravityDns.Domains[0]); system.Rules.Add(original);
            system.FailAdd = 1; Fails(() => AntigravityDns.Change(system, true));
            Assert(system.Rules.Count == 1 && system.Rules[0] == original, "Original rule removed");
        });
        Test("Failed rollback is visible and can be disabled later", () => {
            system.FailAdd = 2; system.FailRemove = 1; Fails(() => AntigravityDns.Change(system, true));
            Assert(AntigravityDns.Status(system).Checked && !AntigravityDns.Status(system).Complete, "Partial state hidden");
            AntigravityDns.Change(system, false); Assert(system.Rules.Count == 0, "Retry failed");
        });
        Test("Partial removal can be retried", () => {
            AntigravityDns.Change(system, true); system.FailRemove = 2;
            Fails(() => AntigravityDns.Change(system, false)); AntigravityDns.Change(system, false);
            Assert(system.Rules.Count == 0, "Removal retry failed");
        });
        Test("Unconfirmed creation reports failure", () => {
            system.SilentAdd = true; Fails(() => AntigravityDns.Change(system, true));
        });
        Test("Cache failure rolls back new rules", () => {
            system.FailFlushOnce = true; Fails(() => AntigravityDns.Change(system, true));
            Assert(system.Rules.Count == 0, "Flush failure left rules");
        });
        Test("Rules not effective in Windows are rolled back", () => {
            system.HideLocalFromPolicy = true; Fails(() => AntigravityDns.Change(system, true));
            Assert(system.Rules.Count == 0, "Ineffective rules retained");
        });
        Test("Modified ownership metadata is preserved", () => {
            var other = FakeAgRules.Own("unrelated.example.com"); system.Rules.Add(other);
            AntigravityDns.Change(system, false); Assert(system.Rules.Contains(other), "Wrong namespace removed");
        });
        Test("Changed resolver and duplicate own rules require explicit disable", () => {
            var own = FakeAgRules.Own(AntigravityDns.Domains[0]); own.Servers = new[] { "192.0.2.53" }; system.Rules.Add(own);
            Fails(() => AntigravityDns.Change(system, true)); Assert(system.Adds == 0, "Changed rule overwritten");
            AntigravityDns.Change(system, false); AntigravityDns.Change(system, true);
            system.Rules.Add(FakeAgRules.Own(AntigravityDns.Domains[0]));
            Fails(() => AntigravityDns.Change(system, true));
        });
        Test("Case-insensitive scope comparison is bounded", () => {
            Assert(AntigravityDns.Matches("CLOUDCODE-PA.GOOGLEAPIS.COM.", AntigravityDns.Domains[0]), "FQDN mismatch");
            Assert(!AntigravityDns.Matches(".googleapis.com", "cloudcode-pa.googleapis.com.evil.example"), "Suffix escaped");
        });
        Test("Windows CIM scalar and array formats are handled", () => {
            Assert(WindowsAgRules.Strings("159.194.200.33;193.233.112.67").SequenceEqual(AntigravityDns.Servers), "Scalar resolver list lost");
            Assert(WindowsAgRules.Strings(new[] { "example.com" })[0] == "example.com", "Array format lost");
            Assert(WindowsAgRules.Strings(null).Length == 0, "Missing list not empty");
        });
        Test("Read-only Windows CIM smoke check", () => {
            var native = new WindowsAgRules(); var rules = native.Read(); native.Effective();
            var status = AntigravityDns.GetStatus(); Assert(status.Error == null, "CIM read failed: " + status.Error);
            Console.WriteLine("Read-only local state: own=" + rules.Count(AntigravityDns.Owned) + ", external=" + status.External);
        });
        Test("Failed WMI read uses the CIM fallback without hiding foreign rules", () => {
            var expected = new[] { Foreign(AntigravityDns.Domains[0]) };
            var result = WindowsAgRules.ReadWithFallback(() => { throw new System.IO.IOException("Missing WMI response"); }, () => expected);
            Assert(result.Length == 1 && object.ReferenceEquals(result,expected),"Foreign policy lost in fallback");
        });
        Test("A successful empty result does not invoke fallback", () => {
            bool fallback = false;
            var result = WindowsAgRules.ReadWithFallback(() => new AgRule[0], () => {fallback=true;return new AgRule[0];});
            Assert(result.Length == 0 && !fallback,"Empty success treated as failure");
        });
        Test("Both read failures remain an error rather than an empty policy", () => {
            Fails(() => WindowsAgRules.ReadWithFallback(() => {throw new System.IO.IOException("WMI failed");}, () => {throw new System.IO.IOException("CIM failed");}));
        });
        Test("Native CIM reader matches WMI under production startup hardening", () => {
            ProcessSecurity.Initialize();
            var expected = new WindowsAgRules();
            var rules = NrptCimReader.Read(false); var policy=NrptCimReader.Read(true);
            Assert(rules.Length == expected.Read().Length && policy.Length == expected.Effective().Length,"Fallback policy differs");
            Assert(policy.All(r => r.Names != null && r.Servers != null),"CIM output incomplete");
            Console.WriteLine("Read-only CIM fallback: rules="+rules.Length+", effective="+policy.Length);
        });
        Test("Registry parser preserves foreign rule identity and resolver order", () => {
            var fields = new Dictionary<string, object> { {"Name", new[] {AntigravityDns.Domains[0]}}, {"GenericDNSServers", "127.0.0.53;83.220.169.155"}, {"Comment", "AG_UNLOCKER_NRPT_V2"}, {"DisplayName", "Foreign unlocker"} };
            var id = Guid.NewGuid().ToString("B");
            var rule = NrptRegistryReader.Parse(id, key => fields.ContainsKey(key) ? fields[key] : null);
            Assert(rule.Id == id && rule.Comment == "AG_UNLOCKER_NRPT_V2" && rule.DisplayName == "Foreign unlocker", "Identity lost");
            Assert(rule.Servers.SequenceEqual(new[] {"127.0.0.53","83.220.169.155"}) && rule.Names.SequenceEqual(new[] {AntigravityDns.Domains[0]}), "Rule changed");
        });
        Test("Registry parser rejects missing, scalar and empty namespaces", () => {
            foreach (object names in new object[] { null, "example.com", new string[0], new[] {" "} })
                Fails(() => NrptRegistryReader.Parse(Guid.NewGuid().ToString("B"), key => key == "Name" ? names : null));
        });
        Test("Registry parser rejects malformed server values", () => {
            Fails(() => NrptRegistryReader.Parse(Guid.NewGuid().ToString("B"), key => key == "Name" ? (object)new[] {"example.com"} : key == "GenericDNSServers" ? (object)123 : null));
        });
        Test("Registry parser preserves a policy without generic DNS servers", () => {
            var rule = NrptRegistryReader.Parse(Guid.NewGuid().ToString("B"), key => key == "Name" ? new[] {".example.com"} : null);
            Assert(rule.Servers.Length == 0 && rule.Names[0] == ".example.com", "Policy discarded");
        });
        Test("Named foreign registry rules remain visible and cannot become owned rules", () => {
            var rule=NrptRegistryReader.Parse("DNS_RESILIENCY_service.example", key => key=="Name" ? (object)new[]{"service.example"} : key=="GenericDNSServers" ? "192.0.2.53" : null);
            Assert(rule.Id=="DNS_RESILIENCY_service.example"&&rule.Names[0]=="service.example"&&rule.Servers[0]=="192.0.2.53","Foreign named rule discarded");
            Assert(EncryptedDns.PolicyConflict(new[]{rule})!=null,"Named policy bypasses encryption conflict check");
            rule.Comment=AntigravityDns.Tag;rule.DisplayName=AntigravityDns.Title;rule.Names=new[]{AntigravityDns.Domains[0]};
            Assert(!AntigravityDns.Owned(rule)&&!AgCleanup.IsLegacy(rule),"Named foreign rule accepted for removal");
            system.Rules.Add(rule);Fails(()=>AntigravityDns.Change(system,true));
            AntigravityDns.Change(system,false);Assert(system.Rules.Contains(rule),"Named foreign rule removed");
        });
        Test("Registry parser rejects empty and unsafe rule IDs", () => {
            foreach(string id in new[]{null,""," ","parent\\child","bad\nkey",new string('x',256)})
                Fails(() => NrptRegistryReader.Parse(id, key => new[] {"example.com"}));
        });
        Test("Registry fallback remains available when both providers fail", () => {
            var expected = new[] {Foreign(".example.com")};
            var actual = WindowsAgRules.ReadWithFallback(() => WindowsAgRules.ReadWithFallback(
                () => {throw new IOException("WMI unavailable");}, () => {throw new IOException("CIM unavailable");}), () => expected);
            Assert(object.ReferenceEquals(actual, expected), "Third independent read lost");
        });
        Test("Registry store reader includes both sources, distinguishes absence from corruption", () => {
            string path = @"Software\KodVsego\SmartDNS\Tests\" + Guid.NewGuid().ToString("N");
            try
            {
                using (var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path))
                {
                    Assert(NrptRegistryReader.ReadStores(root, new[] {"Local", "Policy"}).Length == 0, "Absent stores are not empty");
                    foreach (string store in new[] {"Local", "Policy"})
                        using (var row = root.CreateSubKey(store + "\\" + Guid.NewGuid().ToString("B")))
                        {
                            row.SetValue("Name", new[] {store == "Local" ? "local.example" : ".organization.example"});
                            row.SetValue("GenericDNSServers", "192.0.2.53");
                        }
                    Assert(NrptRegistryReader.ReadStores(root, new[] {"Local"}).Length == 1, "Local read included policy");
                    Assert(NrptRegistryReader.ReadStores(root, new[] {"Local", "Policy"}).Length == 2, "Organization policy omitted");
                    using (root.CreateSubKey("Policy\\" + Guid.NewGuid().ToString("B"))) { }
                    Fails(() => NrptRegistryReader.ReadStores(root, new[] {"Local", "Policy"}));
                }
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, false); }
        });
        Test("Real registry read preserves all local rule namespaces seen by CIM", () => {
            var registry = NrptRegistryReader.Read(false);
            var cim = NrptCimReader.Read(false);
            Assert(registry.Length == cim.Length && cim.All(c => registry.Any(r => r.Id == c.Id && r.Names.SequenceEqual(c.Names) && r.Servers.SequenceEqual(c.Servers))), "Persisted rules differ from CIM");
            var all = NrptRegistryReader.Read(true);
            Assert(registry.All(r => all.Any(a => a.Id == r.Id)), "Local store omitted from conflict check");
            Console.WriteLine("Read-only registry: local=" + registry.Length + ", local+policy=" + all.Length);
        });
        Test("Strict DoH reports conflicting domains and allows an empty policy", () => {
            Assert(EncryptedDns.PolicyConflict(new AgRule[0]) == null, "Empty policy blocked");
            var text = EncryptedDns.PolicyConflict(new[] {Foreign(AntigravityDns.Domains[0])});
            Assert(text.Contains(AntigravityDns.Domains[0]) && text.Contains("DoH") && !text.Contains("анлокера"), "Unknown rule misattributed to an unlocker");
        });
        Test("Exact local Windows Update exception permits adapter DoH and remains unchanged", () => {
            var rule=WindowsUpdateRule();
            Assert(EncryptedDns.IsWindowsUpdateException(rule)&&EncryptedDns.PolicyConflict(new[]{rule})==null,"Windows Update falsely blocks adapter DoH");
            Assert(!EncryptedDns.CanKeepAntigravity(new[]{rule},new[]{rule}),"Windows Update triggers Antigravity consent");
            system.Rules.Add(rule);var status=AntigravityDns.Status(system);
            Assert(status.WindowsUpdateException&&!status.Checked&&!status.External,"Windows Update mistaken for Antigravity");
            AntigravityDns.Change(system,false);
            Assert(system.Rules.Count==1&&object.ReferenceEquals(system.Rules[0],rule)&&rule.Servers[0]=="162.159.36.2","Windows Update rule changed or removed");
        });
        Test("Resiliency metadata is read only from the local store with the exact schema", () => {
            var fields=new Dictionary<string,object>{{"Name",new[]{"slscr.update.microsoft.com"}},{"GenericDNSServers","162.159.36.2"},{"Version",2},{"ConfigOptions",8}};
            Func<string,object> value=key=>fields.ContainsKey(key)?fields[key]:null;
            string id="DNS_RESILIENCY_slscr.update.microsoft.com";
            Assert(EncryptedDns.IsWindowsUpdateException(NrptRegistryReader.Parse(id,value,true)),"Local metadata lost");
            Assert(!EncryptedDns.IsWindowsUpdateException(NrptRegistryReader.Parse(id,value)),"Unknown/organization source accepted");
            fields["Version"]="2";
            Assert(!EncryptedDns.IsWindowsUpdateException(NrptRegistryReader.Parse(id,value,true)),"Wrong registry type accepted");
        });
        Test("Similar names, broad policies and incomplete metadata cannot bypass the DoH guard", () => {
            Action<AgRule>[] invalid={r=>r.Local=false,r=>r.Id=Guid.NewGuid().ToString("B"),r=>r.Version=1,r=>r.Version=null,
                r=>r.ConfigOptions=6,r=>r.ConfigOptions=null,r=>r.Names=new[]{".microsoft.com"},r=>r.Names=new[]{"."},
                r=>r.Names=new[]{"slscr.update.microsoft.com",AntigravityDns.Domains[0]},r=>r.Names=AntigravityDns.Domains,
                r=>r.Id="DNS_RESILIENCY_"+AntigravityDns.Domains[0],r=>r.Comment="Another app",r=>r.DisplayName="Another app",
                r=>r.Servers=new string[0],r=>r.Servers=new[]{"invalid"}};
            foreach(var change in invalid) {
                var rule=WindowsUpdateRule();change(rule);
                Assert(!EncryptedDns.IsWindowsUpdateException(rule)&&EncryptedDns.PolicyConflict(new[]{rule})!=null,"Unexpected policy bypassed validation");
            }
        });
        Test("Windows Update exception does not mask a second conflicting policy", () => {
            var systemRule=WindowsUpdateRule();var other=Foreign(AntigravityDns.Domains[0]);
            string message=EncryptedDns.PolicyConflict(new[]{systemRule,other});
            Assert(message.Contains(AntigravityDns.Domains[0])&&!message.Contains("slscr.update.microsoft.com"),"Conflict includes unrelated Windows Update rule");
            Assert(!EncryptedDns.CanKeepAntigravity(new[]{systemRule,other},new[]{systemRule,other}),"Unknown rule accepted by Antigravity consent");
            other.Comment="AG_UNLOCKER_NRPT_V2";
            Assert(EncryptedDns.CanKeepAntigravity(new[]{systemRule,other},new[]{systemRule,other}),"Independent exception blocks known Antigravity consent");
            var organization=WindowsUpdateRule();organization.Local=false;
            Assert(!EncryptedDns.CanKeepAntigravity(new[]{systemRule,other},new[]{systemRule,other,organization}),"Organization policy was ignored");
        });
        Test("Known local Antigravity rules may be preserved without mutation", () => {
            var rules = AntigravityDns.Domains.Select(name => {var r = Foreign(name); r.Comment = "AG_UNLOCKER_NRPT_V2"; return r;}).ToArray();
            Assert(EncryptedDns.CanKeepAntigravity(rules, rules), "Known exact rules refused");
            Assert(EncryptedDns.PolicyConflict(rules) != null, "Ordinary strict activation silently bypassed consent");
            Assert(rules.All(r => r.Servers.SequenceEqual(new[] {"192.0.2.53"})), "Existing settings changed");
        });
        Test("Antigravity consent cannot bypass broad, unknown or organization policies", () => {
            var known = Foreign(AntigravityDns.Domains[0]); known.Comment = "AG_UNLOCKER_NRPT_V2";
            Assert(!EncryptedDns.CanKeepAntigravity(new AgRule[0], new[] {known}), "Organization policy accepted");
            Assert(!EncryptedDns.CanKeepAntigravity(new[] {known}, new[] {known, known}), "Duplicate or organization policy accepted");
            foreach (string name in new[] {".googleapis.com", ".", "other.example"}) {
                var broad = Foreign(name); broad.Comment = "AG_UNLOCKER_NRPT_V2";
                Assert(!EncryptedDns.CanKeepAntigravity(new[] {broad}, new[] {broad}), "Broad policy bypassed");
            }
            var unknown = Foreign(AntigravityDns.Domains[0]);
            Assert(!EncryptedDns.CanKeepAntigravity(new[] {unknown}, new[] {unknown}), "Unknown application bypassed");
            var multi = Foreign(AntigravityDns.Domains[0]); multi.Comment = "AG_UNLOCKER_NRPT_V2"; multi.Names = AntigravityDns.Domains;
            Assert(!EncryptedDns.CanKeepAntigravity(new[] {multi}, new[] {multi}), "Multi-domain rule accepted");
        });
        Test("Changed policy after consent is checked again", () => {
            var before = Foreign(AntigravityDns.Domains[0]); before.Comment = "AG_UNLOCKER_NRPT_V2";
            var after = Foreign(AntigravityDns.Domains[0]); after.Comment = before.Comment;
            Assert(!EncryptedDns.CanKeepAntigravity(new[] {before}, new[] {after}), "Replacement policy accepted as original");
        });
        Test("Missing CIM result is empty only after independent confirmation", () => {
            Assert(NrptCimReader.ConfirmEmpty(() => new AgRule[0]).Length == 0, "Confirmed absence rejected");
            Fails(() => NrptCimReader.ConfirmEmpty(() => new[] {Foreign(".example.com")}));
        });
        Test("Failure of independent empty-policy confirmation remains an error", () => {
            Fails(() => NrptCimReader.ConfirmEmpty(() => {throw new IOException("Registry unavailable");}));
        });
        Test("Each provider applies only its own built-in addresses", () => {
            for (int provider = 0; provider < AntigravityDns.ServerSets.Length; provider++)
                using (AntigravityDns.UseProvider(provider))
                {
                    var mock = new FakeAgRules(); AntigravityDns.Change(mock, true);
                    Assert(mock.Rules.All(r => r.Servers.SequenceEqual(AntigravityDns.ServerSets[provider])), "Wrong provider applied");
                    Assert(AntigravityDns.Status(mock).Provider == provider, "Actual provider misreported");
                    AntigravityDns.Change(mock, false); Assert(mock.Rules.Count == 0, "Provider rules not removed");
                }
            Assert(AntigravityDns.Servers.SequenceEqual(AntigravityDns.ServerSets[0]), "Provider leaked outside operation");
        });
        Test("Existing provider requires disable before changing", () => {
            using (AntigravityDns.UseProvider(0)) AntigravityDns.Change(system, true);
            using (AntigravityDns.UseProvider(1)) Fails(() => AntigravityDns.Change(system, true));
            Assert(AntigravityDns.Status(system).Provider == 0, "Existing provider overwritten");
            using (AntigravityDns.UseProvider(2)) AntigravityDns.Change(system, false);
        });
        Test("Main selector offers MagicDNS and excludes disabled or unsupported Antigravity providers", () => {
            Assert(AntigravityDns.ProviderForMainSet(0) == -1 && AntigravityDns.ProviderForMainSet(1) == -1 && AntigravityDns.ProviderForMainSet(2) == -1 && AntigravityDns.ProviderForMainSet(4) == 3 && AntigravityDns.ProviderForMainSet(6) == 4,
                "Main selector mapping wrong");
            Assert(AntigravityDns.ProviderForMainSet(3) == -1 && AntigravityDns.ProviderForMainSet(5) == -1, "Xbox or encrypted-only DNS accepted for plain rules");
            Assert(AntigravityDns.ProviderNames[4] == "MagicDNS" && AntigravityDns.ServerSets[4].SequenceEqual(Dns.V4[6]), "MagicDNS rule addresses differ from the shared catalog");
            Assert(AntigravityDns.ProviderForMainSet(9)==5&&AntigravityDns.ProviderNames[5]=="Malw DNS"&&AntigravityDns.ServerSets[5].SequenceEqual(Dns.V4[9]),"Appended Malw provider has its own rule slot without shifting historical slots");
            Assert(AntigravityDns.ProviderForMainSet(13)==-1,"Encrypted Cloudflare Gateway must not create plaintext NRPT rules");
            bool rejected = false;
            try { using (AntigravityDns.UseProvider(AntigravityDns.ServerSets.Length)) { } } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Unknown provider accepted");
        });
        Test("Normal and forced GeoHide activations are rejected before accessing Windows", () => {
            foreach(int provider in new[]{0,2}) foreach(bool force in new[]{false,true})
                Assert(AntigravityDns.Run(true,force,provider).Contains("временно недоступен"), "Disabled provider accepted");
        });
        Test("Disabled GeoHide rules remain detectable and removable", () => {
            foreach(int provider in new[]{0,2}) {
                foreach(string domain in AntigravityDns.Domains) {
                    var rule=FakeAgRules.Own(domain);rule.Servers=AntigravityDns.ServerSets[provider].ToArray();system.Rules.Add(rule);
                }
                Assert(AntigravityDns.Status(system).Complete && AntigravityDns.Status(system).Provider==provider,"Disabled provider identity lost");
                AntigravityDns.Change(system,false);Assert(system.Rules.Count==0,"Disabled provider cannot be removed");
            }
        });
        Test("Normal and forced Comss activations are rejected before accessing Windows", () => {
            foreach(bool force in new[]{false,true}) {
                string error=AntigravityDns.Run(true,force,1);
                Assert(error != null && error.Contains("Comss"), "Unsupported activation accepted");
            }
        });
        Test("Existing Comss rules remain detectable and removable", () => {
            foreach(string domain in AntigravityDns.Domains) {
                var rule=FakeAgRules.Own(domain);rule.Servers=AntigravityDns.ServerSets[1].ToArray();system.Rules.Add(rule);
            }
            var status=AntigravityDns.Status(system);
            Assert(status.Checked && status.Complete && status.Provider==1,"Legacy provider identity lost");
            AntigravityDns.Change(system,false);Assert(system.Rules.Count==0,"Legacy Comss rules cannot be disabled");
        });
        Test("An empty mutation response is followed by state verification", () => {
            foreach (string operation in new[] { "Add", "Set", "Remove" }) WindowsAgRules.CheckResult(operation, null);
            Fails(() => WindowsAgRules.CheckResult("Get", null));
        });
        Test("Windows NRPT error codes are never treated as success", () => {
            using (var type = new System.Management.ManagementClass(@"\\.\root\Microsoft\Windows\DNS:PS_DnsClientNrptRule"))
            using (var result = (System.Management.ManagementBaseObject)type.Methods["Set"].OutParameters.Clone())
            {
                result["ReturnValue"] = (uint)5; Fails(() => WindowsAgRules.CheckResult("Set", result));
                result["ReturnValue"] = (uint)0; WindowsAgRules.CheckResult("Set", result);
                result["ReturnValue"] = null; Fails(() => WindowsAgRules.CheckResult("Set", result));
            }
        });
        Console.WriteLine("All " + passed + " checks passed; no real NRPT or adapter DNS changes made.");
    }
}
