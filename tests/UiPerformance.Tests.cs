using System.Linq;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using HbAi.XboxDns;

static class UiPerformanceTests
{
    static readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static object Get(MainForm form, string name) { return typeof(MainForm).GetField(name, flags).GetValue(form); }
    static void Set(MainForm form, string name, object value) { typeof(MainForm).GetField(name, flags).SetValue(form, value); }
    [STAThread]
    static void Main()
    {
        // Never show the form: Shown, polling and DNS writes cannot run.
        using (var form = new MainForm(new TunnelSession(),false))
        {
            Check((bool)Get(form, "initializing") && !(bool)Get(form, "checkingNetwork"), "Constructing window defers network initialization until Shown");
            Check(form.ConnectionLabel == "Проверяем настройки…", "Initial status does not claim DNS is off before reading Windows");
            Set(form,"defaultGoal",-1);
            Check(form.AcceptNetworkSnapshot(0), "Current idle snapshot accepted");
            var nativeHandle=form.Handle;
            var creation=(CreateParams)typeof(Control).GetProperty("CreateParams",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form,null);
            Check((creation.Style&0x00c40000)==0,"Native caption and thick-frame styles remain disabled for the custom window");
            Check(form.ClientSize==form.Size&&form.PointToScreen(Point.Empty)==form.Location,"Real native client fills the window with no title bar covering header buttons");
            var originalSize=form.ClientSize;
            Check(form.Size==form.MinimumSize,"Startup window size is the minimum; enlargement remains available");
            Check(form.MinimumSize.Width>0&&form.MinimumSize.Height>0,"Resizable window has explicit minimum dimensions");
            form.Size=new Size(10,10);
            Check(form.Width>=form.MinimumSize.Width&&form.Height>=form.MinimumSize.Height,"Windows cannot shrink the form below its minimum");
            form.ClientSize=new Size(originalSize.Width+240,originalSize.Height+180);
            float resizedScale=(float)Get(form,"uiScale");var offset=(PointF)Get(form,"canvasOffset");
            Console.WriteLine("INFO resize: client="+form.ClientSize+"; scale="+resizedScale+"; offset="+offset+"; expected="+Math.Min(form.ClientSize.Width/440f,form.ClientSize.Height/988f));
            int layoutWidth=(int)Get(form,"layoutWidth"),layoutHeight=(int)Get(form,"layoutHeight");
            Check(offset==PointF.Empty&&Math.Abs(layoutWidth*resizedScale-form.ClientSize.Width)<=1&&Math.Abs(layoutHeight*resizedScale-form.ClientSize.Height)<=1,"Responsive layout fills the real window without sidebars or stretched fonts");
            var responsiveCard=(Rectangle)Get(form,"rSecureOption");
            Check(responsiveCard.Left==28&&responsiveCard.Right==layoutWidth-28&&((Rectangle)Get(form,"providerViewport")).Width==layoutWidth-116,"Cards and provider list expand to available window width");
            Check(((Rectangle)Get(form,"providerViewport")).Width/(int)Get(form,"ProviderItemWidth")>3,"Wider windows expose more than three DNS choices");
            Check(((Rectangle)Get(form,"rToggle")).X==(layoutWidth-200)/2&&((Rectangle)Get(form,"rSettings")).X==layoutWidth-152,"Power remains centered and header actions stay anchored to the right");
            var toCanvas=typeof(MainForm).GetMethod("CanvasPoint",flags);
            var mapped=(Point)toCanvas.Invoke(form,new object[]{new Point((int)Math.Round(offset.X+220*resizedScale),(int)Math.Round(offset.Y+300*resizedScale))});
            Check(Math.Abs(mapped.X-220)<=1&&Math.Abs(mapped.Y-300)<=1,"Pointer mapping follows resized and centered buttons");
            Check(form.ResizeHitTest(new Point(0,form.ClientSize.Height/2))==10&&form.ResizeHitTest(new Point(form.ClientSize.Width-1,form.ClientSize.Height/2))==11&&form.ResizeHitTest(new Point(form.ClientSize.Width/2,0))==12&&form.ResizeHitTest(new Point(form.ClientSize.Width/2,form.ClientSize.Height-1))==15,"All four window edges resize");
            Check(form.ResizeHitTest(Point.Empty)==13&&form.ResizeHitTest(new Point(form.ClientSize.Width-1,0))==14&&form.ResizeHitTest(new Point(0,form.ClientSize.Height-1))==16&&form.ResizeHitTest(new Point(form.ClientSize.Width-1,form.ClientSize.Height-1))==17,"All four window corners resize diagonally");
            var closeBounds=(Rectangle)Get(form,"rClose");
            var closePoint=new Point((int)Math.Round((closeBounds.Left+closeBounds.Width/2f)*resizedScale),(int)Math.Round((closeBounds.Top+closeBounds.Height/2f)*resizedScale));
            Check(form.ResizeHitTest(closePoint)==0,"Resize grips do not steal the close button");
            using(var preview=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)) {
                form.DrawToBitmap(preview,form.ClientRectangle);
                preview.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-resize-ui.png"));
            }
            Set(form,"availableUpdate",new UpdateRelease{Version="9.9.9"});
            using(var preview=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)) {
                form.DrawToBitmap(preview,form.ClientRectangle);
                preview.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-update-indicator.png"));
            }
            var indicator=(Rectangle)Get(form,"rUpdateIndicator");var versionHit=(Rectangle)Get(form,"rVersion");
            Check(!indicator.IsEmpty&&versionHit.Contains(indicator)&&!indicator.IntersectsWith((Rectangle)Get(form,"rGroup"))&&!indicator.IntersectsWith((Rectangle)Get(form,"rBrand")),"Update dot is clickable with version and does not overlap footer links");
            Check(indicator.Width==8&&indicator.Height==8,"Available update uses a compact dot instead of a text badge");
            Set(form,"availableUpdate",null);
            using(var preview=new Bitmap(form.ClientSize.Width,form.ClientSize.Height))form.DrawToBitmap(preview,form.ClientRectangle);
            Check(((Rectangle)Get(form,"rUpdateIndicator")).IsEmpty,"Update indicator disappears when no update is available");
            var footer=(Rectangle)Get(form,"rBrand");
            Check(footer.Left==28&&layoutHeight-footer.Bottom==20,"Footer remains anchored to the bottom without the removed Telegram banner");
            var agCard=(Rectangle)Get(form,"rAgOption");var cleanup=(Rectangle)Get(form,"rAgCleanup");
            Check(agCard.Height==58&&footer.Top-agCard.Bottom==16&&cleanup.Bottom<=agCard.Bottom,"Compact Antigravity card is followed directly by the footer");
            form.ClientSize=originalSize;
            Set(form,"defaultGoal",1);
            using(var settings=form.CreateSettingsDialog()) {
                var save=(Button)typeof(StyledDialog).GetField("primaryAction",flags).GetValue(settings);
                var open=(Button)typeof(StyledDialog).GetField("settingsOpenBackup",flags).GetValue(settings);
                var choice=(DialogServicePicker)typeof(StyledDialog).GetField("settingsGoalChoice",flags).GetValue(settings);
                var manual=(RadioButton)typeof(StyledDialog).GetField("settingsManualMode",flags).GetValue(settings);
                var automatic=(RadioButton)typeof(StyledDialog).GetField("settingsAutoMode",flags).GetValue(settings);
                Check(save.Text=="Сохранить"&&save.DialogResult==DialogResult.OK&&settings.CancelButton.DialogResult==DialogResult.Cancel,"Settings saves only by explicit acceptance and Escape cancels");
                Check(open.Enabled==System.IO.Directory.Exists(AgCleanupBackup.Root)&&open.Bottom<save.Top,"Backup folder action remains separate from saving the default service");
                Check(settings.SettingsGoal==1&&choice.Items.Length==AutoDns.Goals.Length&&automatic.Checked,"Settings loads saved service with a separate automatic mode selector");
                int automaticHeight=settings.ClientSize.Height;
                manual.Checked=true;
                Check(settings.SettingsGoal==-1&&(int)Get(form,"defaultGoal")==1,"Changing settings does not apply or persist preference before Save");
                Check(settings.ClientSize.Height<automaticHeight&&!choice.Enabled,"Manual mode removes the unused service row and shrinks settings");
                settings.StartPosition=FormStartPosition.Manual;settings.Location=new Point(-10000,-10000);settings.ShowInTaskbar=false;settings.Show();Application.DoEvents();
                using(var image=new Bitmap(settings.ClientSize.Width,settings.ClientSize.Height)) {
                    settings.DrawToBitmap(image,settings.ClientRectangle);image.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-default-service-settings.png"));
                }
                automatic.Checked=true;
                Check(settings.SettingsGoal==1&&choice.Visible&&choice.Enabled,"Switching back to automatic mode preserves the chosen service");
                using(var image=new Bitmap(settings.ClientSize.Width,settings.ClientSize.Height)) {
                    settings.DrawToBitmap(image,settings.ClientRectangle);image.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-auto-service-settings.png"));
                }
            }
            foreach(float requested in new[]{1f,1.25f,1.5f,2f})foreach(bool dark in new[]{false,true}) {
                float fitted=Math.Min(requested,(Screen.FromControl(form).WorkingArea.Height-32f)/680f);
                using(var settings=new StyledDialog(form,dark,(FontFamily)Get(form,"fBold"),(FontFamily)Get(form,"fMed"),"","Настройки",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1,fitted)) {
                    settings.AddSettingsControls(true,()=>{},AutoDns.Goals.Select(g=>g.Name).ToArray(),1,url=>{});settings.AddLogSettingsAction(logAction=>{});settings.AddResolverSettingsAction(true);
                    settings.StartPosition=FormStartPosition.Manual;settings.Location=new Point(-10000,-10000);settings.ShowInTaskbar=false;settings.Show();Application.DoEvents();
                    var manual=(RadioButton)typeof(StyledDialog).GetField("settingsManualMode",flags).GetValue(settings);
                    var automatic=(RadioButton)typeof(StyledDialog).GetField("settingsAutoMode",flags).GetValue(settings);
                    var connection=(Panel)typeof(StyledDialog).GetField("settingsConnectionPanel",flags).GetValue(settings);
                    var backup=(Panel)typeof(StyledDialog).GetField("settingsBackupPanel",flags).GetValue(settings);
                    var save=(Button)typeof(StyledDialog).GetField("primaryAction",flags).GetValue(settings);
                    var lastLink=(Control)typeof(StyledDialog).GetField("settingsTunnel",flags).GetValue(settings);
                    var observerOption=(Control)typeof(StyledDialog).GetField("settingsResolverCheck",flags).GetValue(settings);
                    int repaints=0;settings.Invalidated+=(s,e)=>{if(e.InvalidRect.Contains(settings.ClientRectangle))repaints++;};
                    for(int cycle=0;cycle<8;cycle++) {
                        if(cycle%2==0)manual.Checked=true;else automatic.Checked=true;Application.DoEvents();
                        Check(connection.Controls.Cast<Control>().Where(c=>c.Visible).All(c=>connection.ClientRectangle.Contains(c.Bounds)),"Connection controls stay inside their opaque card, scale="+requested+" dark="+dark+" cycle="+cycle);
                        Check(backup.Controls.Cast<Control>().All(c=>backup.ClientRectangle.Contains(c.Bounds))&&connection.Bottom<backup.Top,"Backup card never overlaps connection controls");
                        Check(lastLink.Bottom<observerOption.Top&&observerOption.Bottom<save.Top&&settings.ClientRectangle.Contains(save.Bounds),"Settings links and footer remain separated inside the window");
                        Check(settings.Region.IsVisible(settings.ClientSize.Width/2,settings.ClientSize.Height-2)&&!settings.Region.IsVisible(settings.ClientSize.Width/2,settings.ClientSize.Height+2),"Rounded window region follows the current manual/automatic height");
                    }
                    Check(repaints>=8,"Mode transitions invalidate the full native dialog instead of leaving stale cards");
                    Check(connection.Controls.OfType<Label>().All(l=>l.BackColor==connection.BackColor)&&backup.Controls.OfType<Label>().All(l=>l.BackColor==backup.BackColor),"Card labels share the actual container background");
                    if(requested==2f)using(var image=new Bitmap(settings.Width,settings.Height)){settings.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-settings-switch-"+(dark?"dark":"light")+".png"));}
                }
            }
            Set(form,"active",false);Set(form,"manualSelection",false);
            Check((bool)typeof(MainForm).GetProperty("UseDefaultGoal",flags).GetValue(form,null),"Saved service enables one-click auto selection");
            typeof(MainForm).GetMethod("PickSet",flags).Invoke(form,new object[]{(int)Get(form,"dnsSet")});
            Check(!(bool)typeof(MainForm).GetProperty("UseDefaultGoal",flags).GetValue(form,null)&&(int)Get(form,"defaultGoal")==1,"Manual DNS selection overrides the default in this session without erasing the saved goal");
            Set(form,"defaultGoal",-1);
            Set(form,"dnsSet",12);Check(form.PlainAntigravityNote.Contains("не заявлен"),"AdGuard card does not advertise an unsupported Antigravity route");
            Set(form,"dnsSet",9);Check(form.PlainAntigravityNote=="Будет использован: Malw DNS","Malw card offers its own declared Antigravity route");
            Set(form,"dnsSet",0);
            int selected=(int)Get(form,"dnsSet");
            Set(form,"providerScroll",0);
            form.ScrollProviderPage(1);
            Check((int)Get(form,"providerScroll")==Math.Min(324,(Dns.AvailableProviders.Length+1-3)*108)&&(int)Get(form,"dnsSet")==selected,"Arrow pages available providers without changing selected DNS");
            for(int page=0;page<Dns.AvailableProviders.Length;page++)form.ScrollProviderPage(1);
            Check((int)Get(form,"providerScroll")==((Dns.AvailableProviders.Length+1-3)*108),"Last page clamps to the final provider with an expanded catalog");
            for(int page=0;page<Dns.AvailableProviders.Length;page++)form.ScrollProviderPage(-1);
            Check((int)Get(form,"providerScroll")==0,"Backward paging stops at first provider");
            var segments=(Rectangle[])Get(form,"rSeg");
            Check(segments[Dns.Name.Length].Left<segments[0].Left&&segments[Dns.Name.Length].Left==58,"Auto occupies the first slot without renumbering stored provider IDs");
            var savedAgStatus=(AgStatus)Get(form,"agStatus");
            bool savedSecure=(bool)Get(form,"secureDns");
            Set(form,"dnsSet",6);Set(form,"secureDns",true);Set(form,"agStatus",new AgStatus{WindowsUpdateException=true});
            Check(form.EncryptionNote.Contains("Windows Update"),"Adapter DoH label discloses the preserved Windows Update exception");
            Set(form,"agStatus",new AgStatus{WindowsUpdateException=true,Checked=true});
            Check(form.EncryptionNote.Contains("Windows Update")&&form.EncryptionNote.Contains("Antigravity"),"All known DNS exceptions stay visible when combined");
            bool savedAgLoading=(bool)Get(form,"agLoading"),savedActive=(bool)Get(form,"active"),savedConfirmed=(bool)Get(form,"dnsConfirmed");
            Set(form,"agLoading",false);Set(form,"agStatus",new AgStatus());Set(form,"active",false);Set(form,"dnsConfirmed",false);
            Check(form.AntigravityUsesSharedDoh&&form.SharedAntigravityNote.Contains("большой кнопкой"),"Disconnected DoH mode replaces the redundant Antigravity checkbox with a connection hint");
            int revisionBefore=(int)Get(form,"operationRevision");
            typeof(MainForm).GetMethod("ToggleAntigravity",flags).Invoke(form,null);
            Check(!(bool)Get(form,"busy")&&(int)Get(form,"operationRevision")==revisionBefore,"Shared DoH card cannot open a redundant success dialog or change DNS rules");
            using(var preview=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)) {
                form.DrawToBitmap(preview,form.ClientRectangle);preview.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-shared-doh-card.png"));
            }
            Set(form,"active",true);Set(form,"dnsConfirmed",true);
            Check(!form.SharedAntigravityNote.Contains("Включите"),"Confirmed DoH connection shows current use instead of asking to connect again");
            Check(form.SharedAntigravityNote.Contains("Antigravity не проверен"),"Configured common DNS does not claim a successful Antigravity account or model response");
            foreach(int provider in new[]{0,7,8,10,11,12}){
                Set(form,"dnsSet",provider);
                Check(!form.SharedAntigravityDeclared&&form.SharedAntigravityTitle.Contains("не заявлено")&&form.SharedAntigravityNote.Contains(Dns.Name[provider])&&!form.SharedAntigravityNote.Contains("Включите"),"Encryption does not invent Antigravity coverage for "+Dns.Name[provider]);
            }
            Set(form,"dnsSet",12);Set(form,"active",false);
            Check(form.SharedAntigravityNote.Contains("не заявлена")&&!form.SharedAntigravityNote.Contains("Включите"),"Disconnected AdGuard no longer asks to enable DNS as an Antigravity solution");
            Set(form,"secureDns",false);int unsupportedRevision=(int)Get(form,"operationRevision");
            typeof(MainForm).GetMethod("ToggleAntigravity",flags).Invoke(form,null);
            Check(form.AntigravityCardInfoOnly&&(int)Get(form,"operationRevision")==unsupportedRevision,"Plain AdGuard is informational too and cannot activate an unsupported Antigravity option");
            Set(form,"agStatus",new AgStatus{Checked=true});Check(!form.AntigravityCardInfoOnly,"Existing Antigravity rules stay removable even with an unsupported main provider");
            Set(form,"agStatus",new AgStatus());Set(form,"secureDns",true);
            Set(form,"dnsSet",6);Set(form,"active",true);
            Set(form,"agStatus",new AgStatus{Checked=true});
            Check(!form.AntigravityUsesSharedDoh,"Existing separate Antigravity rules remain available for removal");
            Set(form,"agStatus",new AgStatus{External=true});
            Check(!form.AntigravityUsesSharedDoh,"Foreign Antigravity rules keep the cleanup action available");
            Set(form,"agLoading",savedAgLoading);Set(form,"active",savedActive);Set(form,"dnsConfirmed",savedConfirmed);
            Set(form,"dnsSet",selected);Set(form,"agStatus",savedAgStatus);Set(form,"secureDns",savedSecure);
            Check(segments[1].IsEmpty&&segments[2].IsEmpty&&segments[3].IsEmpty&&segments[4].Left==segments[0].Right&&segments[5].Left==segments[4].Right,"Disabled GeoHide and Xbox tiles have no hit areas or gaps in the provider bar");
            Set(form,"dnsSet",1);
            typeof(MainForm).GetMethod("CheckDnsServers",flags).Invoke(form,null);
            Check(!(bool)Get(form,"probing")&&((string[])Get(form,"probeLabels"))[1].Contains("отключён"),"A still-configured disabled provider is not queried in the background");
            Set(form,"dnsSet",selected);
            var viewport=(Rectangle)Get(form,"providerViewport");
            Check(!viewport.IntersectsWith((Rectangle)Get(form,"providerPrevious"))&&!viewport.IntersectsWith((Rectangle)Get(form,"providerNext"))&&viewport.Width==3*108,"Three full provider tiles fit between separate arrow hit areas");
            Set(form, "busy", true);
            Check(!form.AcceptNetworkSnapshot(0), "Snapshot cannot overwrite an active operation");
            Set(form, "busy", false); Set(form, "operationRevision", 1);
            Check(!form.AcceptNetworkSnapshot(0) && form.AcceptNetworkSnapshot(1), "Old snapshot remains rejected after operation finishes");
            Set(form,"autoGoal",0);
            Check((int)typeof(MainForm).GetProperty("SelectedSegment",flags).GetValue(form,null)==Dns.Name.Length,"Auto selection has its own segment without changing DNS provider indices");
            typeof(MainForm).GetMethod("PickSet",flags).Invoke(form,new object[]{0});
            Check((int)Get(form,"autoGoal")==-1,"Manual choice exits auto even when provider itself is unchanged");
            using(var info=form.CreateDialog("Включите DNS большой кнопкой.","Режим DoH",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1)) {
                var caption=(Label)typeof(StyledDialog).GetField("statusCaption",flags).GetValue(info);
                var marker=(DialogStatusIcon)typeof(StyledDialog).GetField("statusMarker",flags).GetValue(info);
                Check(caption.Text=="Информация"&&marker.Kind==DialogStatusKind.Information&&marker.AccessibleName=="Информация","Instructions use an information icon and never claim a completed operation");
                info.AddDnsTestControls(new[]{"MagicDNS"},0,index=>{});
                info.ShowDnsTestStatus("Проверка завершена","Проверено",false,false);
                Check(marker.Kind==DialogStatusKind.Success,"Successful DNS tests retain a distinct success icon");
            }
            using(var dialog=form.CreateDialog("Auto test","Авто",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1)) {
                bool changed=false;
                dialog.AddAutoDnsControls(new[]{"Gemini","ChatGPT"},0,i=>{},()=>changed=true);
                var choice=(ComboBox)typeof(StyledDialog).GetField("browserChoice",flags).GetValue(dialog);
                Check(dialog.CancelButton.DialogResult==DialogResult.Cancel&&dialog.AcceptButton!=dialog.CancelButton,"Escape and close never accept an automatic connection");
                choice.SelectedIndex=1;Check(changed,"Changing goal invalidates the previous selection");
                dialog.ShowAutoDnsStatus("running","test",true,false);
                Check(!choice.Enabled,"Goal cannot change during search");
                dialog.ShowAutoDnsStatus("done","test",false,false);
                Check(choice.Enabled,"Goal can be changed after search");
                var primary=(Button)typeof(StyledDialog).GetField("primaryAction",flags).GetValue(dialog);
                Check(primary.DialogResult==DialogResult.None&&choice.Top<((Label)typeof(StyledDialog).GetField("statusCaption",flags).GetValue(dialog)).Top,"Single action starts search and service choice is above the result");
                dialog.ShowAutoDnsResult("GeoHide для ChatGPT","","LONG TECHNICAL DETAILS","Подключить GeoHide");
                var body=(Label)typeof(StyledDialog).GetField("messageText",flags).GetValue(dialog);
                Check(primary.Text=="Подключить GeoHide"&&primary.DialogResult==DialogResult.OK&&!body.Text.Contains("TECHNICAL"),"Selected DNS is named in the action; technical details stay out of summary");
                Check(!(bool)typeof(StyledDialog).GetField("autoDetailsExpanded",flags).GetValue(dialog),"Results are collapsed by default");
                Check(!dialog.CopyAutoLog(text=>{throw new Exception("Stale log copied");}),"Copy is unavailable before current technical details arrive");
                dialog.SetDiagnosticDetails("HTTP=401; classification=Login; api_key=fixture-secret",false);
                string copied=null;
                Check(dialog.CopyAutoLog(text=>copied=text)&&copied.Contains("LONG TECHNICAL DETAILS")&&copied.Contains("HTTP=401")&&copied.Contains("Авто · ChatGPT")&&!copied.Contains("fixture-secret"),"Successful auto log copy includes service, report and technical details with secrets scrubbed");
                var copy=(Button)typeof(StyledDialog).GetField("autoCopyLog",flags).GetValue(dialog);
                Check(copy.Text=="Скопировано"&&copy.Enabled&&!(bool)typeof(StyledDialog).GetField("supportShown",flags).GetValue(dialog),"Copy works after success without a bulky error-report panel");
                Check(!dialog.CopyAutoLog(text=>{throw new System.Runtime.InteropServices.ExternalException("busy");})&&copy.Text=="Буфер занят"&&copy.Enabled,"Busy clipboard permits retry without another error dialog");
                int compactHeight=dialog.ClientSize.Height;
                dialog.ToggleAutoDetails();
                Check(dialog.ClientSize.Height>compactHeight&&primary.Bottom<dialog.ClientSize.Height,"Expanded results leave the connect action visible");
                dialog.ToggleAutoDetails();
                Check(dialog.ClientSize.Height==compactHeight&&dialog.CancelButton.DialogResult==DialogResult.Cancel,"Collapsing restores compact layout and Escape still cancels");
                dialog.ShowAutoDnsStatus("running","test",true,false);
                Check(!primary.Enabled&&primary.DialogResult==DialogResult.None,"Search disables connection and clears previous acceptance");
                dialog.ShowAutoDnsResult("Не удалось подобрать","Повторите позже","Failure details");
                Check(primary.DialogResult==DialogResult.None&&primary.Enabled,"Failed search offers retry instead of applying a previous DNS");
                dialog.SetDiagnosticDetails("TLS timeout; ip=203.0.113.4; token=private-test-value",true);
                var support=(Panel)typeof(StyledDialog).GetField("supportPanel",flags).GetValue(dialog);
                Check((bool)typeof(StyledDialog).GetField("supportShown",flags).GetValue(dialog)&&support.Bottom<primary.Top&&support.Top>=362*primary.Height/42,"Auto failure log controls fit above primary action without overlapping summary");
                Check(dialog.DiagnosticMessage.Contains("TLS timeout")&&!dialog.DiagnosticMessage.Contains("private-test-value"),"Export includes technical failure and removes credentials");
                dialog.ToggleAutoDetails();
                var expanded=(Panel)typeof(StyledDialog).GetField("autoDetails",flags).GetValue(dialog);
                Check(expanded.Bottom<support.Top&&support.Bottom<primary.Top,"Expanded diagnostics keep log save and connect controls separate");
                dialog.ShowAutoDnsStatus("running","test",true,false);
                Check(!(bool)typeof(StyledDialog).GetField("supportShown",flags).GetValue(dialog)&&!dialog.DiagnosticMessage.Contains("TLS timeout"),"New search clears stale diagnostics and hides log action");
                Check(!dialog.CopyAutoLog(text=>copied=text)&&!dialog.DiagnosticMessage.Contains("Failure details"),"Running or changed search cannot copy previous results");
            }
            foreach(float requested in new[]{1f,1.5f,2f}) {
                float fitted=Math.Min(requested,(Screen.FromControl(form).WorkingArea.Height-32f)/640f);
                using(var dialog=form.CreateDialog("Авто","Авто",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1,fitted)) {
                    dialog.AddAutoDnsControls(new[]{"Gemini","ChatGPT"},1,i=>{},null);
                    dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-10000,-10000);dialog.ShowInTaskbar=false;dialog.Show();Application.DoEvents();
                    dialog.ShowAutoDnsResult("MagicDNS для ChatGPT","Сайт: Требуется проверка в браузере; API: Нужен ключ или вход · сервер отвечает","Full website and API report","Подключить MagicDNS");
                    dialog.SetDiagnosticDetails("DNS, TLS, HTTP; no authentication",false);
                    var copy=(Button)typeof(StyledDialog).GetField("autoCopyLog",flags).GetValue(dialog);
                    var toggle=(LinkLabel)typeof(StyledDialog).GetField("autoDetailsToggle",flags).GetValue(dialog);
                    var primary=(Button)typeof(StyledDialog).GetField("primaryAction",flags).GetValue(dialog);
                    var bodyPanel=(Panel)typeof(StyledDialog).GetField("messageBody",flags).GetValue(dialog);
                    var summary=(Label)typeof(StyledDialog).GetField("messageText",flags).GetValue(dialog);
                    Check(copy.Visible&&toggle.Right<copy.Left&&copy.Bottom<primary.Top&&copy.Right<=dialog.ClientSize.Width,"Copy and results links fit before connect action for requested scale "+requested);
                    Check(bodyPanel.Height>=summary.GetPreferredSize(new Size(summary.MaximumSize.Width,0)).Height,"Website and API summary fits its card for requested scale "+requested);
                    dialog.ToggleAutoDetails();
                    var details=(Panel)typeof(StyledDialog).GetField("autoDetails",flags).GetValue(dialog);
                    Check(details.Top>copy.Bottom&&details.Bottom<primary.Top,"Expanded results keep copy and connect accessible for requested scale "+requested);
                    dialog.ShowAutoDnsStatus("running","Gemini",true,false);
                    Check(!copy.Visible&&!dialog.CopyAutoLog(text=>{}),"New service/search hides copying for requested scale "+requested);
                    dialog.Close();
                }
            }
            using(var dialog=form.CreateDialog("Auto start test","Авто",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1)) {
                int started=0,goal=-1;
                dialog.AddAutoDnsControls(new[]{"Gemini","ChatGPT"},1,selectedGoal=>{started++;goal=selectedGoal;},null);
                dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-10000,-10000);dialog.ShowInTaskbar=false;dialog.Show();Application.DoEvents();
                dialog.StartAutoDnsSearch();
                Check(started==1&&goal==1,"One-click auto start immediately checks the preselected service without another button press");
                dialog.Close();
            }
            using(var dialog=form.CreateDialog("Запрос прав администратора отменён. DNS не изменён.","Ошибка DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button1)) {
                var body=(Panel)typeof(StyledDialog).GetField("messageBody",flags).GetValue(dialog);
                var label=(Label)typeof(StyledDialog).GetField("messageText",flags).GetValue(dialog);
                Check(body.Height>=label.GetPreferredSize(new Size(label.MaximumSize.Width,0)).Height&&dialog.Height<300,"Short errors fit in a compact dialog");
            }
            using(var dialog=form.CreateDialog("Выберите DNS.","Тест DNS",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1)) {
                dialog.AddDnsTestControls(new[]{"Example DNS"},0,index=>{});
                dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-10000,-10000);dialog.ShowInTaskbar=false;dialog.Show();Application.DoEvents();
                dialog.ShowDnsTestStatus("Проверяем сервер…","Проверка",true,false);
                int runningHeight=dialog.Height;
                Check(runningHeight<400,"DNS test progress has no fixed 480-pixel empty area");
                string full=string.Join("\n",Enumerable.Repeat("example.com — 100 мс",60));
                dialog.ShowDnsTestStatus(full,"Ответы: 60/60",false,false,"Сервер ответил. Настройки DNS не изменялись.");
                int compact=dialog.Height;
                var details=(LinkLabel)typeof(StyledDialog).GetField("testDetailsToggle",flags).GetValue(dialog);
                Check(details.Visible&&dialog.DiagnosticMessage.Contains(full)&&compact<400,"Detailed test data stays in the log while summary stays compact");
                typeof(LinkLabel).GetMethod("OnLinkClicked",flags).Invoke(details,new object[]{new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())});
                Check(dialog.Height>compact&&dialog.Height<=Screen.PrimaryScreen.WorkingArea.Height,"Expanded test details scroll within the screen");
                using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,dialog.ClientRectangle);image.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-compact-test.png"));}
                typeof(LinkLabel).GetMethod("OnLinkClicked",flags).Invoke(details,new object[]{new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())});
                Check(dialog.Height==compact,"Collapsing test results restores the compact height");
                dialog.ShowDnsTestStatus("Повторная проверка…","Проверка",true,false);
                Check(!details.Visible&&dialog.Height==runningHeight,"Repeating a test clears the previous details and height");
            }
            foreach(float testScale in new[]{1f,1.5f,2f})
            using(var dialog=form.CreateDialog("Update test","Обновление",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1)) {
                dialog.AddUpdateControls(()=>{});
                dialog.ShowUpdateStatus("Скачиваем","Загрузка 50%","Скачиваем…",false);
                var primary=(Button)typeof(StyledDialog).GetField("primaryAction",flags).GetValue(dialog);
                Check(primary.DialogResult==DialogResult.None&&!primary.Enabled&&dialog.CancelButton.DialogResult==DialogResult.Cancel,"Update progress cannot accidentally accept or close the install action");
                dialog.ShowUpdateStatus("Доступна новая версия","Готово","Обновить",true);
                Check(primary.Enabled&&primary.Text=="Обновить","Update action becomes available after metadata check");
                dialog.ShowUpdateStatus("Доступна версия 2.23.0.\nСкачивание: 600 КБ.\nПосле установки программа перезапустится.","Доступно обновление","Обновить",true);
                var updateBody=(Panel)typeof(StyledDialog).GetField("messageBody",flags).GetValue(dialog);
                var updateText=(Label)typeof(StyledDialog).GetField("messageText",flags).GetValue(dialog);
                Check(updateBody.Height>=updateText.GetPreferredSize(new Size(updateText.MaximumSize.Width,0)).Height&&primary.Bottom<dialog.ClientSize.Height,"Update summary fits without clipping text or install action");
            }
            Set(form,"autoGoal",0);Set(form,"initializing",false);Set(form,"hasBackup",true);Set(form,"dnsConfirmed",true);
            typeof(MainForm).GetMethod("EnsureProviderVisible",flags).Invoke(form,new object[0]);
            Set(form, "active", true);
            form.SnapMotion();
            var paint = typeof(MainForm).GetMethod("PaintCanvas", flags);
            using (var image = new Bitmap(440, 980))
            using (var graphics = Graphics.FromImage(image))
            {
                paint.Invoke(form, new object[] { graphics });
                var glow = Get(form, "toggleGlow");
                Check(glow != null, "Active glow raster created once");
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 40; i++) paint.Invoke(form, new object[] { graphics });
                Check(object.ReferenceEquals(glow, Get(form, "toggleGlow")), "Repeated frames reuse glow instead of regenerating gradient");
                Console.WriteLine("INFO mean full canvas render: " + timer.Elapsed.TotalMilliseconds / 40 + " ms");
                image.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-auto-ui-test.png"));
            }
            using (var image = new Bitmap(880,1960))
            using (var graphics = Graphics.FromImage(image)) {
                graphics.ScaleTransform(2,2);
                paint.Invoke(form,new object[]{graphics});
                var background=(Bitmap)(Get(form,"darkBackground")??Get(form,"lightBackground"));
                Check(background.Width==2*(int)Get(form,"layoutWidth")&&background.Height==2*(int)Get(form,"layoutHeight"),"200 percent DPI uses a background cache sized in physical pixels for the responsive layout");
                var glow=(Bitmap)Get(form,"toggleGlow");
                Check(glow.Width==560&&glow.Height==560,"Glow also uses device pixels without per-frame upscaling");
                var clock=Stopwatch.StartNew();
                for(int i=0;i<20;i++)paint.Invoke(form,new object[]{graphics});
                Check(object.ReferenceEquals(background,Get(form,"darkBackground")??Get(form,"lightBackground")),"High DPI frames reuse the same native-size background");
                Console.WriteLine("INFO mean 200 percent canvas render: "+clock.Elapsed.TotalMilliseconds/20+" ms");
                foreach(bool dark in new[]{false,true}) {
                    typeof(MainForm).GetMethod("SetTheme",flags).Invoke(form,new object[]{dark,false});form.SnapMotion();
                    paint.Invoke(form,new object[]{graphics});
                    var glyph=image.GetPixel(440,500);
                    Check(glyph.R<50&&glyph.G<70&&glyph.B<80,"Power glyph remains visible at 200 percent DPI in "+(dark?"dark":"light")+" theme");
                }
            }
            Check(form.ConnectionLabel=="Авто · Gemini","Auto status names the selected goal");
            Set(form,"autoNeedsRetry",true);
            Check(form.ConnectionLabel=="Проверьте Авто","Unavailable goal offers a retry without switching DNS");
            Check(!((Timer)Get(form, "anim")).Enabled, "Idle window has no continuous animation timer");
            Set(form,"active",false);Set(form,"initializing",false);Set(form,"busy",false);Set(form,"dnsSet",6);Set(form,"secureDns",false);Set(form,"tlsDns",false);
            int transportRevision=(int)Get(form,"operationRevision");
            form.SelectTransport(DnsTransport.Tls);
            Check(form.Transport==DnsTransport.Tls&&!(bool)Get(form,"busy")&&(int)Get(form,"operationRevision")==transportRevision+1,"Disconnected TLS selection only changes the UI and invalidates stale probes");
            Check(form.EncryptionNote.StartsWith("DoT"),"TLS description names the actual selected transport");
            var bounds=(Rectangle)Get(form,"rSecureOption");
            Check(bounds.Contains(form.ConnectionModeButton)&&bounds.Contains(form.ConnectionProtocolButton)&&!form.ConnectionModeButton.IntersectsWith(form.ConnectionProtocolButton)&&form.ConnectionMethod==ConnectionMethod.Windows&&form.ConnectionHint.Contains("после выхода"),"Unified connection card explains persistent Windows settings");
            using(var menu=form.CreateConnectionMenu()) {
                var choices=menu.Items.OfType<ToolStripMenuItem>().ToArray();
                Check(choices.Length==3&&choices.Count(i=>i.Checked)==1&&choices.All(i=>i.Text.Contains("\n")),"Method menu has three explained choices and exactly one selection");
                Check(choices.All(i=>!i.Text.Contains("HTTPS")&&!i.Text.Contains("TLS")),"Technical protocols do not compete with connection methods");
                menu.Show(new Point(-10000,-10000));Application.DoEvents();menu.Close();
                Check(!menu.IsDisposed,"Closing the native popup finishes before the owner releases its resources");
                choices.Single(i=>(ConnectionMethod)i.Tag==ConnectionMethod.Application).PerformClick();
                Check(form.LocalMode&&form.Transport==DnsTransport.Https&&!(bool)Get(form,"active")&&!(bool)Get(form,"busy"),"Choosing application mode selects encryption without connecting");
            }
            form.SelectConnectionMethod(ConnectionMethod.Windows,false);
            Set(form,"active",true);
            using(var menu=form.CreateConnectionMenu()) {
                Check(menu.Items.OfType<ToolStripMenuItem>().All(i=>!i.Enabled)&&menu.Items.Cast<ToolStripItem>().Any(i=>i.Tag==null&&i.Text.Contains("выключите")),"Connected method menu is locked with an explanation");
                menu.Items.OfType<ToolStripMenuItem>().Last().PerformClick();
                Check(form.ConnectionMethod==ConnectionMethod.Windows,"Disabled choices cannot switch an active connection");
            }
            Set(form,"active",false);
            DnsTransport chosen=DnsTransport.Https;
            using(var menu=ConnectionUi.Protocols(chosen,new[]{false,true,true},false,p=>chosen=p,(Font)form.Font.Clone(),Color.Black,Color.White,Color.Cyan,Color.Gray)) {
                var choices=menu.Items.OfType<ToolStripMenuItem>().ToArray();
                Check(!choices.Single(i=>(DnsTransport)i.Tag==DnsTransport.Plain).Enabled,"Unavailable plaintext stays disabled in advanced settings");
                choices.Single(i=>(DnsTransport)i.Tag==DnsTransport.Tls).PerformClick();
                Check(chosen==DnsTransport.Tls&&form.Transport!=DnsTransport.Tls,"Advanced protocol selection changes only its staged value");
            }
            using(var preview=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)) {
                form.DrawToBitmap(preview,form.ClientRectangle);preview.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"SmartDNS-tls-choice.png"));
            }
            form.SelectTransport(DnsTransport.Https);Check(form.Transport==DnsTransport.Https,"Switching to HTTPS clears the TLS selection");
            form.SelectTransport(DnsTransport.Plain);Check(form.Transport==DnsTransport.Plain,"Switching to ordinary DNS clears encryption selection");
            Set(form,"dnsSet",5);Set(form,"secureDns",true);Set(form,"tlsDns",true);
            form.SelectTransport(DnsTransport.Plain);
            Check(!form.TransportAvailable(DnsTransport.Plain)&&form.Transport==DnsTransport.Tls,"DNS-AI cannot be downgraded to plaintext through the selector");
            Set(form,"dnsSet",2);Check(!form.TransportAvailable(DnsTransport.Tls),"Providers without a published TLS host cannot select TLS");
            Set(form,"dnsSet",0);form.SelectConnectionMethod(ConnectionMethod.Windows,false);Set(form,"secureDns",false);Set(form,"tlsDns",false);
            using(var menu=form.CreateConnectionProtocolMenu()){
                var options=menu.Items.OfType<ToolStripMenuItem>().Where(i=>i.Tag!=null).ToArray();
                Check(options.Length==3&&options.All(i=>i.Enabled)&&options.Single(i=>i.Checked).Tag.Equals(DnsTransport.Plain),"Main Windows selector shows its actual unencrypted protocol and compatible alternatives");
                int revision=(int)Get(form,"operationRevision");options.Single(i=>i.Tag.Equals(DnsTransport.Https)).PerformClick();
                Check(form.Transport==DnsTransport.Https&&(int)Get(form,"operationRevision")==revision+1&&!(bool)Get(form,"busy"),"Main encryption selector changes the stopped configuration without applying system DNS");
            }
            Set(form,"dnsSet",11);
            using(var menu=form.CreateConnectionProtocolMenu()){
                var options=menu.Items.OfType<ToolStripMenuItem>().Where(i=>i.Tag!=null).ToArray();
                Check(!options.Single(i=>i.Tag.Equals(DnsTransport.Plain)).Enabled&&options.Single(i=>i.Tag.Equals(DnsTransport.Plain)).AccessibleDescription.Contains("Webshift"),"HTTPS-only provider explains why plaintext is unavailable");
                Check(!options.Single(i=>i.Tag.Equals(DnsTransport.Tls)).Enabled&&options.Single(i=>i.Tag.Equals(DnsTransport.Tls)).AccessibleDescription.Contains("не публикует"),"Missing TLS endpoint has its own explanation");
            }
            Set(form,"dnsSet",0);form.SelectConnectionMethod(ConnectionMethod.Application,false);
            using(var menu=form.CreateConnectionProtocolMenu()){
                var options=menu.Items.OfType<ToolStripMenuItem>().Where(i=>i.Tag!=null).ToArray();
                Check(!options.Single(i=>i.Tag.Equals(DnsTransport.Plain)).Enabled&&options.Single(i=>i.Tag.Equals(DnsTransport.Plain)).AccessibleDescription.Contains("2 и 3"),"Application mode explains the mandatory encryption requirement");
                options.Single(i=>i.Tag.Equals(DnsTransport.Tls)).PerformClick();Check(form.Transport==DnsTransport.Tls,"Main selector can choose TLS in application mode without starting the worker");
            }
            Set(form,"active",true);int lockedRevision=(int)Get(form,"operationRevision");
            form.SelectConnectionTransport(DnsTransport.Https);
            using(var menu=form.CreateConnectionProtocolMenu())Check(menu.Items.Cast<ToolStripItem>().All(i=>!i.Enabled),"Running connections lock all main encryption choices");
            Check(form.Transport==DnsTransport.Tls&&(int)Get(form,"operationRevision")==lockedRevision,"Locked main selector cannot trigger reconfiguration or elevation");
            Set(form,"active",false);form.SelectConnectionMethod(ConnectionMethod.Windows,false);
            var connectionBounds=(Rectangle)Get(form,"rSecureOption");
            Check(connectionBounds.Bottom<((Rectangle)Get(form,"rAgOption")).Top,"Combined card fits above Antigravity without enlarging the window");
        }
        Console.WriteLine("All " + passed + " UI checks passed; no app release built, no network settings changed.");
    }
}
