using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HbAi.XboxDns;

sealed class CleanupFakeSystem : IAgCleanupSystem
{
    internal readonly List<AgRule> Rules = new List<AgRule>(), Policies = new List<AgRule>();
    internal int Removes, Flushes, FailRemove;
    internal bool Recreate, FailFlush;
    internal bool BackupReady;
    static AgRule Copy(AgRule r) { return new AgRule {Id=r.Id,Comment=r.Comment,DisplayName=r.DisplayName,Names=r.Names.ToArray(),Servers=r.Servers.ToArray()}; }
    public AgRule[] Read() { return Rules.Select(Copy).ToArray(); }
    public AgRule[] Organization() { return Policies.Select(Copy).ToArray(); }
    public void Remove(AgRule rule)
    {
        if (!BackupReady) throw new Exception("Deletion before backup");
        Removes++;
        if (Removes == FailRemove) throw new IOException("Simulated deletion failure");
        Rules.RemoveAll(r => r.Id == rule.Id);
        if (Recreate) Rules.Add(new AgRule { Id=Guid.NewGuid().ToString("B"),Comment=rule.Comment,DisplayName=rule.DisplayName,Names=rule.Names.ToArray(),Servers=rule.Servers.ToArray() });
    }
    public void Flush() { Flushes++; if (FailFlush) throw new IOException("Cache failure"); }
}
sealed class CleanupFakeBackup : IAgCleanupBackup
{
    internal bool Fail, Empty;
    internal int Saves;
    internal AgRule[] Saved;
    internal Action AfterSave;
    internal CleanupFakeSystem System;
    public string Save(AgRule[] rules) {
        Saves++; if (Fail) throw new IOException("Backup failed");
        Saved=rules; System.BackupReady=true;
        if (AfterSave != null) AfterSave();
        return Empty ? "" : @"C:\mock-backup";
    }
}
static class CleanupTests
{
    static int passed;
    static int skipped;
    sealed class SkipTest : Exception { }
    static CleanupFakeSystem system;
    static CleanupFakeBackup backup;
    static AgRule Rule(string name, string tag = "AG_UNLOCKER_NRPT_V2") {
        return new AgRule {Id=Guid.NewGuid().ToString("B"),Comment=tag,DisplayName="AG Unlocker",Names=new[]{name},Servers=new[]{"127.0.0.53","192.0.2.53"}};
    }
    static void Assert(bool value,string text) {if(!value)throw new Exception(text);}
    static string Fail(Action action) {try {action();} catch(IOException e) {return e.Message;} throw new Exception("Expected failure");}
    static void Test(string name, Action action) {
        system=new CleanupFakeSystem();backup=new CleanupFakeBackup {System=system};
        try {action();} catch(SkipTest) {skipped++;Console.WriteLine("SKIP "+name);return;}
        passed++;Console.WriteLine("PASS "+name);
    }
    static void Both() {foreach(string name in AntigravityDns.Domains)system.Rules.Add(Rule(name));}
    static string Run() {return AgCleanup.Change(system,backup);}
    static void Main()
    {
        Test("Only exact old unlocker rules are removed after backup",()=>{
            Both();var other=Rule("unrelated.example");system.Rules.Add(other);
            var result=Run();Assert(result==@"C:\mock-backup" && backup.Saved.Length==2 && system.Removes==2 && system.Flushes==1,"Wrong operation");
            Assert(system.Rules.Count==1 && system.Rules[0]==other,"Unrelated rule touched");
            Assert(backup.Saved.All(r=>r.Servers.SequenceEqual(new[]{"127.0.0.53","192.0.2.53"})),"Original resolvers lost");
        });
        Test("Backup failure prevents all removals",()=>{Both();backup.Fail=true;Fail(()=>Run());Assert(system.Removes==0 && system.Rules.Count==2,"Removal without backup");});
        Test("Unconfirmed backup prevents all removals",()=>{Both();backup.Empty=true;Fail(()=>Run());Assert(system.Removes==0,"Empty confirmation accepted");});
        Test("Unknown unlocker tags are preserved",()=>{system.Rules.Add(Rule(AntigravityDns.Domains[0],"Other app"));Fail(()=>Run());Assert(system.Removes==0 && backup.Saves==0,"Unknown rule touched");});
        Test("Broad and multi-domain rules are not cleaned",()=>{
            foreach(var names in new[]{new[]{".googleapis.com"},AntigravityDns.Domains}) {
                system.Rules.Clear();var r=Rule(AntigravityDns.Domains[0]);r.Names=names;system.Rules.Add(r);
                Fail(()=>Run());Assert(system.Removes==0,"Broad rule removed");
            }
        });
        Test("Duplicate or overlapping rules stop cleanup",()=>{
            Both();system.Rules.Add(Rule(".googleapis.com"));Fail(()=>Run());Assert(backup.Saves==0,"Overlap ignored");
            system.Rules.Clear();Both();system.Rules.Add(Rule(AntigravityDns.Domains[0]));Fail(()=>Run());Assert(system.Removes==0,"Duplicate removed");
        });
        Test("Organization policy is never removed",()=>{Both();system.Policies.Add(Rule(".googleapis.com"));Fail(()=>Run());Assert(backup.Saves==0 && system.Removes==0,"Organization conflict ignored");});
        Test("Unrelated organization policy does not block narrow cleanup",()=>{Both();system.Policies.Add(Rule(".organization.example"));Run();Assert(system.Removes==2 && system.Policies.Count==1,"Unrelated policy changed");});
        Test("Completed cleanup is safely repeatable",()=>{Both();Run();Run();Assert(backup.Saves==1 && system.Removes==2,"Retry created changes");});
        Test("Changed rule after backup is preserved",()=>{
            Both();backup.AfterSave=()=>system.Rules[0].Servers=new[]{"192.0.2.99"};
            var message=Fail(()=>Run());Assert(system.Removes==0 && message.Contains("mock-backup"),"Changed rule removed or backup hidden");
        });
        Test("Failure on first deletion retains rules and recovery copy",()=>{Both();system.FailRemove=1;var message=Fail(()=>Run());Assert(system.Rules.Count==2 && backup.Saved.Length==2 && message.Contains("mock-backup"),"Recovery lost");});
        Test("Partial deletion reports error and preserves full original backup",()=>{Both();system.FailRemove=2;var message=Fail(()=>Run());Assert(system.Rules.Count==1 && backup.Saved.Length==2 && message.Contains("mock-backup"),"Partial state hidden");});
        Test("An unlocker recreating rules is detected",()=>{Both();system.Recreate=true;var message=Fail(()=>Run());Assert(message.Contains("вернул") && backup.Saved.Length==2,"Recreated rules reported as removed");});
        Test("Cache failure keeps recovery information",()=>{Both();system.FailFlush=true;var message=Fail(()=>Run());Assert(system.Rules.Count==0 && message.Contains("mock-backup"),"Cache failure hidden");});
        Test("Our own rules are not deleted",()=>{
            var r=Rule(AntigravityDns.Domains[0],AntigravityDns.Tag);r.DisplayName=AntigravityDns.Title;system.Rules.Add(r);
            Run();Assert(system.Removes==0 && backup.Saves==0,"Our rule cleaned as foreign");
        });
        Test("Invalid IDs cannot reach export or removal",()=>{var r=Rule(AntigravityDns.Domains[0]);r.Id="invalid";system.Rules.Add(r);Fail(()=>Run());Assert(system.Removes==0 && backup.Saves==0,"Invalid ID accepted");});
        Test("Real Windows exports preserve the full existing rule without deleting it",()=>{
            var windows = new WindowsAgRules();
            var candidates = windows.Read().Where(AgCleanup.IsLegacy).Take(2).ToArray();
            if(candidates.Length==0) throw new SkipTest();
            string root=Path.Combine(Path.GetTempPath(),"SmartDNS-CleanupExport-"+Guid.NewGuid().ToString("N"));
            string directory=new AgCleanupBackup(root).Save(candidates);
            Assert(Directory.GetFiles(directory,"*.reg").Length==candidates.Length,"Export incomplete");
            foreach(var original in candidates) {
                string file=Path.Combine(directory,new Guid(original.Id).ToString("B")+".reg");
                var content=File.ReadAllText(file);
                Assert(content.Contains("GenericDNSServers") && content.Contains("\"Name\"=hex(7):") && content.Contains(original.Comment) && content.Contains(original.Servers[0]),"Original fields missing");
            }
            var after=windows.Read();
            Assert(candidates.All(c=>after.Any(r=>AgCleanup.Same(r,c))),"Read-only export changed a rule");
            Console.WriteLine("Read-only backup exported to isolated test folder: "+directory);
        });
        Console.WriteLine("All "+passed+" cleanup tests passed; "+skipped+" skipped. No real rules removed. A live export needs existing legacy rules.");
    }
}
