using System;
using System.IO;
using System.Linq;
using HbAi.XboxDns;

sealed class FakeBrowserProcess : IBrowserProcess
{
    internal bool IsExited, Window = true, AcceptClose = true, Disposed;
    internal int CloseCalls;
    public bool Exited { get { return IsExited; } }
    public bool HasWindow { get { return Window; } }
    public bool CloseWindow() { CloseCalls++; return AcceptClose; }
    public void Dispose() { Disposed = true; }
}
sealed class FakeBrowserSystem : IBrowserSystem
{
    internal FakeBrowserProcess[] Processes = new[] { new FakeBrowserProcess() };
    internal FakeBrowserProcess Respawn;
    internal bool Elevated, OpenFails;
    internal bool CaptureFails;
    internal BrowserLaunch[] Plan = new[] { new BrowserLaunch { Arguments = "--profile-directory=\"Profile 2\"" } }, Launched;
    internal int Queries, Opens, Waits, ExitAfter = 1;
    public void EnsureOrdinaryUser() { if (Elevated) throw new InvalidOperationException("Elevated launch blocked"); }
    public IBrowserProcess[] Running(BrowserChoice choice)
    {
        Queries++;
        return Queries == 1 ? Processes : Respawn == null ? new IBrowserProcess[0] : new[] { Respawn };
    }
    public BrowserLaunch[] Capture(BrowserChoice browser, IBrowserProcess[] windows)
    {
        if (Processes.Any(p => p.CloseCalls > 0)) throw new Exception("Profiles read after close");
        if (CaptureFails) throw new IOException("Cannot capture profiles");
        return Plan;
    }
    public void Open(BrowserChoice browser, BrowserLaunch[] launches) { Launched = launches; Opens++; if (OpenFails) throw new IOException("Cannot start browser"); }
    public void Wait()
    {
        Waits++;
        if (ExitAfter > 0 && Waits >= ExitAfter) foreach (var process in Processes) process.IsExited = true;
    }
}
static class BrowserTests
{
    static int passed;
    static FakeBrowserSystem system;
    static readonly BrowserChoice Choice = new BrowserChoice { Name = "Google Chrome", Exe = @"C:\Browsers\Chrome\chrome.exe" };
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Fail(Action action) { bool failed = false; try { action(); } catch { failed = true; } Assert(failed, "Expected failure"); }
    static void Test(string name, Action action) { system = new FakeBrowserSystem(); action(); passed++; Console.WriteLine("PASS " + name); }
    static void Main()
    {
        Test("Normal exit precedes relaunch", () => {
            BrowserRestart.Run(Choice, system);
            Assert(system.Processes[0].CloseCalls == 1 && system.Waits == 1 && system.Opens == 1 && system.Processes[0].Disposed, "Incorrect restart order");
            Assert(object.ReferenceEquals(system.Plan, system.Launched), "Captured profile not used for relaunch");
        });
        Test("Restart-only mode leaves a stopped application stopped", () => {
            system.Processes = new FakeBrowserProcess[0];
            BrowserRestart.Run(new BrowserChoice {Name="Antigravity",Exe=@"C:\Apps\Antigravity.exe"}, system, true);
            Assert(system.Opens == 0 && system.Launched == null, "Stopped Antigravity was opened");
        });
        Test("Antigravity restart-only mode closes before relaunch", () => {
            BrowserRestart.Run(new BrowserChoice {Name="Antigravity",Exe=@"C:\Apps\Antigravity.exe"}, system, true);
            Assert(system.Processes[0].CloseCalls == 1 && system.Processes[0].IsExited && system.Opens == 1, "Restart order wrong");
        });
        Test("Antigravity waiting for save is not relaunched", () => {
            system.ExitAfter = 0;
            Fail(() => BrowserRestart.Run(new BrowserChoice {Name="Antigravity",Exe=@"C:\Apps\Antigravity.exe"}, system, true));
            Assert(system.Opens == 0 && !system.Processes[0].IsExited, "Application forced or duplicated");
        });
        Test("Antigravity restart arguments preserve settings without copying arbitrary flags", () => {
            var plan = AntigravityRestart.Capture(new[] {"\"C:\\Apps\\Antigravity.exe\" --user-data-dir=\"C:\\Profiles\\My AG\" --extensions-dir=C:\\Extensions --inspect=1234 --disable-web-security https://example.test"}, path => true);
            Assert(plan.Length == 1 && plan[0].Arguments.Contains("--user-data-dir=\"C:\\Profiles\\My AG\"") && plan[0].Arguments.Contains("--extensions-dir=\"C:\\Extensions\""), "Settings lost");
            Assert(!plan[0].Arguments.Contains("inspect") && !plan[0].Arguments.Contains("disable-web-security") && !plan[0].Arguments.Contains("example.test"), "Arbitrary launch flags retained");
        });
        Test("Default Antigravity profile is reopened without new arguments", () => {
            var plan=AntigravityRestart.Capture(new[]{"C:\\Apps\\Antigravity.exe", "C:\\Apps\\Antigravity.exe"},path=>true);
            Assert(plan.Length==1 && plan[0].Arguments=="", "Default profile altered or duplicated");
        });
        Test("Invalid Antigravity settings and renderer processes are refused before close", () => {
            foreach(var command in new[]{"C:\\Apps\\Antigravity.exe --user-data-dir=relative", "C:\\Apps\\Antigravity.exe --user-data-dir=\\\\server\\profile", "C:\\Apps\\Antigravity.exe --type=renderer"})
                Fail(()=>AntigravityRestart.Capture(new[]{command},path=>true));
            Fail(()=>AntigravityRestart.Capture(new[]{"C:\\Apps\\Antigravity.exe --user-data-dir=C:\\Missing"},path=>false));
        });
        Test("Automatic restart requires confirmed activation of the requested provider", () => {
            var status=new AgStatus{Checked=true,Complete=true,Provider=0};
            Assert(AntigravityRestart.Ready(true,false,null,status,0),"Valid activation rejected");
            Assert(!AntigravityRestart.Ready(false,false,null,status,0) && !AntigravityRestart.Ready(true,true,null,status,0) && !AntigravityRestart.Ready(true,false,"UAC cancelled",status,0) && !AntigravityRestart.Ready(true,false,null,status,1),"Failed or unrelated action restarted app");
            status.Complete=false;Assert(!AntigravityRestart.Ready(true,false,null,status,0),"Partial activation restarted app");
            status.Complete=true;status.Error="Read failed";Assert(!AntigravityRestart.Ready(true,false,null,status,0),"Unverified activation restarted app");
        });
        Test("Unidentified profile prevents closing browser", () => {
            system.CaptureFails = true; Fail(() => BrowserRestart.Run(Choice, system));
            Assert(system.Processes[0].CloseCalls == 0 && system.Opens == 0 && system.Processes[0].Disposed, "Browser closed without profile");
        });
        Test("All browser window processes receive graceful close", () => {
            system.Processes = new[] { new FakeBrowserProcess(), new FakeBrowserProcess() };
            BrowserRestart.Run(Choice, system); Assert(system.Processes.All(p => p.CloseCalls == 1 && p.Disposed), "Window process missed");
        });
        Test("Canceled close does not relaunch", () => {
            system.ExitAfter = 0; Fail(() => BrowserRestart.Run(Choice, system));
            Assert(system.Waits == 150 && system.Opens == 0 && system.Processes[0].Disposed, "Canceled close bypassed");
        });
        Test("Close rejection does not relaunch", () => {
            system.Processes[0].AcceptClose = false; Fail(() => BrowserRestart.Run(Choice, system));
            Assert(system.Opens == 0 && system.Waits == 0, "Rejected close ignored");
        });
        Test("Yandex background root is asked to exit before relaunch", () => {
            system.Processes[0].Window = false;
            BrowserRestart.Run(new BrowserChoice { Name = "Яндекс Браузер", Exe = @"C:\Yandex\browser.exe" }, system);
            Assert(system.Processes[0].CloseCalls == 1 && system.Opens == 1, "Background Yandex root was not restarted");
        });
        Test("Background-only browser is preserved", () => {
            system.Processes[0].Window = false; Fail(() => BrowserRestart.Run(Choice, system));
            Assert(system.Opens == 0 && system.Processes[0].CloseCalls == 0, "Background process affected");
        });
        Test("Respawn before launch blocks duplicate start", () => {
            system.Respawn = new FakeBrowserProcess(); Fail(() => BrowserRestart.Run(Choice, system));
            Assert(system.Opens == 0 && system.Respawn.Disposed, "Respawn not handled");
        });
        Test("Elevated context is blocked before closing anything", () => {
            system.Elevated = true; Fail(() => BrowserRestart.Run(Choice, system));
            Assert(system.Queries == 0 && system.Opens == 0 && system.Processes[0].CloseCalls == 0, "Elevated browser restart attempted");
        });
        Test("Stopped browser opens without closing processes", () => {
            system.Processes = new FakeBrowserProcess[0]; BrowserRestart.Run(Choice, system); Assert(system.Opens == 1 && system.Waits == 0, "Stopped browser not started");
        });
        Test("Launch failure releases process handles", () => {
            system.OpenFails = true; Fail(() => BrowserRestart.Run(Choice, system)); Assert(system.Processes[0].Disposed, "Process handle leaked");
        });
        Test("Registry arguments cannot enter executable path", () => {
            Assert(BrowserCatalog.ExeFromCommand("\"C:\\Program Files\\Chrome\\chrome.exe\" --anything https://example.test") == @"C:\Program Files\Chrome\chrome.exe", "Quoted executable not isolated");
            Assert(BrowserCatalog.ExeFromCommand(@"C:\Program Files\Chrome\chrome.exe --anything") == @"C:\Program Files\Chrome\chrome.exe", "Arguments not removed");
            Assert(BrowserCatalog.ExeFromCommand(@"\\server\share\chrome.exe") == null && BrowserCatalog.ExeFromCommand("chrome.exe") == null, "Remote or relative path accepted");
        });
        Test("Only recognized browsers are offered", () => {
            Assert(BrowserCatalog.NameFor(@"C:\Windows\cmd.exe") == null, "Non-browser accepted");
            Assert(BrowserCatalog.NameFor(@"C:\OtherApp\browser.exe") == null, "Unknown browser.exe accepted");
            Assert(BrowserCatalog.NameFor(@"C:\Yandex\browser.exe") == "Яндекс Браузер", "Yandex not recognized");
        });
        Test("Process matching is bounded by installation path", () => {
            Assert(BrowserCatalog.Matches(Choice, Choice.Exe), "Exact executable rejected");
            Assert(!BrowserCatalog.Matches(Choice, @"C:\Other\chrome.exe"), "Other installation accepted");
            var opera = new BrowserChoice { Name = "Opera", Exe = @"C:\Apps\Opera\launcher.exe" };
            Assert(BrowserCatalog.Matches(opera, @"C:\Apps\Opera\100\opera.exe"), "Opera version child rejected");
            Assert(!BrowserCatalog.Matches(opera, @"C:\Apps\OperaOther\100\opera.exe"), "Directory boundary escaped");
        });
        Test("Installed browser discovery is read-only", () => {
            var choices = BrowserCatalog.Find(); Assert(choices.All(b => File.Exists(b.Exe)), "Nonexistent browser offered");
            Console.WriteLine("Detected: " + string.Join(", ", choices.Select(b => b.Name)));
        });
        Console.WriteLine("All " + passed + " checks passed. No real browsers closed or launched.");
    }
}
