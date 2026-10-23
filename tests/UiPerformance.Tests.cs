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
        using (var form = new MainForm())
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
            Set(form,"active",false);Set(form,"manualSelection",false);
            Check((bool)typeof(MainForm).GetProperty("UseDefaultGoal",flags).GetValue(form,null),"Saved service enables one-click auto selection");
            typeof(MainForm).GetMethod("PickSet",flags).Invoke(form,new object[]{(int)Get(form,"dnsSet")});
            Check(!(bool)typeof(MainForm).GetProperty("UseDefaultGoal",flags).GetValue(form,null)&&(int)Get(form,"defaultGoal")==1,"Manual DNS selection overrides the default in this session without erasing the saved goal");
            Set(form,"defaultGoal",-1);
            int selected=(int)Get(form,"dnsSet");
            Set(form,"providerScroll",0);
            form.ScrollProviderPage(1);
            Check((int)Get(form,"providerScroll")==324&&(int)Get(form,"dnsSet")==selected,"Arrow pages three providers without changing selected DNS");
            form.ScrollProviderPage(1);
            Check((int)Get(form,"providerScroll")==((Dns.AvailableProviders.Length+1-3)*108),"Last page clamps to the final Auto entry");
            form.ScrollProviderPage(-1);form.ScrollProviderPage(-1);
            Check((int)Get(form,"providerScroll")==0,"Backward paging stops at first provider");
            var segments=(Rectangle[])Get(form,"rSeg");
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
            Set(form,"agStatus",new AgStatus{Checked=true});
            Check(!form.AntigravityUsesSharedDoh,"Existing separate Antigravity rules remain available for removal");
            Set(form,"agStatus",new AgStatus{External=true});
            Check(!form.AntigravityUsesSharedDoh,"Foreign Antigravity rules keep the cleanup action available");
            Set(form,"agLoading",savedAgLoading);Set(form,"active",savedActive);Set(form,"dnsConfirmed",savedConfirmed);
            Set(form,"dnsSet",selected);Set(form,"agStatus",savedAgStatus);Set(form,"secureDns",savedSecure);
            Check(segments[1].IsEmpty&&segments[2].IsEmpty&&segments[3].Left==segments[0].Right&&segments[4].Left==segments[3].Right,"Disabled GeoHide tiles have no hit areas or gaps in the provider bar");
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
        }
        Console.WriteLine("All " + passed + " UI checks passed; no app release built, no network settings changed.");
    }
}
