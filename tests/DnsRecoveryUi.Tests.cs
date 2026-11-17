using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HbAi.XboxDns;

static class DnsRecoveryUiTests {
    static int passed;static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    [STAThread] static void Main(){
        using(var form=new MainForm(new TunnelSession(),false))using(var dialog=form.CreateSettingsDialog()){
            dialog.ShowInTaskbar=false;dialog.Opacity=0;dialog.Show();Application.DoEvents();
            var link=(LinkLabel)typeof(StyledDialog).GetField("settingsDnsRecovery",Flags).GetValue(dialog);
            Check(link.Visible&&link.Text=="Очистить DNS…","DNS recovery is reachable in General without an active connection");
            Check(link.AccessibleName.Contains("отключённый Wi-Fi"),"Recovery explains that disconnected Wi-Fi is included");
            var heading=(Label)typeof(StyledDialog).GetField("settingsBackupHeading",Flags).GetValue(dialog);
            Check(heading.Right<=link.Left&&heading.Top==link.Top,"Recovery action does not overlap the backup heading");
            dialog.SelectSettingsTab(true);Check(!link.Visible,"Recovery link is hidden on Providers tab");
            dialog.SelectAntigravitySettingsTab();Check(!link.Visible,"Recovery link is hidden on the Antigravity tab");
            dialog.SelectSettingsTab(false);Check(link.Visible,"General tab restores recovery access");
            using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(Path.GetTempPath(),"SmartDNS-offline-recovery-settings.png"));}
            int runs=0;dialog.AddDnsRecoveryAction(d=>runs++);var action=(Button)typeof(StyledDialog).GetField("primaryAction",Flags).GetValue(dialog);
            Check(!action.Enabled,"Cleanup cannot start before the read-only preview is ready");
            dialog.RecoveryStatus("Wi-Fi (отключён): восстановить копию",true);action.PerformClick();Check(runs==1&&dialog.DialogResult==DialogResult.None,"Preview action runs without closing the review window");
            dialog.RecoveryStatus("Восстанавливаем",false,true);dialog.Close();Check(!dialog.IsDisposed&&!action.Enabled,"Window stays open while recovery owns an operation");
            dialog.RecoveryStatus("Копии сохранены для повторной попытки.",false,false,true);Check(action.Enabled&&action.Text=="Закрыть","Incomplete recovery remains visible and can be closed");
        }
        Console.WriteLine("All "+passed+" recovery UI checks passed; no DNS changes, elevation or real recovery actions.");
    }
}
