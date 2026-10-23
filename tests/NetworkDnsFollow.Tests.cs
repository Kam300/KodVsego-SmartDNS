using System;
using System.Linq;
using HbAi.XboxDns;

static class NetworkFollowTests
{
    static int passed;
    static void Check(bool condition,string name) { if(!condition)throw new Exception(name);passed++;Console.WriteLine("PASS "+name); }
    static DnsFollowSnapshot Snap(string key,bool configured=false,string returned="")
    { return new DnsFollowSnapshot{NetworkKey=key,Configured=configured,RestoreKey=returned}; }
    static void Main()
    {
        var follow=new NetworkDnsFollow();
        Check(follow.Observe(Snap("wifi"),true,false).Action==DnsFollowAction.None,"Opening the app does not enable DNS or restore anything without session intent");
        follow.Enable(6,true,true);follow.Observe(Snap("wifi",true),true,false);
        Check(follow.Observe(Snap("wifi",false),true,false).Action==DnsFollowAction.None,"External DNS edits on the same network are not overwritten");
        Check(follow.Observe(Snap("ethernet"),true,false).Action==DnsFollowAction.None,"New connection settles before applying DNS");
        var plan=follow.Observe(Snap("ethernet"),true,false);
        Check(plan.Action==DnsFollowAction.Apply&&plan.Provider==6&&plan.Encrypted&&plan.KeepAntigravity,"Changing adapters preserves the selected provider, encryption and consent");
        Check(follow.Observe(Snap("ethernet"),true,false).Action==DnsFollowAction.None,"Failure or UAC cancellation cannot prompt again on every timer tick");
        follow.Observe(Snap("unstable"),true,false);follow.Observe(Snap("unstable"),true,false);
        Check(follow.Observe(Snap("ethernet"),true,false).Action==DnsFollowAction.None,"Returning to an already cancelled connection does not repeat elevation");
        follow.Observe(Snap("ethernet",true),true,false);
        Check(follow.Observe(Snap("mobile"),true,true).Action==DnsFollowAction.None,"An active VPN blocks automatic DNS changes");
        Check(follow.Observe(Snap("mobile"),false,false).Action==DnsFollowAction.None,"Offline state retains intent without an automatic write");
        follow.Observe(Snap("mobile"),true,false);
        Check(follow.Observe(Snap("mobile"),true,false).Action==DnsFollowAction.Apply,"A later stable mobile connection is applied once");
        follow.Stop();
        Check(!follow.Enabled&&follow.Observe(Snap("another"),true,false).Action==DnsFollowAction.None,"Manual stop cancels automatic enablement");
        follow.DeferRestore();
        Check(follow.Observe(Snap("wifi",true),true,false).Action==DnsFollowAction.None,"Absent adapters never cause restore prompts");
        Check(follow.Observe(Snap("wifi",true,"returned-guid"),true,true).Action==DnsFollowAction.None,"Returning adapters wait while a VPN is active");
        Check(follow.Observe(Snap("wifi",true,"returned-guid"),false,false).Action==DnsFollowAction.Restore,"An adapter can be restored without internet when it returns");
        Check(follow.Observe(Snap("wifi",true,"returned-guid"),true,false).Action==DnsFollowAction.None,"Deferred restoration is not repeated after cancellation or failure");
        follow.Observe(Snap("wifi",true,"another-guid,returned-guid"),true,false);
        Check(follow.Observe(Snap("wifi",true,"returned-guid"),true,false).Action==DnsFollowAction.None,"Changing the set of returned adapters cannot repeat the same cancelled restore");
        follow.Enable(4,false,false);
        Check(follow.Enabled&&follow.Observe(Snap("wifi",false,"returned-guid"),true,false).Action==DnsFollowAction.None,"Re-enabling DNS cancels old restore intent and debounces the new selection");
        follow.Observe(null,true,false);
        Check(follow.Observe(Snap("wifi"),true,false).Action==DnsFollowAction.None,"A failed snapshot restarts stabilization instead of using stale observations");
        follow.Observe(Snap("wifi"),false,false);
        Check(follow.Observe(Snap("wifi"),true,false).Action==DnsFollowAction.None,"An offline interval restarts stabilization");
        follow.Enable(1,false,false);follow.Observe(Snap("wifi"),true,false);
        Check(follow.Observe(Snap("wifi"),true,false).Action==DnsFollowAction.None,"A retired provider cannot be applied automatically");

        var system=new FakeSystem{CaptureDoh=true};var store=new MemoryStore();var engine=new DnsSettingsEngine(system,store);
        engine.Change(true,EncryptedDns.ServersFor(6),EncryptedDns.Templates[6]);
        var wifi=system.All[0];var original=store.Load().Single();system.All.Clear();
        var ethernet=Guid.NewGuid();system.Add(ethernet);
        var fresh=DnsFollowSnapshot.Capture(system,store,6,true,"ethernet");
        Check(!fresh.Configured&&!fresh.Managed,"A new adapter is detected without claiming unrelated old backups as its ownership");
        engine.Change(true,EncryptedDns.ServersFor(6),EncryptedDns.Templates[6]);
        Check(store.Load().Count==2&&store.Load().Single(r=>r.Guid==original.Guid).Families[0].Servers.SequenceEqual(original.Families[0].Servers),"Moving to a second adapter saves its own original DNS and preserves the first backup");
        var configured=DnsFollowSnapshot.Capture(system,store,6,true,"ethernet");
        Check(configured.Configured&&configured.Managed&&!system.ReadIpv6Binding(ethernet),"New adapter receives strict DoH, an owned backup and the intended IPv6 state");
        engine.Change(false,new string[0]);
        Check(engine.DeferredAdapters.Count==1&&store.Load().Count==1&&system.Values[ethernet+"/IPv4"][0]=="192.0.2.53"&&system.ReadIpv6Binding(ethernet),"Disabling restores the current adapter while retaining the missing adapter backup");
        Check(DnsFollowSnapshot.Capture(system,store,6,true,"ethernet").RestoreKey=="","Only absent backups do not request another elevation");
        system.All.Add(wifi);int before=system.Writes;
        Check(DnsFollowSnapshot.Capture(system,store,6,true,"ethernet").RestoreKey.Length>0,"The original adapter returning makes recovery eligible");
        engine.RestoreReturned();
        Check(store.Json==null&&system.Values[system.Id+"/IPv4"][0]=="192.0.2.53"&&system.Ipv6Enabled&&system.Writes>before,"Returning adapter automatically restores its exact original DNS, encryption and IPv6");

        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();engine=new DnsSettingsEngine(system,store);
        engine.Change(true,EncryptedDns.ServersFor(6),EncryptedDns.Templates[6]);
        system.Values[system.Id+"/IPv4"]=new[]{"8.8.8.8"};system.Encryption[system.Id+"/IPv4"]=new DohConfig[0];before=system.Writes;
        Check(DnsFollowSnapshot.Capture(system,store,6,true,"ethernet").RestoreKey=="","Externally changed DNS cannot request an automatic restore prompt");
        engine.RestoreReturned();
        Check(engine.RestoreSkipped&&store.Json!=null&&system.Writes==before&&system.Values[system.Id+"/IPv4"][0]=="8.8.8.8","Helper rechecks external changes and keeps their settings and backup intact");

        system=new FakeSystem();store=new MemoryStore();engine=new DnsSettingsEngine(system,store);
        engine.Change(true,Dns.V4[4]);
        Check(DnsFollowSnapshot.Capture(system,store,4,false,"wifi").Configured,"Ordinary DNS following also supports adapters without Windows 11 DoH");
        ethernet=Guid.NewGuid();system.Add(ethernet);
        Check(!DnsFollowSnapshot.Capture(system,store,4,false,"wifi+ethernet").Configured,"A second active adapter needs configuration even if Wi-Fi remains connected");
        engine.Change(true,Dns.V4[4]);
        Check(store.Load().Count==2&&DnsFollowSnapshot.Capture(system,store,4,false,"wifi+ethernet").Configured,"The same ordinary provider is applied to both adapters with separate backups");
        engine.Change(false,new string[0]);
        Check(store.Json==null&&system.BindingWrites==0&&system.Values[ethernet+"/IPv4"][0]=="192.0.2.53"&&system.Values[system.Id+"/IPv4"][0]=="192.0.2.53","Turning off restores both active adapters without changing IPv6 bindings for ordinary DNS");

        system=new FakeSystem{CaptureDoh=true};store=new MemoryStore();engine=new DnsSettingsEngine(system,store);
        engine.Change(true,EncryptedDns.ServersFor(6),EncryptedDns.Templates[6]);
        system.Values[system.Id+"/IPv6"]=new[]{"2001:db8::53"};before=system.Writes;int bindingsBefore=system.BindingWrites;
        engine.RestoreReturned();
        Check(engine.RestoreSkipped&&system.Writes==before&&system.BindingWrites==bindingsBefore&&store.Json!=null,"A third-party IPv6 DNS change protects the whole adapter including its IPv6 binding");
        Console.WriteLine("All "+passed+" network-follow checks passed; fake adapters only, no real network changes.");
    }
}
