using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HbAi.XboxDns;

sealed class FakeDotPolicy : IDotPolicy
{
    internal bool On, FailSave, FailSet;
    internal bool? Saved;
    internal int Writes, Saves;
    public bool Enabled() { return On; }
    public bool? Backup() { return Saved; }
    public void Save(bool? value) { Saves++; if (FailSave) throw new IOException("save"); Saved = value; }
    public void Set(bool value) { Writes++; if (FailSet) throw new IOException("set"); On = value; }
}
static class DotDnsTests
{
    static int passed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    static void Fails(Action action, string message) { bool failed = false; try { action(); } catch { failed = true; } Check(failed,message); }
    static FamilyBackup Tls(params DohConfig[] properties) { return new FamilyBackup { Family="IPv4",Manual=true,Servers=new[]{"109.120.132.43"},DohCaptured=true,Doh=properties }; }
    static void Main(string[] args)
    {
        Check(Marshal.SizeOf(typeof(EncryptedDns.DotSettings)) == 24 && Marshal.OffsetOf(typeof(EncryptedDns.DotSettings),"Port").ToInt32()==16,"TLS native structure matches the Windows SDK ABI");
        Check(Marshal.SizeOf(typeof(EncryptedDns.Property)) == 24 && Marshal.OffsetOf(typeof(EncryptedDns.Property),"Value").ToInt32()==16,"DNS property union matches the Windows SDK ABI");
        var strict=EncryptedDns.Strict(1,"dns.magicdns.ru",true);
        EncryptedDns.Validate(Tls(strict));
        Check(strict[0].Kind==2&&strict[0].Flags==1&&strict[0].Port==853,"TLS is explicit with port 853 and no plaintext fallback");
        Fails(()=>EncryptedDns.Validate(Tls(new DohConfig{Index=0,Kind=2,Flags=1,Port=853,Template="https://example.com"})),"TLS host rejects URLs");
        Fails(()=>EncryptedDns.Validate(Tls(new DohConfig{Index=0,Kind=3,Flags=1,Port=853,Template="dns.magicdns.ru"})),"Unknown encryption properties are refused");
        Fails(()=>EncryptedDns.Validate(Tls(strict[0],strict[0])),"Duplicate TLS properties are refused");
        var both=new[]{strict[0],EncryptedDns.Strict(1,"https://dns.magicdns.ru/dns-query")[0]};
        EncryptedDns.Validate(Tls(both));Check(true,"Existing HTTPS and TLS properties on one server can be backed up");
        Check(!EncryptedDns.IsStrict(new DohConfig{Kind=2,Port=853,Flags=3}),"TLS fallback cannot be reported as strict encryption");
        var magic=Tls(EncryptedDns.Strict(2,DotDns.Host(6),true));magic.Servers=DotDns.Servers(6);
        Check(EncryptedDns.ExplicitlyConfigured(magic,6),"MagicDNS TLS configuration is recognized");
        var ai=Tls(EncryptedDns.Strict(2,DotDns.Host(5),true));ai.Servers=DotDns.Servers(5);
        Check(EncryptedDns.ExplicitlyConfigured(ai,5),"Encrypted-only DNS-AI recognizes TLS as encrypted");
        foreach(int provider in new[]{0,3,4,5,6}) {
            Check(DotDns.Available(provider)&&DotDns.Servers(provider).Length>0,"Provider TLS metadata: "+Dns.Name[provider]);
            Check(EncryptedDns.Templates[provider]!=null,"Provider HTTPS metadata: "+Dns.Name[provider]);
        }
        Check(!Dns.IsAvailable(1)&&!Dns.IsAvailable(2),"Adding transports does not re-enable GeoHide");
        foreach(int offset in new[]{400,500}) foreach(int provider in Enumerable.Range(0,Dns.Name.Length)) {
            var request=new DnsRequest{Enable=true,Set=offset+provider,Parent=123,Token=Guid.NewGuid()};DnsRequest parsed;
            Check(DnsRequest.TryParse(request.Arguments.Split(' '),out parsed)&&parsed.Set==request.Set,"TLS elevated operation round-trip "+request.Set);
        }
        DnsRequest rejected;
        Check(!DnsRequest.TryParse(new[]{"--dns-operation","tls-enable","dns.evil.test",Guid.NewGuid().ToString("N"),"123"},out rejected),"Elevated TLS cannot accept arbitrary hosts");

        var system=new FakeSystem{CaptureDoh=true};var store=new MemoryStore();var engine=new DnsSettingsEngine(system,store);
        engine.Change(true,DotDns.Servers(6),DotDns.Host(6),true);
        Check(system.Encryption[system.Id+"/IPv4"].All(e=>e.Kind==2&&e.Flags==1),"Engine applies TLS properties rather than HTTPS templates");
        engine.Change(true,EncryptedDns.ServersFor(6),EncryptedDns.Templates[6]);
        Check(system.Encryption[system.Id+"/IPv4"].All(e=>e.Kind!=2),"Switching TLS to HTTPS clears TLS properties");
        engine.Change(false,new string[0]);
        Check(system.Values[system.Id+"/IPv4"].SequenceEqual(new[]{"192.0.2.53","192.0.2.54"})&&system.Ipv6Enabled&&store.Json==null,"Turning TLS off restores original DNS and IPv6");
        engine.ChangeVerified(DotDns.Servers(6),DotDns.Host(6),true,()=>{});
        Check(store.Json!=null,"Successful system verification retains recovery snapshot");
        engine.Change(false,new string[0]);
        Fails(()=>engine.ChangeVerified(DotDns.Servers(6),DotDns.Host(6),true,()=>{throw new IOException("system DNS timeout");}),"Failed system verification triggers rollback");
        Check(system.Values[system.Id+"/IPv4"][0]=="192.0.2.53"&&store.Json==null&&system.Ipv6Enabled,"Rollback after system DNS failure restores all settings");
        system.Encryption[system.Id+"/IPv4"]=strict;system.Values[system.Id+"/IPv4"]=new[]{"109.120.132.43"};
        engine.Change(true,EncryptedDns.ServersFor(0),EncryptedDns.Templates[0]);
        Check(RegistryDnsBackupStore.SnapshotVersion(store.Load())==3,"TLS snapshots use a version older applications must reject");
        engine.Change(false,new string[0]);Check(system.Encryption[system.Id+"/IPv4"][0].Kind==2,"Existing TLS is restored losslessly after using HTTPS");

        var policy=new FakeDotPolicy();var tx=new DotPolicyTransaction(policy);tx.Enable();
        Check(policy.On&&policy.Saved==false,"Original disabled policy saved before enabling TLS");
        tx.Enable();Check(policy.Saved==false&&policy.Writes==1,"Repeated TLS use preserves the original policy snapshot");
        tx.Restore();Check(!policy.On&&!policy.Saved.HasValue,"Turning TLS off restores the original global policy");
        policy=new FakeDotPolicy{On=true};tx=new DotPolicyTransaction(policy);tx.Enable();tx.Restore();
        Check(policy.On&&policy.Writes==0&&policy.Saves==0,"An already enabled global policy is never claimed or disabled");
        policy=new FakeDotPolicy{FailSave=true};tx=new DotPolicyTransaction(policy);
        Fails(tx.Enable,"Backup failure aborts policy activation");Check(policy.Writes==0&&!policy.On,"No policy writes before durable backup");
        policy=new FakeDotPolicy{FailSet=true};tx=new DotPolicyTransaction(policy);
        Fails(tx.Enable,"Policy write failure keeps recovery state");Check(policy.Saved==false,"Failed policy activation retains original state");
        policy=new FakeDotPolicy{Saved=false,On=false};tx=new DotPolicyTransaction(policy);tx.Restore();Check(policy.Writes==0&&!policy.Saved.HasValue,"External policy disable is preserved");
        if(args.Contains("--live")) {
            Console.WriteLine("READ-ONLY global TLS enabled="+new WindowsDotPolicy().Enabled());
            Parallel.ForEach(new[]{0,3,4,5,6},new ParallelOptions{MaxDegreeOfParallelism=3},provider=> {
                var result=DotDns.Check(provider,"example.com");Console.WriteLine("LIVE "+Dns.Name[provider]+" "+result+" "+result.Detail);
            });
        }
        Console.WriteLine("All "+passed+" TLS checks passed. No system settings changed.");
    }
}
