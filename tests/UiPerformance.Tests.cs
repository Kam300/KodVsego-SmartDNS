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
            Check(form.AcceptNetworkSnapshot(0), "Current idle snapshot accepted");
            Set(form, "busy", true);
            Check(!form.AcceptNetworkSnapshot(0), "Snapshot cannot overwrite an active operation");
            Set(form, "busy", false); Set(form, "operationRevision", 1);
            Check(!form.AcceptNetworkSnapshot(0) && form.AcceptNetworkSnapshot(1), "Old snapshot remains rejected after operation finishes");
            Set(form,"autoGoal",0);
            Check((int)typeof(MainForm).GetProperty("SelectedSegment",flags).GetValue(form,null)==Dns.Name.Length,"Auto selection has its own segment without changing DNS provider indices");
            typeof(MainForm).GetMethod("PickSet",flags).Invoke(form,new object[]{0});
            Check((int)Get(form,"autoGoal")==-1,"Manual choice exits auto even when provider itself is unchanged");
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
                dialog.ShowAutoDnsResult("GeoHide для ChatGPT","Сайт просит проверку в браузере.\nПосле подключения откройте сервис.","LONG TECHNICAL DETAILS","Подключить GeoHide");
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
                Check(background.Width==880&&background.Height==2*(int)typeof(MainForm).GetField("CanvasHeight",BindingFlags.NonPublic|BindingFlags.Static).GetRawConstantValue(),"200 percent DPI uses a background cache sized in physical pixels");
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
