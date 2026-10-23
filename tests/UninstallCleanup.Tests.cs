using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using HbAi.XboxDns;
using Microsoft.Win32;

sealed class FakeUninstallCleanup : IUninstallCleanup
{
    internal bool Needed,Remain;internal string Error;internal bool Interactive;internal int Restores,Clears;
    public bool NeedsNetworkCleanup(){return Needed;}
    public string RestoreNetwork(bool interactive){Restores++;Interactive=interactive;if(Error==null&&!Remain)Needed=false;return Error;}
    public void ClearUserData(){if(Needed)throw new Exception("Recovery data deleted before network restore");Clears++;}
}
static class UninstallTests
{
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static bool Fails(Action action){try{action();return false;}catch(IOException){return true;}catch(InvalidOperationException){return true;}}
    static void Main()
    {
        var cleanup=new FakeUninstallCleanup{Needed=true};
        Check(UninstallCleanup.Run(cleanup,true)==null&&cleanup.Restores==1&&cleanup.Clears==1&&cleanup.Interactive,"Restore succeeds before preferences and cache are removed");
        cleanup=new FakeUninstallCleanup();Check(UninstallCleanup.Run(cleanup,false)==null&&cleanup.Restores==0&&cleanup.Clears==1,"Untouched installation needs no administrator prompt or network writes");
        foreach(string error in new[]{"UAC cancelled","unavailable adapter","corrupt backup","restore failed"}) {
            cleanup=new FakeUninstallCleanup{Needed=true,Error=error};
            Check(UninstallCleanup.Run(cleanup,true)==error&&cleanup.Clears==0&&cleanup.Needed,"Recovery failure preserves user data and blocks removal: "+error);
        }
        cleanup=new FakeUninstallCleanup{Needed=true,Remain=true};
        Check(UninstallCleanup.Run(cleanup,true)!=null&&cleanup.Clears==0,"A success reply with pending recovery cannot authorize uninstall");
        bool interactive;
        Check(UninstallCleanup.TryUiLevel(new[]{"--prepare-uninstall","5"},out interactive)&&interactive,"Full installer UI allows the ordinary UAC flow");
        Check(UninstallCleanup.TryUiLevel(new[]{"--prepare-uninstall","2"},out interactive)&&!interactive,"Quiet installer never displays a new UAC dialog");
        foreach(var args in new[]{new string[0],new[]{"--prepare-uninstall"},new[]{"--prepare-uninstall","0"},new[]{"--prepare-uninstall","6"},new[]{"--prepare-uninstall","5","C:\\"},new[]{"--prepare-uninstall","powershell"}})
            Check(!UninstallCleanup.TryUiLevel(args,out interactive),"Invalid uninstall arguments cannot reach cleanup");
        var request=new DnsRequest{Enable=false,Set=4,Token=Guid.NewGuid(),Parent=123};DnsRequest parsed;
        Check(DnsRequest.TryParse(request.Arguments.Split(' '),out parsed)&&!parsed.Enable&&parsed.Set==4,"Network-only uninstall operation uses the authenticated elevation protocol");
        Check(!DnsRequest.TryParse(request.Arguments.Replace("uninstall-restore 0","uninstall-restore 1").Split(' '),out parsed),"Elevation protocol cannot carry arbitrary cleanup targets");

        var system=new FakeSystem{CaptureDoh=true};var store=new MemoryStore();var ag=new MemoryAgTakeoverStore();var rules=new FakeAgRules();
        var engine=new DnsSettingsEngine(system,store);engine.Change(true,EncryptedDns.ServersFor(5),EncryptedDns.Templates[5]);
        rules.Rules.Add(FakeAgRules.Own(AntigravityDns.Domains[0]));
        var unrelated=new AgRule{Id=Guid.NewGuid().ToString("B"),Names=new[]{"other.example"},Servers=new[]{"192.0.2.1"},Comment="another app",DisplayName="external"};rules.Rules.Add(unrelated);
        UninstallCleanup.RestoreNetwork(engine,store,ag,rules);
        Check(system.Values[system.Id+"/IPv4"].SequenceEqual(new[]{"192.0.2.53","192.0.2.54"})&&system.Ipv6Enabled&&store.Load().Count==0,"Uninstall restores original DNS, DoH and IPv6 and clears only completed backup");
        Check(rules.Rules.Count==1&&rules.Rules[0]==unrelated,"Only owned Antigravity rules are removed");
        int writes=system.Writes;UninstallCleanup.RestoreNetwork(engine,store,ag,rules);
        Check(system.Writes==writes,"Repeated uninstall preparation is idempotent and never resets third-party DNS");
        rules=new FakeAgRules();ag=new MemoryAgTakeoverStore();var originalAg=new[]{"127.0.0.53","45.155.204.190"};
        foreach(string domain in AntigravityDns.Domains)rules.Rules.Add(new AgRule{Id=Guid.NewGuid().ToString("B"),Names=new[]{domain},Comment="AG_UNLOCKER_NRPT_V2",DisplayName="AG Unlocker original",Servers=originalAg.ToArray()});
        AgTakeover.Enable(rules,ag);system=new FakeSystem();store=new MemoryStore();engine=new DnsSettingsEngine(system,store);engine.Change(true,Dns.V4[0]);
        UninstallCleanup.RestoreNetwork(engine,store,ag,rules);
        Check(ag.Load().Length==0&&store.Load().Count==0&&rules.Rules.All(r=>r.Servers.SequenceEqual(originalAg)&&r.Comment=="AG_UNLOCKER_NRPT_V2"),"Previously replaced Antigravity rules regain their original resolver lists and metadata");
        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();engine=new DnsSettingsEngine(system,store);
        engine.Change(true,EncryptedDns.ServersFor(5),EncryptedDns.Templates[5]);system.All.Clear();
        Check(Fails(()=>UninstallCleanup.RestoreNetwork(engine,store,new MemoryAgTakeoverStore(),new FakeAgRules()))&&store.Load().Count==1&&!system.Ipv6Enabled,"Unavailable adapter blocks removal while keeping its DNS/IPv6 recovery data");
        system=new FakeSystem();store=new MemoryStore();engine=new DnsSettingsEngine(system,store);engine.Change(true,Dns.V4[0]);system.FailWrites.Add(system.Writes+1);
        Check(Fails(()=>UninstallCleanup.RestoreNetwork(engine,store,new MemoryAgTakeoverStore(),new FakeAgRules()))&&store.Load().Count==1,"Failed DNS write leaves a backup for the next removal attempt");
        system=new FakeSystem();store=new MemoryStore{Json="corrupt"};engine=new DnsSettingsEngine(system,store);rules=new FakeAgRules();rules.Rules.Add(FakeAgRules.Own(AntigravityDns.Domains[0]));bool corrupt=false;
        try{UninstallCleanup.RestoreNetwork(engine,store,new MemoryAgTakeoverStore(),rules);}catch(Exception){corrupt=true;}
        Check(corrupt&&system.Writes==0&&rules.Removes==0,"Invalid DNS backup is found before any DNS or rule writes");

        string temp=Path.Combine(Path.GetTempPath(),"SmartDNS-uninstall-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        string cache=Path.Combine(temp,"Updates"),outside=Path.Combine(temp,"user-report.txt");Directory.CreateDirectory(Path.Combine(cache,"nested"));File.WriteAllText(Path.Combine(cache,"nested","download.part"),"cache");File.WriteAllText(outside,"user report");
        try {
            UninstallCleanup.CleanCache(temp,cache);
            Check(!Directory.Exists(cache)&&File.ReadAllText(outside)=="user report","Fixed cache tree is deleted while exported user documents remain");
            Check(Fails(()=>UninstallCleanup.CleanCache(temp,temp))&&Fails(()=>UninstallCleanup.CleanCache(temp,Path.Combine(temp,"..","other"))),"Parent and escaping cache paths are refused before deletion");
            UninstallCleanup.CleanCache(temp,cache);Check(true,"Missing cache is a successful idempotent cleanup");
        }finally{File.Delete(outside);Directory.Delete(temp,false);}
        string keyPath=@"Software\SmartDNS-UninstallTests-"+Guid.NewGuid().ToString("N");
        try {
            using(var key=Registry.CurrentUser.CreateSubKey(keyPath)) {
                foreach(string name in UninstallCleanup.PreferenceValues)key.SetValue(name,"test");
                key.SetValue("UnrelatedValue","keep");using(var child=key.CreateSubKey("Msi"))child.SetValue("InstallLocation","keep");
            }
            UninstallCleanup.ClearPreferences(Registry.CurrentUser,keyPath);
            using(var key=Registry.CurrentUser.OpenSubKey(keyPath)) {
                Check(UninstallCleanup.PreferenceValues.All(name=>key.GetValue(name)==null)&&Convert.ToString(key.GetValue("UnrelatedValue"))=="keep"&&key.GetSubKeyNames().Contains("Msi"),"Preference cleanup preserves MSI ownership and unknown values/subkeys");
            }
            Registry.CurrentUser.DeleteSubKeyTree(keyPath);
            using(var key=Registry.CurrentUser.CreateSubKey(keyPath))key.SetValue("Theme","Light");
            UninstallCleanup.ClearPreferences(Registry.CurrentUser,keyPath);
            using(var key=Registry.CurrentUser.OpenSubKey(keyPath))Check(key==null,"Empty preferences key is removed");
        }finally{Registry.CurrentUser.DeleteSubKeyTree(keyPath,false);}
        Console.WriteLine("All "+passed+" uninstall checks passed. No real DNS, backup, application settings or installation changed.");
    }
}
