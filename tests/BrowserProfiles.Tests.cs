using System;
using System.IO;
using System.Linq;
using HbAi.XboxDns;

static class ProfileTests
{
    static int passed;
    static readonly BrowserChoice Chrome = new BrowserChoice { Name = "Google Chrome", Exe = @"C:\Chrome\chrome.exe" };
    static readonly BrowserChoice Firefox = new BrowserChoice { Name = "Mozilla Firefox", Exe = @"C:\Firefox\firefox.exe" };
    const string Json = "{\"profile\":{\"last_active_profiles\":[\"Profile 3\",\"Default\"],\"last_used\":\"Profile 3\"}}";
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Fails(Action action) { bool failed = false; try { action(); } catch { failed = true; } Assert(failed, "Expected failure"); }
    static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static BrowserLaunch[] Capture(string command, string json)
    {
        return BrowserProfiles.Capture(Chrome, new[] { command }, p => json, p => true, p => false);
    }
    static void Main()
    {
        Test("Active profiles override the profile that originally started the browser", () => {
            Assert(BrowserProfiles.ActiveChromiumProfiles(Json, "Profile 1").SequenceEqual(new[] { "Profile 3", "Default" }), "Closed initial profile was reopened");
        });
        Test("Only profile parameters are replayed", () => {
            var plan = Capture("chrome.exe --user-data-dir=\"C:\\Profiles\\Work Space\" --profile-directory=\"Profile 1\" --unsafe-treat-insecure-origin-as-secure=https://example.test --remote-debugging-port=9999 https://example.test", Json);
            Assert(plan.Length == 2 && plan.All(p => p.Arguments.Contains("--restore-last-session") && p.Arguments.Contains("C:\\Profiles\\Work Space")), "Profile path or session restore lost");
            Assert(plan.All(p => !p.Arguments.Contains("unsafe") && !p.Arguments.Contains("remote-debugging") && !p.Arguments.Contains("https://")), "Unrelated switches or URLs replayed");
        });
        Test("Windows argument quoting handles spaces, slashes and quotes", () => {
            foreach (string value in new[] { @"C:\Profiles\Work Space", "C:\\Profiles\\", "a\"b", "Profile 3", "" })
                Assert(BrowserProfiles.Split("browser.exe " + BrowserProfiles.Quote(value))[1] == value, "Argument did not round-trip");
        });
        Test("Separate-value flags and equals flags are both supported", () => {
            var args = BrowserProfiles.Split("chrome.exe --user-data-dir \"C:\\A B\" --profile-directory=\"Profile 2\"");
            Assert(BrowserProfiles.Option(args, "--user-data-dir") == @"C:\A B" && BrowserProfiles.Option(args, "--profile-directory") == "Profile 2", "Profile flags lost");
        });
        Test("Conflicting profile switches are refused", () => {
            Fails(() => BrowserProfiles.Option(BrowserProfiles.Split("chrome.exe --user-data-dir=C:\\One --user-data-dir=C:\\Two"), "--user-data-dir"));
            Fails(() => Capture("chrome.exe --user-data-dir=", Json));
        });
        Test("Explicit profile is fallback if metadata has no active list", () => {
            Assert(BrowserProfiles.ActiveChromiumProfiles("{}", "Profile 7").Single() == "Profile 7", "Explicit profile lost");
            Assert(BrowserProfiles.ActiveChromiumProfiles("{\"profile\":{\"last_used\":\"Default\"}}", null).Single() == "Default", "Last used fallback lost");
        });
        Test("Unknown, guest or path-escaping profiles are refused", () => {
            Fails(() => BrowserProfiles.ActiveChromiumProfiles("{}", null));
            foreach (string name in new[] { "..", "../outside", "C:\\Other", "Guest Profile", "System Profile" })
                Fails(() => BrowserProfiles.ActiveChromiumProfiles("{}", name));
        });
        Test("Private windows are refused before closing", () => {
            foreach (string arg in new[] { "--incognito", "--inprivate", "--guest", "--incognito=true" })
                Fails(() => Capture("chrome.exe --user-data-dir=C:\\Profiles " + arg, Json));
        });
        Test("Missing directories or metadata prevent restart", () => {
            Fails(() => BrowserProfiles.Capture(Chrome, new[] { "chrome.exe --user-data-dir=C:\\Missing" }, p => Json, p => false, p => false));
            Fails(() => BrowserProfiles.Capture(Chrome, new[] { "chrome.exe --user-data-dir=C:\\Missing" }, p => { throw new IOException(); }, p => true, p => false));
            Fails(() => Capture("chrome.exe --user-data-dir=\\\\server\\share", Json));
        });
        Test("Multiple running instances deduplicate identical profiles", () => {
            var plan = BrowserProfiles.Capture(Chrome, new[] { "chrome.exe --user-data-dir=C:\\Profiles", "chrome.exe --user-data-dir=C:\\Profiles" }, p => Json, p => true, p => false);
            Assert(plan.Length == 2, "Profiles duplicated");
        });
        Test("Distinct user-data roots stay distinct", () => {
            var plan = BrowserProfiles.Capture(Chrome, new[] { "chrome.exe --user-data-dir=C:\\One", "chrome.exe --user-data-dir=C:\\Two" }, p => Json, p => true, p => false);
            Assert(plan.Length == 4, "Separate profile roots merged");
        });
        Test("Firefox explicit profile is preserved without arbitrary flags", () => {
            var plan = BrowserProfiles.Capture(Firefox, new[] { "firefox.exe -profile \"C:\\Profiles\\Firefox Work\" -some-other-switch" }, p => "", p => true, p => false);
            Assert(plan.Single().Arguments == "-profile \"C:\\Profiles\\Firefox Work\"", "Firefox path lost");
        });
        Test("Firefox named profiles resolve through profiles.ini", () => {
            const string ini = "[Profile0]\nName=Work\nIsRelative=1\nPath=Profiles/work\n[Profile1]\nName=Personal\nIsRelative=0\nPath=C:\\Profiles\\Home";
            var paths = BrowserProfiles.FirefoxPaths(ini, @"C:\FirefoxData", "Work");
            Assert(paths.Single() == @"C:\FirefoxData\Profiles\work", "Named profile not resolved");
            var plan = BrowserProfiles.Capture(Firefox, new[] { "firefox.exe -P Work" }, p => ini, p => true, p => false);
            Assert(plan.Length == 1 && plan[0].Arguments.Contains("Profiles\\work"), "Named Firefox profile not captured");
        });
        Test("Firefox only includes profiles currently locked by a running instance", () => {
            const string ini = "[Profile0]\nName=Work\nIsRelative=0\nPath=C:\\Profiles\\Work\n[Profile1]\nName=Other\nIsRelative=0\nPath=C:\\Profiles\\Other";
            var plan = BrowserProfiles.Capture(Firefox, new[] { "firefox.exe" }, p => ini, p => true, p => p.Contains("Work"));
            Assert(plan.Length == 1 && plan[0].Arguments.Contains("Work") && !plan[0].Arguments.Contains("Other"), "Inactive Firefox profile selected");
            Fails(() => BrowserProfiles.Capture(Firefox, new[] { "firefox.exe" }, p => ini, p => true, p => false));
        });
        Test("Live Chrome profile capture is read-only", () => {
            var chrome = BrowserCatalog.Find().FirstOrDefault(b => b.Name == "Google Chrome");
            if (chrome == null) return;
            var system = new WindowsBrowserSystem(); var processes = system.Running(chrome);
            try
            {
                var windows = processes.Where(p => !p.Exited && p.HasWindow).ToArray();
                if (windows.Length == 0) return;
                var plan = system.Capture(chrome, windows);
                Assert(plan.Length > 0 && plan.All(l => l.Arguments.Contains("--profile-directory=")), "Running Chrome profile not captured");
                Console.WriteLine("Read-only capture: " + plan.Length + " Chrome profile(s); paths and command lines not logged.");
            }
            finally { foreach (var process in processes) process.Dispose(); }
        });
        Console.WriteLine("All " + passed + " profile checks passed; no browsers closed or started, no profile files changed.");
    }
}
