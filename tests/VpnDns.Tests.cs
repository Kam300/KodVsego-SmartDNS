using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Forms;
using HbAi.XboxDns;

static class VpnDnsTests {
    static int passed;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Reject(Action action,string name){bool rejected=false;try{action();}catch(IOException){rejected=true;}Check(rejected,name);}
    static AgRule Own(){return new AgRule{Id=Guid.NewGuid().ToString("B"),Local=true,Comment=VpnDnsPolicy.Tag,DisplayName=VpnDnsPolicy.Tag,Names=VpnDnsPolicy.Names.ToArray(),Servers=new[]{"127.0.0.1"}};}
    [STAThread] static void Main(){
        var vpn=new VpnStatus{Connections=new[]{"Test VPN"}};
        Check(!VpnDnsPreference.Blocks(vpn,true,true),"Opt-in permits VPN only for local mode");
        Check(VpnDnsPreference.Blocks(vpn,true,false)&&VpnDnsPreference.Blocks(vpn,false,true),"Other connection methods and default remain protected");
        Check(VpnDnsPreference.Blocks(new VpnStatus{Failed=true},true,true),"Failed network inspection cannot be bypassed");
        Check(!VpnDnsPreference.Blocks(new VpnStatus(),true,true),"Opt-in also works without a detected VPN");
        Check(VpnDnsPolicy.Overlap(".",".chatgpt.com")&&VpnDnsPolicy.Overlap(".com",".chatgpt.com"),"Catch-all and parent VPN policies conflict");
        Check(VpnDnsPolicy.Overlap("API.OpenAI.com",".openai.com"),"Exact child policies cannot be overwritten");
        Check(!VpnDnsPolicy.Overlap(".evilopenai.com",".openai.com"),"Suffix conflict check respects domain boundaries");
        var foreign=new AgRule{Id="foreign",Names=new[]{".corp.internal"},Servers=new[]{"10.0.0.53"}};
        VpnDnsPolicy.Validate(new[]{foreign});Check(true,"Unrelated corporate domains retain their VPN DNS");
        foreign.Names=new[]{"."};Reject(()=>VpnDnsPolicy.Validate(new[]{foreign}),"Default VPN policy is preserved and blocks activation");
        var owned=Own();Check(VpnDnsPolicy.Owned(owned),"Ownership requires exact local rule contents");
        owned.Servers=new[]{"1.1.1.1"};Check(!VpnDnsPolicy.Owned(owned),"Changed rule cannot be deleted as ours");
        var rows=new List<AgRule>{Own(),foreign};int flushes=0;
        VpnDnsPolicy.Remove(()=>rows.ToArray(),id=>rows.RemoveAll(r=>r.Id==id),()=>flushes++);
        Check(rows.Count==1&&rows[0]==foreign&&flushes==1,"Cleanup deletes only the owned rule and flushes once");
        VpnDnsPolicy.Remove(()=>rows.ToArray(),id=>{throw new Exception("Foreign deletion");},()=>flushes++);
        Check(flushes==1,"Repeated cleanup is a no-op");
        rows.Add(owned);Reject(()=>VpnDnsPolicy.Remove(()=>rows.ToArray(),id=>{throw new Exception("Changed deletion");},()=>{}),"Externally edited rule leaves recovery pending");
        rows=new List<AgRule>{Own()};Reject(()=>VpnDnsPolicy.Remove(()=>rows.ToArray(),id=>{},()=>{}),"Unconfirmed Windows deletion is reported as failure");
        int reads=0;owned=Own();Reject(()=>VpnDnsPolicy.Remove(()=>{reads++;if(reads==2)owned.Servers=new[]{"8.8.8.8"};return new[]{owned};},id=>{throw new Exception("Raced deletion");},()=>{}),"Ownership rechecked immediately before deletion");
        using(var proxy=new LocalDnsProxy(q=>{var answer=TunnelWire.Error(q);answer[3]=128;return answer;})){
            proxy.Start(0,false);proxy.WatchHost("unique.example.com");
            using(var client=new UdpClient()){client.Client.ReceiveTimeout=3000;client.Connect(IPAddress.Loopback,proxy.Port);var question=DnsProbe.Query("other.example.com");client.Send(question,question.Length);IPEndPoint from=null;client.Receive(ref from);Check(!proxy.HostObserved,"Unrelated local queries do not confirm system DNS path");question=DnsProbe.Query("unique.example.com");client.Send(question,question.Length);client.Receive(ref from);Check(proxy.HostObserved,"Only the exact watched DNS question confirms path");}
            proxy.WatchHost(null);Check(!proxy.HostObserved,"Probe observation clears between verification passes");
        }
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using(var form=new MainForm(new TunnelSession(),false))using(var dialog=form.CreateSettingsDialog()){
            dialog.ShowInTaskbar=false;dialog.Opacity=0;dialog.Show();Application.DoEvents();
            var box=(CheckBox)typeof(StyledDialog).GetField("settingsVpnDns",flags).GetValue(dialog);
            Check(box.Visible&&!box.Checked&&box.Enabled,"Compatibility checkbox is opt-in in General");
            box.Checked=true;Check(dialog.VpnDnsEnabled,"Draft reflects selection without applying DNS");
            dialog.SelectSettingsTab(true);Check(!box.Visible,"Checkbox is hidden on Providers");
            dialog.SelectSettingsTab(false);Check(box.Visible,"Checkbox returns on General");
            var footer=(Button)typeof(StyledDialog).GetField("primaryAction",flags).GetValue(dialog);
            var catalog=(Panel)typeof(StyledDialog).GetField("settingsCatalog",flags).GetValue(dialog);
            Check(catalog.Bottom<=footer.Top,"General controls do not overlap Save");
            using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-vpn-settings.png"));}
        }
        Console.WriteLine("All "+passed+" VPN DNS checks passed; loopback and fake policy only.");
    }
}
