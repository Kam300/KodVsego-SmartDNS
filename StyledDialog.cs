using System;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HbAi.XboxDns
{
    // Modal messages share the application's palette and fonts. No network operations.
    sealed class StyledDialog : Form
    {
        readonly Color surface, border;
        readonly Font titleFont, bodyFont, buttonFont;
        readonly int radius;
        Panel messageBody;
        Button primaryAction, cancelAction;
        int footerGap, actionHeight, bodyGap;
        Control dragControl;
        Point dragPointerStart, dragWindowStart;
        ComboBox browserChoice;
        ComboBox serviceTestChoice;
        internal int TestService {get{return serviceTestChoice==null?-1:serviceTestChoice.SelectedIndex-1;}}
        Button restartBrowser;
        bool browserBusy;
        int browserRowHeight, maxDialogHeight;
        Label messageText, statusCaption, browserHint;
        DialogStatusIcon statusMarker;
        Color statusAccent, statusWarning;
        Panel supportPanel;
        Label supportHint;
        bool supportShown;
        int supportGrowth;
        Func<string, string, string> makeReport;
        string diagnosticDetails;
        internal string DiagnosticMessage {get {return DiagnosticReport.Scrub((autoLayout?messageText.Text+"\n\n"+autoDetailsText.Text:dnsTestLayout?testFullText??messageText.Text:messageText.Text)+(string.IsNullOrEmpty(diagnosticDetails)?"":"\n\nТехнические сведения:\n"+diagnosticDetails));}}
        internal void SetDiagnosticDetails(string details,bool visible)
        {diagnosticDetails=details;if(!string.IsNullOrEmpty(details))DiagnosticReport.Record(Text+" · подробности",details);SetSupportVisible(visible&&!string.IsNullOrEmpty(details));UpdateAutoCopy();UpdateDialogLayout();}
        bool autoLayout, autoReady, autoRunning, autoDetailsExpanded, arranging;
        bool settingsLayout, dnsTestLayout, testDetailsExpanded;
        string testFullText, testSummary; LinkLabel testDetailsToggle;
        Button testCopyResults;
        bool testResultReady;
        DialogServicePicker settingsGoalChoice;
        DialogModeOption settingsManualMode,settingsAutoMode;
        Label settingsModeHint,settingsBackupHeading,settingsBackupTitle,settingsBackupStatus;
        Rectangle settingsConnectionBounds,settingsBackupBounds;
        Panel settingsConnectionPanel,settingsBackupPanel;
        Color settingsCardColor;
        Button settingsOpenBackup,settingsGithub,settingsTelegram;
        Label settingsSupportHeading;
        LinkLabel settingsSource;
        LinkLabel settingsTunnel;
        CheckBox settingsResolverCheck;
        internal bool AutoResolverCheck { get { return settingsResolverCheck==null||settingsResolverCheck.Checked; } }
        internal int SettingsGoal {get{return settingsGoalChoice==null||!settingsAutoMode.Checked?-1:settingsGoalChoice.SelectedIndex;}}
        internal void StartAutoDnsSearch(){if(autoLayout&&!autoRunning&&!autoReady)primaryAction.PerformClick();}
        internal int AutoSelectedGoal {get{return browserChoice.SelectedIndex;}}
        Panel autoDetails;
        Label autoDetailsText, autoScopeNote;
        LinkLabel autoDetailsToggle;
        Button autoCopyLog;
        Color autoCardColor;
        Rectangle autoCardBounds;

        internal StyledDialog(Form owner, bool dark, FontFamily bold, FontFamily medium,
            string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon,
            MessageBoxDefaultButton defaultButton, float requestedScale = 0, bool allowDismiss = false)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Text = title;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            surface = dark ? Color.FromArgb(8, 8, 13) : Color.FromArgb(250, 249, 255);
            border = dark ? Color.FromArgb(43, 40, 59) : Color.FromArgb(222, 217, 239);
            Color ink = dark ? Color.FromArgb(247, 246, 252) : Color.FromArgb(21, 29, 34);
            Color muted = dark ? Color.FromArgb(151, 148, 168) : Color.FromArgb(104, 99, 123);
            Color accent = dark ? Color.FromArgb(2, 165, 207) : Color.FromArgb(0, 127, 159);
            Color card = dark ? Color.FromArgb(18, 18, 27) : Color.FromArgb(241, 239, 250);
            statusAccent = accent;
            statusWarning = accent;
            BackColor = surface;
            ForeColor = ink;
            var area = Screen.FromControl(owner).WorkingArea;
            maxDialogHeight = area.Height - 32;
            float dpi;
            using (var g = owner.CreateGraphics()) dpi = g.DpiX / 96f;
            if (requestedScale > 0) dpi = requestedScale;
            float scale = Math.Min(dpi, Math.Min((area.Width - 32f) / 480f, (area.Height - 32f) / 350f));
            scale = Math.Max(0.5f, scale);
            Func<int, int> px = n => (int)Math.Round(n * scale);
            radius = px(20);
            footerGap = px(26); actionHeight = px(42); bodyGap = px(20);
            titleFont = new Font(bold, 19 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            bodyFont = new Font(medium, 15 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            buttonFont = new Font(bold, 15 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            int width = px(480), pad = px(28), bodyWidth = width - pad * 2 - px(20);
            var bodyLabel = new Label { Text = message, Font = bodyFont, ForeColor = muted,
                UseMnemonic = false, UseCompatibleTextRendering = true, AutoSize = true,
                MaximumSize = new Size(bodyWidth, 0), Location = Point.Empty };
            int textHeight = bodyLabel.GetPreferredSize(new Size(bodyWidth, 0)).Height + px(12);
            messageText = bodyLabel;
            int contentHeight = Math.Min(textHeight, Math.Max(px(90), area.Height - px(210) - 32));
            ClientSize = new Size(width, px(196) + contentHeight);

            var heading = new Label { Text = title, Font = titleFont, ForeColor = ink,
                Bounds = new Rectangle(pad, px(24), width - pad * 2 - px(36), px(32)), UseMnemonic = false, UseCompatibleTextRendering = true };
            Controls.Add(heading);
            bool warning = icon == MessageBoxIcon.Warning || icon == MessageBoxIcon.Error;
            var marker = new DialogStatusIcon { Kind = icon==MessageBoxIcon.Error?DialogStatusKind.Error:warning?DialogStatusKind.Warning:DialogStatusKind.Information,
                ForeColor = accent,
                Bounds = new Rectangle(pad, px(66), px(28), px(28)) };
            Controls.Add(marker);
            var caption = new Label { Text = warning ? "Обратите внимание" : buttons == MessageBoxButtons.YesNo ? "Подтвердите действие" : "Информация", Font = buttonFont,
                ForeColor = accent, UseMnemonic = false,
                Bounds = new Rectangle(pad + px(38), px(70), width - pad * 2 - px(38), px(26)) };
            Controls.Add(caption);
            statusCaption = caption; statusMarker = marker;
            foreach (var control in new Control[] { heading, marker, caption }) EnableDragging(control);
            MouseDown += (s, e) => { if (e.Y < px(108)) BeginDrag(this, e); };
            MouseMove += MoveDrag;
            MouseUp += EndDrag;
            MouseCaptureChanged += DragCaptureChanged;
            Deactivate += (s, e) => StopDrag();

            var body = new Panel { AutoScroll = true,
                Bounds = new Rectangle(pad, px(108), width - pad * 2, contentHeight) };
            body.Controls.Add(bodyLabel);
            Controls.Add(body);
            messageBody = body;

            bool confirm = buttons == MessageBoxButtons.YesNo;
            var primary = new DialogButton(confirm ? "Продолжить" : "Понятно", buttonFont,
                dark ? Color.FromArgb(2, 165, 207) : Color.FromArgb(0, 127, 159),
                dark ? Color.FromArgb(8, 8, 13) : Color.White, border, px(12)) {
                Bounds = new Rectangle(width - pad - px(150), ClientSize.Height - px(66), px(150), px(42)),
                DialogResult = confirm ? DialogResult.Yes : DialogResult.OK, TabIndex = confirm ? 1 : 0
            };
            Controls.Add(primary);
            primaryAction = primary;
            if (confirm)
            {
                var cancel = new DialogButton("Отмена", buttonFont, card, ink, border, px(12)) {
                    Bounds = new Rectangle(primary.Left - px(130), primary.Top, px(118), px(42)),
                    DialogResult = DialogResult.No, TabIndex = 0
                };
                Controls.Add(cancel);
                cancelAction = cancel;
                AcceptButton = defaultButton == MessageBoxDefaultButton.Button2 ? cancel : primary;
                CancelButton = cancel;
            }
            else
            {
                AcceptButton = primary; CancelButton = primary;
                if (allowDismiss)
                {
                    var dismiss = new DialogButton("Больше не показывать", buttonFont, card, ink, border, px(12)) {
                        Bounds = new Rectangle(pad, primary.Top, primary.Left - pad - px(12), px(42)),
                        DialogResult = DialogResult.Ignore, TabIndex = 1
                    };
                    Controls.Add(dismiss);
                    cancelAction = dismiss;
                }
            }
            var close = new DialogCloseButton(muted, scale) {
                Bounds = new Rectangle(width - px(58), px(22), px(36), px(36)),
                DialogResult = confirm ? DialogResult.No : DialogResult.OK, TabStop = false,
                AccessibleName = "Закрыть"
            };
            Controls.Add(close);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            heading.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            AccessibleName = title;
            Shown += (s, e) => { var target = AcceptButton as Control; if (target != null) target.Focus(); };
            FormClosing += (s, e) => { if (browserBusy) e.Cancel = true; };
            if(warning&&!confirm&&!allowDismiss) {
                // The title already identifies the error; keep its icon beside the explanation.
                statusCaption.Visible=false;statusMarker.Top=px(78);
                messageBody.Top=px(76);messageBody.Left=px(72);messageBody.Width=width-px(100);
                messageText.MaximumSize=new Size(messageBody.Width-px(20),0);
                int preferred=messageText.GetPreferredSize(new Size(messageText.MaximumSize.Width,0)).Height+px(8);
                ClientSize=new Size(width,Math.Min(maxDialogHeight,messageBody.Top+preferred+bodyGap+actionHeight+footerGap));
                UpdateDialogLayout();
            }
        }

        internal void AddBrowserControls(BrowserChoice[] choices, Action<BrowserChoice> restart)
        {
            float scale = actionHeight / 42f;
            Func<int, int> px = n => (int)Math.Round(n * scale);
            browserRowHeight = px(104);
            browserChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed, FlatStyle = FlatStyle.Flat, Font = bodyFont,
                BackColor = surface, ForeColor = ForeColor, ItemHeight = px(24),
                Left = px(28), Width = ClientSize.Width - px(220), TabIndex = 2,
                AccessibleName = "Браузер для перезапуска" };
            browserChoice.DrawItem += (s, e) => {
                e.DrawBackground();
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, browserChoice.Items[e.Index].ToString(), bodyFont,
                    e.Bounds, e.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            };
            foreach (var choice in choices) browserChoice.Items.Add(choice);
            if (choices.Length > 0) browserChoice.SelectedIndex = 0;
            else { browserChoice.Items.Add("Браузеры не найдены"); browserChoice.SelectedIndex = 0; browserChoice.Enabled = false; }
            restartBrowser = new DialogButton("Перезапустить", buttonFont, border, ForeColor, border, px(12)) {
                Left = ClientSize.Width - px(180), Width = px(152), Height = px(36), TabIndex = 3,
                Enabled = choices.Length > 0, AccessibleName = "Перезапустить выбранный браузер"
            };
            restartBrowser.Click += (s, e) => {
                var choice = browserChoice.SelectedItem as BrowserChoice;
                if (choice != null) restart(choice);
            };
            browserHint = new Label { Text = "Сохраните работу перед перезапуском.\nПриватные окна не восстановятся.",
                Font = bodyFont, ForeColor = ForeColor, UseCompatibleTextRendering = true,
                Left = px(28), Width = ClientSize.Width - px(56), Height = px(40), UseMnemonic = false };
            Controls.Add(browserChoice); Controls.Add(restartBrowser); Controls.Add(browserHint);
            ClientSize = new Size(ClientSize.Width, Math.Min(maxDialogHeight, ClientSize.Height + browserRowHeight));
            UpdateDialogLayout();
        }

        internal void SetBrowserBusy(bool busy)
        {
            browserBusy = busy;
            foreach (Control control in Controls)
                if (control is Button) control.Enabled = !busy;
            if (browserChoice != null) browserChoice.Enabled = !busy && browserChoice.SelectedItem is BrowserChoice;
            if (restartBrowser != null) restartBrowser.Text = busy ? "Подождите…" : "Перезапустить";
            if (supportPanel != null) supportPanel.Enabled = !busy;
        }

        internal void AddDnsTestControls(string[] providers, int selected, Action<int> run)
        {
            dnsTestLayout=true;
            statusCaption.Text = "Выберите DNS для теста";
            float scale = actionHeight / 42f;
            Func<int, int> px = n => (int)Math.Round(n * scale);
            browserRowHeight = px(104);
            browserChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = bodyFont,
                BackColor = surface, ForeColor = ForeColor, Left = px(28), Width = ClientSize.Width - px(220),
                AccessibleName = "DNS для тестирования" };
            browserChoice.Items.AddRange(providers); browserChoice.SelectedIndex = selected;
            restartBrowser = new DialogButton("Проверить", buttonFont, border, ForeColor, border, px(12)) {
                Left = ClientSize.Width - px(180), Width = px(152), Height = px(36), AccessibleName = "Проверить выбранный DNS" };
            restartBrowser.Click += (s, e) => run(browserChoice.SelectedIndex);
            browserHint = new Label { Text = "Выбор здесь только для теста.\nСистемные настройки не меняются.", Font = bodyFont,
                ForeColor = ForeColor, Left = px(28), Width = ClientSize.Width - px(56), Height = px(40), UseMnemonic = false };
            Controls.Add(browserChoice); Controls.Add(restartBrowser); Controls.Add(browserHint);
            testDetailsToggle=new LinkLabel {Text="Подробности",Font=bodyFont,LinkColor=statusAccent,ActiveLinkColor=statusAccent,VisitedLinkColor=statusAccent,Left=px(28),Width=px(220),Height=px(24),Visible=false};
            testDetailsToggle.LinkClicked+=(s,e)=>{testDetailsExpanded=!testDetailsExpanded;messageText.Text=testDetailsExpanded?testFullText:testSummary;UpdateDialogLayout();};
            Controls.Add(testDetailsToggle);
            testCopyResults=new DialogButton("Копировать",bodyFont,border,ForeColor,border,px(8)){AccessibleName="Скопировать результаты и технические сведения проверки",Visible=false};
            testCopyResults.Click+=(s,e)=>CopyDnsTestResults(text=>Clipboard.SetText(text,TextDataFormat.UnicodeText));
            Controls.Add(testCopyResults);
            UpdateDialogLayout();
        }
        internal void ShowDnsTestStatus(string text, string caption, bool busy, bool warning, string summary=null,bool isResult=true)
        {
            testFullText=text;testSummary=summary;testDetailsExpanded=false;
            diagnosticDetails=null;testResultReady=!busy&&isResult;
            testCopyResults.Visible=testResultReady;testCopyResults.Enabled=testResultReady;testCopyResults.Text="Копировать";
            bool details=!busy&&summary!=null;
            testDetailsToggle.Visible=details;
            messageText.Text=details?summary:text; messageBody.AutoScrollPosition = Point.Empty;
            statusCaption.Text = caption;
            statusCaption.ForeColor = warning ? statusWarning : statusAccent;
            statusMarker.Kind = warning ? DialogStatusKind.Warning : busy ? DialogStatusKind.Search : DialogStatusKind.Success;
            statusMarker.ForeColor = statusCaption.ForeColor;
            browserChoice.Enabled = !busy; restartBrowser.Enabled = !busy;
            if(serviceTestChoice!=null)serviceTestChoice.Enabled=!busy;
            restartBrowser.Text = busy ? "Проверяем…" : "Проверить";
            SetSupportVisible(warning && !busy && DiagnosticReport.ShouldOffer(Text, text));
            UpdateDialogLayout(); Invalidate(); Update();
        }
        internal bool CopyDnsTestResults(Action<string> copy)
        {
            if(!dnsTestLayout||autoLayout||!testResultReady)return false;
            string text=DiagnosticReport.Scrub("Код Всего · Smart DNS "+MainForm.VersionLabel+"\nDNS: "+browserChoice.Text+
                "\nПроверка: "+(serviceTestChoice==null?"Только DNS":serviceTestChoice.Text)+"\n\n"+DiagnosticMessage);
            if(text.Length>65536)text=text.Substring(0,65536)+"\n[Результат сокращён]";
            try{copy(text);testCopyResults.Text="Скопировано";return true;}
            catch(System.Runtime.InteropServices.ExternalException){testCopyResults.Text="Буфер занят";return false;}
        }

        internal void AddServiceTestControls(int selected)
        {
            float scale=actionHeight/42f;
            Func<int,int> px=n=>(int)Math.Round(n*scale);
            serviceTestChoice=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Font=bodyFont,BackColor=surface,ForeColor=ForeColor,
                Left=px(28),Width=ClientSize.Width-px(56),AccessibleName="Сервис для проверки доступности"};
            serviceTestChoice.Items.Add("Только DNS");
            serviceTestChoice.Items.AddRange(ServiceAvailability.Targets.Select(t=>t.Name+" · сайт и API (без ключа)").ToArray());
            serviceTestChoice.Items.Add("Фактический DNS Windows · MangoProxy");
            serviceTestChoice.SelectedIndex=selected>=0&&selected<ServiceAvailability.Targets.Length?selected+1:0;
            browserRowHeight=px(96);browserHint.Visible=false;
            restartBrowser.AccessibleName="Проверить DNS и доступность сервиса";
            Controls.Add(serviceTestChoice);
            EventHandler changed=(s,e)=>{SetDiagnosticDetails(null,false);ShowDnsTestStatus(TestService==ResolverCheck.ServiceIndex?"MangoProxy увидит DNS-узлы для уникальных тестовых имён. Сравним системный путь с выбранным провайдером. Настройки не меняются, ключи и журнал не отправляются.":"Нажмите «Проверить» для выбранного DNS и сервиса.","Готово к проверке",false,false,null,false);};
            serviceTestChoice.SelectedIndexChanged+=changed;browserChoice.SelectedIndexChanged+=changed;
            UpdateDialogLayout();
        }
        internal void SelectResolverTest() { serviceTestChoice.SelectedIndex=ResolverCheck.ServiceIndex+1; }
        internal void AddAutoDnsControls(string[] goals, int selected, Action<int> run, Action selectionChanged = null)
        {
            AddDnsTestControls(goals,selected,run);
            autoCardColor=surface.R<100?Color.FromArgb(18,18,27):Color.FromArgb(241,239,250);
            restartBrowser.Visible=false;restartBrowser.TabStop=false;
            statusMarker.Visible=false;
            messageBody.AutoScroll=false;messageBody.BackColor=autoCardColor;
            messageText.BackColor=autoCardColor;statusCaption.BackColor=autoCardColor;
            browserChoice.AccessibleName="Сервис для автоматического подбора DNS";
            browserChoice.BackColor=autoCardColor;
            browserChoice.DrawMode=DrawMode.OwnerDrawFixed;
            browserChoice.ItemHeight=(int)Math.Round(28*actionHeight/42f);
            browserChoice.DrawItem+=(s,e)=> {
                using(var fill=new SolidBrush(browserChoice.BackColor))e.Graphics.FillRectangle(fill,e.Bounds);
                string item=e.Index<0?browserChoice.Text:Convert.ToString(browserChoice.Items[e.Index]);
                TextRenderer.DrawText(e.Graphics,item,bodyFont,e.Bounds,browserChoice.ForeColor,
                    TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
            browserHint.Text="Для какого сервиса?";
            autoScopeNote=new Label {Text="Нестабильный режим. Меняет DNS компьютера.\nВход и генерация не проверялись.",Font=bodyFont,ForeColor=messageText.ForeColor,UseMnemonic=false};
            autoDetailsToggle=new LinkLabel {Text="Результаты проверки",Font=bodyFont,LinkColor=statusAccent,ActiveLinkColor=statusAccent,UseMnemonic=false,Visible=false};
            autoDetails=new Panel {AutoScroll=true,Visible=false,BackColor=autoCardColor};
            autoDetailsText=new Label {AutoSize=true,Font=bodyFont,ForeColor=messageText.ForeColor,UseMnemonic=false,BackColor=autoCardColor};
            autoDetails.Controls.Add(autoDetailsText);
            autoDetailsToggle.LinkClicked+=(s,e)=>ToggleAutoDetails();
            Controls.Add(autoScopeNote);Controls.Add(autoDetailsToggle);Controls.Add(autoDetails);
            autoCopyLog=new DialogButton("Копировать лог",bodyFont,border,ForeColor,border,(int)Math.Round(8*actionHeight/42f)){AccessibleName="Скопировать технический лог Авто",Visible=false};
            autoCopyLog.Click+=(s,e)=>CopyAutoLog(text=>Clipboard.SetText(text,TextDataFormat.UnicodeText));
            Controls.Add(autoCopyLog);
            primaryAction.Click+=(s,e)=> {if(!autoReady&&!autoRunning)run(browserChoice.SelectedIndex);};
            var close=Controls.OfType<DialogCloseButton>().Single();
            close.DialogResult=DialogResult.Cancel; CancelButton=close;
            AcceptButton=primaryAction;
            if(selectionChanged!=null)browserChoice.SelectedIndexChanged+=(s,e)=>selectionChanged();
            autoLayout=true;
            ShowAutoDnsStatus("Нажмите «Подобрать DNS».\nПроверим подходящие варианты.","Найдём подходящий DNS",false,false);
        }
        internal void ShowAutoDnsStatus(string text,string caption,bool running,bool warning)
        {
            autoRunning=running;autoReady=false;autoDetailsExpanded=false;
            messageText.Text=text;statusCaption.Text=caption;statusCaption.ForeColor=statusAccent;
            browserChoice.Enabled=!running;
            primaryAction.DialogResult=DialogResult.None;
            primaryAction.Enabled=!running;
            primaryAction.Text=running?"Подбираем…":"Подобрать DNS";
            if(autoDetailsToggle!=null)autoDetailsToggle.Visible=false;
            SetSupportVisible(false);
            diagnosticDetails=null;
            if(autoDetailsText!=null)autoDetailsText.Text="";
            UpdateAutoCopy();
            UpdateDialogLayout();Invalidate();
        }
        internal void ShowAutoDnsResult(string caption,string summary,string details,string connectLabel=null)
        {
            ShowAutoDnsStatus(summary,caption,false,false);
            autoReady=connectLabel!=null;
            primaryAction.DialogResult=autoReady?DialogResult.OK:DialogResult.None;
            primaryAction.Text=connectLabel??"Повторить подбор";
            autoDetailsText.Text=details??"";
            autoDetailsToggle.Visible=!string.IsNullOrEmpty(details);
            UpdateDialogLayout();Invalidate();
        }
        internal void ToggleAutoDetails()
        {autoDetailsExpanded=!autoDetailsExpanded;UpdateDialogLayout();Invalidate();}
        void UpdateAutoCopy()
        {
            if(autoCopyLog==null)return;
            bool available=autoLayout&&!autoRunning&&!string.IsNullOrEmpty(diagnosticDetails);
            autoCopyLog.Visible=available;autoCopyLog.Enabled=available;autoCopyLog.Text="Копировать лог";
        }
        internal bool CopyAutoLog(Action<string> copy)
        {
            if(!autoLayout||autoRunning||string.IsNullOrEmpty(diagnosticDetails))return false;
            string text=DiagnosticReport.Scrub("Код Всего · Smart DNS "+MainForm.VersionLabel+"\nАвто · "+browserChoice.Text+"\n\n"+DiagnosticMessage);
            if(text.Length>65536)text=text.Substring(0,65536)+"\n[Лог сокращён]";
            try {copy(text);autoCopyLog.Text="Скопировано";return true;}
            catch(System.Runtime.InteropServices.ExternalException){autoCopyLog.Text="Буфер занят";return false;}
        }
        void LayoutAutoDns()
        {
            float scale=actionHeight/42f;Func<int,int> px=n=>(int)Math.Round(n*scale);
            int bodyHeight=Math.Max(px(60),Math.Min(px(104),messageText.GetPreferredSize(new Size(px(352),0)).Height));
            int extra=bodyHeight-px(60);
            var wantedSize=new Size(px(440),Math.Min(maxDialogHeight,px((autoDetailsExpanded?602:452)+(supportShown?92:0))+extra));
            if(ClientSize!=wantedSize)ClientSize=wantedSize;
            var heading=Controls.OfType<Label>().First(label=>label.Font==titleFont);
            heading.Bounds=new Rectangle(px(28),px(24),ClientSize.Width-px(92),px(32));
            browserHint.Bounds=new Rectangle(px(28),px(72),ClientSize.Width-px(56),px(24));
            browserChoice.Left=px(28);browserChoice.Top=px(102);browserChoice.Width=ClientSize.Width-px(56);
            autoCardBounds=new Rectangle(px(28),px(156),ClientSize.Width-px(56),px(112)+extra);
            statusCaption.Bounds=new Rectangle(px(44),px(170),ClientSize.Width-px(88),px(26));
            messageBody.Bounds=new Rectangle(px(44),px(202),ClientSize.Width-px(88),bodyHeight);
            messageBody.AutoScroll=messageText.GetPreferredSize(new Size(messageBody.Width,0)).Height>bodyHeight;
            messageText.MaximumSize=new Size(messageBody.Width,0);
            autoScopeNote.Bounds=new Rectangle(px(28),px(286)+extra,ClientSize.Width-px(56),px(44));
            autoDetailsToggle.Bounds=new Rectangle(px(28),px(336)+extra,ClientSize.Width-px(56)-px(174),px(26));
            autoCopyLog.Bounds=new Rectangle(ClientSize.Width-px(188),px(334)+extra,px(160),px(30));
            autoDetailsToggle.Text=autoDetailsExpanded?"Скрыть результаты":"Результаты проверки";
            primaryAction.Bounds=new Rectangle(px(28),ClientSize.Height-footerGap-actionHeight,ClientSize.Width-px(56),actionHeight);
            int detailsBottom=primaryAction.Top;
            if(supportShown&&supportPanel!=null) {
                supportPanel.Bounds=new Rectangle(px(28),primaryAction.Top-bodyGap-supportPanel.Height,ClientSize.Width-px(56),supportPanel.Height);
                supportHint.Width=supportPanel.Width;
                foreach(var link in supportPanel.Controls.OfType<LinkLabel>())link.Width=supportPanel.Width-link.Left;
                detailsBottom=supportPanel.Top;
            }
            autoDetails.Visible=autoDetailsExpanded;
            autoDetails.Bounds=new Rectangle(px(28),px(370)+extra,ClientSize.Width-px(56),Math.Max(0,detailsBottom-px(386)-extra));
            autoDetailsText.MaximumSize=new Size(autoDetails.Width-px(24),0);
            autoDetailsText.Location=new Point(px(8),px(8));
        }

        internal void ShowBrowserStatus(string text, bool warning)
        {
            messageText.Text = text;
            messageBody.AutoScrollPosition = Point.Empty;
            statusCaption.Text = warning ? "Не удалось перезапустить" : browserBusy ? "Перезапускаем…" : "Браузер запущен";
            statusCaption.ForeColor = warning ? statusWarning : statusAccent;
            statusMarker.Kind = warning ? DialogStatusKind.Error : browserBusy ? DialogStatusKind.Search : DialogStatusKind.Success;
            statusMarker.ForeColor = warning ? statusWarning : statusAccent;
            SetSupportVisible(warning && DiagnosticReport.ShouldOffer(Text, text));
            UpdateDialogLayout(); Invalidate(); Update();
        }
        internal void AddUpdateControls(Action action)
        {
            primaryAction.DialogResult=DialogResult.None;
            primaryAction.Click+=(s,e)=>action();
            var close=Controls.OfType<DialogCloseButton>().Single();
            close.DialogResult=DialogResult.Cancel;CancelButton=close;AcceptButton=primaryAction;
        }
        internal void AddSettingsControls(bool backupAvailable,Action openBackup,string[] goals,int selectedGoal,Action<string> openSupport)
        {
            float scale=actionHeight/42f;Func<int,int> px=n=>(int)Math.Round(n*scale);
            messageBody.Visible=false;statusMarker.Visible=false;statusCaption.Visible=false;
            settingsCardColor=surface.R<100?Color.FromArgb(18,18,27):Color.FromArgb(241,239,250);
            settingsConnectionPanel=new DialogSettingsCard(settingsCardColor,border,px(12));
            settingsBackupPanel=new DialogSettingsCard(settingsCardColor,border,px(12));
            Controls.Add(settingsConnectionPanel);Controls.Add(settingsBackupPanel);
            Controls.Add(new Label{Text="Выбор DNS",Font=buttonFont,ForeColor=ForeColor,Bounds=new Rectangle(px(28),px(72),ClientSize.Width-px(56),px(24))});
            settingsManualMode=new DialogModeOption("Вручную",buttonFont,settingsCardColor,ForeColor,statusAccent,border,px(10));
            settingsAutoMode=new DialogModeOption("Авто · beta",buttonFont,settingsCardColor,ForeColor,statusAccent,border,px(10));
            settingsManualMode.AccessibleName="Ручной выбор DNS";settingsAutoMode.AccessibleName="Автоматический подбор DNS для сервиса — beta, нестабильный режим";
            settingsConnectionPanel.Controls.Add(settingsManualMode);settingsConnectionPanel.Controls.Add(settingsAutoMode);
            settingsGoalChoice=new DialogServicePicker(goals,bodyFont,settingsCardColor,ForeColor,statusAccent,border,px(10)){AccessibleName="Сервис по умолчанию"};
            settingsGoalChoice.SelectedIndex=selectedGoal>=0&&selectedGoal<goals.Length?selectedGoal:0;settingsConnectionPanel.Controls.Add(settingsGoalChoice);
            settingsModeHint=new Label{Font=bodyFont,ForeColor=messageText.ForeColor,BackColor=settingsCardColor,UseMnemonic=false};settingsConnectionPanel.Controls.Add(settingsModeHint);
            settingsBackupHeading=new Label{Text="Резервные копии",Font=buttonFont,ForeColor=ForeColor};Controls.Add(settingsBackupHeading);
            settingsBackupTitle=new Label{Text="Правила Antigravity",Font=bodyFont,ForeColor=ForeColor,BackColor=settingsCardColor};settingsBackupPanel.Controls.Add(settingsBackupTitle);
            settingsBackupStatus=new Label{Text=backupAvailable?"Сохранены на компьютере":"Пока нет копий",Font=bodyFont,ForeColor=messageText.ForeColor,BackColor=settingsCardColor};settingsBackupPanel.Controls.Add(settingsBackupStatus);
            settingsOpenBackup=new DialogButton("Открыть",buttonFont,border,ForeColor,border,px(10)){Enabled=backupAvailable,AccessibleName="Открыть папку копий правил Antigravity"};
            ((DialogButton)settingsOpenBackup).SurfaceColor=settingsCardColor;
            settingsOpenBackup.Click+=(s,e)=>openBackup();settingsBackupPanel.Controls.Add(settingsOpenBackup);
            settingsSupportHeading=new Label{Text="Сообщить об ошибке",Font=buttonFont,ForeColor=ForeColor};Controls.Add(settingsSupportHeading);
            settingsGithub=new DialogButton("GitHub Issues",buttonFont,settingsCardColor,ForeColor,border,px(10)){AccessibleName="Сообщить об ошибке в GitHub Issues"};
            settingsTelegram=new DialogButton("Написать в Telegram",buttonFont,settingsCardColor,ForeColor,border,px(10)){AccessibleName="Написать разработчику @TotalCodeAdmin"};
            settingsGithub.Click+=(s,e)=>openSupport("https://github.com/Kam300/KodVsego-SmartDNS/issues/new");
            settingsTelegram.Click+=(s,e)=>openSupport("https://t.me/TotalCodeAdmin");
            Controls.Add(settingsGithub);Controls.Add(settingsTelegram);
            settingsSource=new LinkLabel{Text="Исходный код · Open Source",Font=bodyFont,LinkColor=statusAccent,ActiveLinkColor=statusAccent,VisitedLinkColor=statusAccent,UseMnemonic=false,AccessibleName="Открыть исходный код Smart DNS на GitHub"};
            settingsSource.LinkClicked+=(s,e)=>openSupport("https://github.com/Kam300/KodVsego-SmartDNS");Controls.Add(settingsSource);
            cancelAction=new DialogButton("Отмена",buttonFont,settingsCardColor,ForeColor,border,px(10)){DialogResult=DialogResult.Cancel};Controls.Add(cancelAction);
            var close=Controls.OfType<DialogCloseButton>().Single();close.DialogResult=DialogResult.Cancel;CancelButton=cancelAction;
            primaryAction.Text="Сохранить";primaryAction.DialogResult=DialogResult.OK;primaryAction.Enabled=true;AcceptButton=primaryAction;
            settingsAutoMode.Checked=selectedGoal>=0;settingsManualMode.Checked=selectedGoal<0;
            settingsManualMode.CheckedChanged+=(s,e)=>UpdateDialogLayout();settingsAutoMode.CheckedChanged+=(s,e)=>UpdateDialogLayout();
            settingsLayout=true;
            UpdateDialogLayout();
        }
        internal void AddTunnelSettingsAction(Action open)
        {
            settingsTunnel=new LinkLabel{Text="DNS-туннель · beta",Font=bodyFont,LinkColor=statusAccent,ActiveLinkColor=statusAccent,UseMnemonic=false,AccessibleName="Открыть тестовый DNS-туннель"};
            settingsTunnel.LinkClicked+=(s,e)=>open();Controls.Add(settingsTunnel);UpdateDialogLayout();
        }
        internal void AddLogSettingsAction(Action<Action<string>> save)
        {
            settingsTunnel=new LinkLabel{Text="Скачать журнал всей программы (.txt)",Font=bodyFont,LinkColor=statusAccent,ActiveLinkColor=statusAccent,UseMnemonic=false,AccessibleName="Сохранить подробный журнал текущего запуска программы"};
            settingsTunnel.LinkClicked+=(s,e)=>save(text=>{if(!IsDisposed)settingsTunnel.Text=text;});Controls.Add(settingsTunnel);UpdateDialogLayout();
        }
        internal void AddResolverSettingsAction(bool enabled)
        {
            settingsResolverCheck=new CheckBox{Text="Автопроверка DNS · MangoProxy",Checked=enabled,Font=bodyFont,ForeColor=ForeColor,BackColor=surface,UseVisualStyleBackColor=false,AccessibleName="После включения проверять фактический DNS через MangoProxy; отправляются только тестовые имена"};
            Controls.Add(settingsResolverCheck);UpdateDialogLayout();
        }
        void LayoutSettings()
        {
            SuspendLayout();settingsConnectionPanel.SuspendLayout();settingsBackupPanel.SuspendLayout();
            try {
            float scale=actionHeight/42f;Func<int,int> px=n=>(int)Math.Round(n*scale);bool automatic=settingsAutoMode.Checked;
            int height=Math.Min(maxDialogHeight,px((automatic?574:518)+(settingsTunnel==null?0:32)+(settingsResolverCheck==null?0:30)));if(ClientSize.Height!=height)ClientSize=new Size(ClientSize.Width,height);
            int innerWidth=ClientSize.Width-px(88),half=(innerWidth-px(8))/2;
            settingsConnectionBounds=new Rectangle(px(28),px(100),ClientSize.Width-px(56),px(automatic?148:92));
            settingsConnectionPanel.Bounds=settingsConnectionBounds;
            settingsManualMode.Bounds=new Rectangle(px(16),px(16),half,px(34));settingsAutoMode.Bounds=new Rectangle(px(16)+half+px(8),px(16),half,px(34));
            settingsModeHint.Text=automatic?"Подключать выбранный сервис одной кнопкой.":"Выбирайте DNS на главном экране.";
            settingsModeHint.Bounds=new Rectangle(px(16),px(60),innerWidth,px(24));
            settingsGoalChoice.Visible=automatic;settingsGoalChoice.Enabled=automatic;settingsGoalChoice.Bounds=new Rectangle(px(16),px(96),innerWidth,px(36));
            settingsBackupHeading.Bounds=new Rectangle(px(28),settingsConnectionBounds.Bottom+px(20),ClientSize.Width-px(56),px(24));
            settingsBackupBounds=new Rectangle(px(28),settingsBackupHeading.Bottom+px(8),ClientSize.Width-px(56),px(64));
            settingsBackupPanel.Bounds=settingsBackupBounds;
            settingsBackupTitle.Bounds=new Rectangle(px(16),px(9),ClientSize.Width-px(232),px(24));
            settingsBackupStatus.Bounds=new Rectangle(px(16),px(34),ClientSize.Width-px(232),px(22));
            settingsOpenBackup.Bounds=new Rectangle(settingsBackupPanel.Width-px(128),px(14),px(112),px(36));
            settingsSupportHeading.Bounds=new Rectangle(px(28),settingsBackupBounds.Bottom+px(16),ClientSize.Width-px(56),px(24));
            int supportWidth=(ClientSize.Width-px(64))/2;
            settingsGithub.Bounds=new Rectangle(px(28),settingsSupportHeading.Bottom+px(8),supportWidth,px(36));
            settingsTelegram.Bounds=new Rectangle(px(36)+supportWidth,settingsGithub.Top,supportWidth,px(36));
            settingsSource.Bounds=new Rectangle(px(28),settingsGithub.Bottom+px(12),ClientSize.Width-px(56),px(24));
            if(settingsTunnel!=null)settingsTunnel.Bounds=new Rectangle(px(28),settingsSource.Bottom+px(6),ClientSize.Width-px(56),px(24));
            if(settingsResolverCheck!=null)settingsResolverCheck.Bounds=new Rectangle(px(28),(settingsTunnel!=null?settingsTunnel.Bottom:settingsSource.Bottom)+px(6),ClientSize.Width-px(56),px(24));
            primaryAction.Bounds=new Rectangle(ClientSize.Width-px(178),ClientSize.Height-footerGap-actionHeight,px(150),actionHeight);
            cancelAction.Bounds=new Rectangle(px(28),primaryAction.Top,px(112),actionHeight);
            }finally{settingsBackupPanel.ResumeLayout(false);settingsConnectionPanel.ResumeLayout(false);ResumeLayout(false);}
        }
        internal void ShowUpdateStatus(string text,string caption,string action,bool enabled)
        {
            messageText.Text=text;messageBody.AutoScrollPosition=Point.Empty;
            float scale=actionHeight/42f;
            int textHeight=messageText.GetPreferredSize(new Size(messageText.MaximumSize.Width,0)).Height;
            int height=Math.Min(maxDialogHeight,(int)Math.Round(190*scale)+Math.Max((int)Math.Round(90*scale),textHeight+(int)Math.Round(12*scale)));
            if(ClientSize.Height!=height)ClientSize=new Size(ClientSize.Width,height);
            statusCaption.Text=caption;statusCaption.ForeColor=statusAccent;
            statusCaption.Font=buttonFont;
            statusCaption.Left=statusMarker.Right+(int)Math.Round(10*scale);
            statusCaption.Width=ClientSize.Width-statusCaption.Left-messageBody.Left;
            statusMarker.Visible=true;
            statusMarker.Kind=enabled&&action.StartsWith("Повторить",StringComparison.Ordinal)?DialogStatusKind.Error:
                action=="Обновить"||action.StartsWith("Скачиваем",StringComparison.Ordinal)?DialogStatusKind.Download:
                !enabled?DialogStatusKind.Search:DialogStatusKind.Success;
            primaryAction.Enabled=enabled;SetPrimaryText(action);
            SetSupportVisible(false);UpdateDialogLayout();Invalidate();
        }
        internal void AddSupportControls(Func<string, string, string> buildReport, bool visible)
        {
            makeReport = buildReport;
            float scale = actionHeight / 42f;
            Func<int,int> px = n => (int)Math.Round(n * scale);
            supportPanel = new Panel {Left=px(28), Width=ClientSize.Width-px(56), Height=px(72), Visible=false};
            var save = new DialogButton("Сохранить лог", buttonFont, border, ForeColor, border, px(10)) {
                Bounds=new Rectangle(0,0,px(204),px(34)), AccessibleName="Сохранить диагностический отчёт"};
            var contact = new LinkLabel {Text="@TotalCodeAdmin ↗", Font=bodyFont, LinkColor=statusAccent, ActiveLinkColor=statusAccent,
                VisitedLinkColor=statusAccent, Bounds=new Rectangle(px(218),px(8),supportPanel.Width-px(218),px(24)), AutoSize=false,
                AccessibleName="Написать разработчику в Telegram"};
            supportHint = new Label {Text="Сохраните файл и отправьте разработчику.", Font=bodyFont, ForeColor=ForeColor,
                Bounds=new Rectangle(0,px(39),supportPanel.Width,px(32)), UseMnemonic=false, UseCompatibleTextRendering=true};
            save.Click += (s,e) => {
                using(var dialog = new SaveFileDialog {Title="Сохранить отчёт для разработчика", Filter="Текстовый отчёт (*.txt)|*.txt", DefaultExt="txt", AddExtension=true,
                    FileName="SmartDNS-"+MainForm.VersionLabel+"-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".txt", OverwritePrompt=true})
                {
                    if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                    try {
                        save.Enabled=false;
                        supportHint.Text="Собираем отчёт…"; Update();
                        System.IO.File.WriteAllText(dialog.FileName, makeReport(Text,DiagnosticMessage), new System.Text.UTF8Encoding(true));
                        supportHint.Text="Отчёт сохранён. Отправьте @TotalCodeAdmin.";
                    }
                    catch(Exception ex) {supportHint.Text="Не удалось сохранить: "+DiagnosticReport.Scrub(ex.Message);}
                    finally {save.Enabled=true;}
                }
            };
            contact.LinkClicked += (s,e) => {
                try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiagnosticReport.Contact) {UseShellExecute=true});}
                catch(Exception ex) {supportHint.Text="Не удалось открыть Telegram: "+DiagnosticReport.Scrub(ex.Message);}
            };
            supportPanel.Controls.Add(save); supportPanel.Controls.Add(contact); supportPanel.Controls.Add(supportHint);
            Controls.Add(supportPanel);
            SetSupportVisible(visible);
        }
        void SetSupportVisible(bool visible)
        {
            if(supportPanel==null || supportShown==visible) return;
            supportShown=visible; supportPanel.Visible=visible;
            if(autoLayout){UpdateDialogLayout();return;}
            if(visible) {
                DiagnosticReport.Record(Text,messageText.Text);
                supportGrowth=Math.Min(maxDialogHeight-ClientSize.Height,supportPanel.Height+bodyGap);
                ClientSize=new Size(ClientSize.Width, ClientSize.Height+supportGrowth);
            }
            else ClientSize=new Size(ClientSize.Width, Math.Max(1,ClientSize.Height-supportGrowth));
            UpdateDialogLayout();
        }

        internal static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal void SetPrimaryText(string text)
        {
            if(autoLayout){primaryAction.Text=text;return;}
            float scale = actionHeight / 42f;
            int right = primaryAction.Right;
            int gap = (int)Math.Round(12 * scale);
            int available = ClientSize.Width - (int)Math.Round(56 * scale) - (cancelAction == null ? 0 : cancelAction.Width + gap);
            primaryAction.Text = text;
            primaryAction.Width = Math.Min(available, TextRenderer.MeasureText(text, buttonFont).Width + (int)Math.Round(32 * scale));
            primaryAction.Left = right - primaryAction.Width;
            if (cancelAction != null) cancelAction.Left = primaryAction.Left - gap - cancelAction.Width;
        }

        void EnableDragging(Control control)
        {
            control.MouseDown += BeginDrag;
            control.MouseMove += MoveDrag;
            control.MouseUp += EndDrag;
            control.MouseCaptureChanged += DragCaptureChanged;
        }
        void BeginDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            StopDrag();
            dragControl = (Control)sender;
            dragPointerStart = dragControl.PointToScreen(e.Location);
            dragWindowStart = Location;
            // Keep receiving mouse events when the pointer leaves the title area.
            dragControl.Capture = true;
        }
        void MoveDrag(object sender, MouseEventArgs e)
        {
            if (dragControl == null || sender != dragControl || e.Button != MouseButtons.Left) return;
            Point pointer = dragControl.PointToScreen(e.Location);
            Location = new Point(dragWindowStart.X + pointer.X - dragPointerStart.X,
                dragWindowStart.Y + pointer.Y - dragPointerStart.Y);
        }
        void EndDrag(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) StopDrag();
        }
        void DragCaptureChanged(object sender, EventArgs e)
        {
            if (sender == dragControl && !dragControl.Capture) StopDrag();
        }
        void StopDrag()
        {
            var control = dragControl;
            dragControl = null;
            if (control != null && !control.IsDisposed) control.Capture = false;
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateDialogLayout();
        }
        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            UpdateDialogLayout();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Windows may constrain the form to the available screen during Show.
            UpdateDialogLayout();
        }
        void UpdateDialogLayout()
        {
            if(arranging)return;arranging=true;
            try {
            if(settingsLayout)LayoutSettings();
            else if(autoLayout)LayoutAutoDns();
            else if (primaryAction != null && messageBody != null)
            {
                float unit=actionHeight/42f;
                if(dnsTestLayout) {
                    int pad=(int)Math.Round(28*unit);
                    primaryAction.Left=ClientSize.Width-pad-primaryAction.Width;
                    messageBody.Width=Math.Max(1,ClientSize.Width-2*pad);
                    messageText.MaximumSize=new Size(Math.Max(1,messageBody.Width-(int)Math.Round(20*unit)),0);
                    statusCaption.Width=Math.Max(1,ClientSize.Width-pad-statusCaption.Left);
                    restartBrowser.Left=ClientSize.Width-pad-restartBrowser.Width;
                    browserChoice.Width=Math.Max(1,restartBrowser.Left-browserChoice.Left-(int)Math.Round(12*unit));
                    if(serviceTestChoice!=null)serviceTestChoice.Width=Math.Max(1,ClientSize.Width-2*pad);
                    if(supportPanel!=null) {
                        supportPanel.Width=Math.Max(1,ClientSize.Width-2*pad);
                        foreach(Control control in supportPanel.Controls)
                            if(control is Label)control.Width=Math.Max(1,supportPanel.Width-control.Left);
                    }
                    int preferred=messageText.GetPreferredSize(new Size(messageText.MaximumSize.Width,0)).Height+(int)Math.Round(8*unit);
                    int detailHeight=testDetailsToggle.Visible||testResultReady?(int)Math.Round(34*unit):0;
                    int extras=footerGap+actionHeight+bodyGap+browserRowHeight+detailHeight+(supportShown&&supportPanel!=null?supportPanel.Height+bodyGap:0);
                    int height=Math.Min(maxDialogHeight,messageBody.Top+Math.Min(preferred,(int)Math.Round(190*unit))+extras);
                    if(ClientSize.Height!=height)ClientSize=new Size(ClientSize.Width,height);
                    testDetailsToggle.Text=testDetailsExpanded?"Скрыть подробности":"Подробности";
                }
                primaryAction.Top = Math.Max(0, ClientSize.Height - footerGap - actionHeight);
                if (cancelAction != null) cancelAction.Top = primaryAction.Top;
                int bodyBottom = primaryAction.Top - bodyGap;
                int contentBottom = primaryAction.Top;
                if(supportShown && supportPanel != null) {
                    supportPanel.Top=primaryAction.Top-bodyGap-supportPanel.Height;
                    contentBottom=supportPanel.Top;
                    bodyBottom=contentBottom-bodyGap;
                }
                if (browserChoice != null && restartBrowser != null)
                {
                    restartBrowser.Top = contentBottom - (int)Math.Round(62 * actionHeight / 42f);
                    browserChoice.Top = restartBrowser.Top + (restartBrowser.Height - browserChoice.Height) / 2;
                    browserHint.Top = contentBottom - browserRowHeight;
                    if(serviceTestChoice!=null) {
                        serviceTestChoice.Top=contentBottom-(int)Math.Round(92*unit);
                    }
                    bodyBottom = browserHint.Top - bodyGap;
                }
                if(dnsTestLayout&&(testDetailsToggle.Visible||testResultReady)) {
                    int rowTop=bodyBottom-(int)Math.Round(30*unit);
                    testCopyResults.Bounds=new Rectangle(ClientSize.Width-(int)Math.Round(188*unit),rowTop,(int)Math.Round(160*unit),(int)Math.Round(30*unit));
                    testDetailsToggle.Top=rowTop+(int)Math.Round(3*unit);
                    testDetailsToggle.Width=Math.Max(1,testCopyResults.Left-testDetailsToggle.Left-(int)Math.Round(12*unit));
                    bodyBottom=rowTop-(int)Math.Round(6*unit);
                }
                messageBody.Height = Math.Max(0, bodyBottom - messageBody.Top);
            }
            if (IsHandleCreated && ClientSize.Width > 0 && ClientSize.Height > 0 && radius > 0)
            {
                var previous = Region;
                using (var path = Rounded(ClientRectangle, radius)) Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
            }
            finally {arranging=false;}
            if(settingsLayout)Invalidate(true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if(autoLayout)using(var path=Rounded(autoCardBounds,radius/2f))
            using(var fill=new SolidBrush(autoCardColor))
            using(var line=new Pen(border)){e.Graphics.FillPath(fill,path);e.Graphics.DrawPath(line,path);}
            using (var path = Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius))
            using (var pen = new Pen(border)) e.Graphics.DrawPath(pen, path);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { titleFont.Dispose(); bodyFont.Dispose(); buttonFont.Dispose(); }
        }
    }

    sealed class DialogSettingsCard : Panel
    {
        readonly Color border;readonly int radius;
        internal DialogSettingsCard(Color fill,Color border,int radius)
        {BackColor=fill;this.border=border;this.radius=radius;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        protected override void OnSizeChanged(EventArgs e)
        {base.OnSizeChanged(e);if(Width>0&&Height>0&&radius>0){var previous=Region;using(var path=StyledDialog.Rounded(ClientRectangle,radius))Region=new Region(path);if(previous!=null)previous.Dispose();}Invalidate(true);}
        protected override void OnPaintBackground(PaintEventArgs e)
        {e.Graphics.Clear(Parent==null?BackColor:Parent.BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var path=StyledDialog.Rounded(ClientRectangle,radius))using(var fill=new SolidBrush(BackColor))e.Graphics.FillPath(fill,path);}
        protected override void OnPaint(PaintEventArgs e)
        {base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var path=StyledDialog.Rounded(new RectangleF(.5f,.5f,Width-1,Height-1),radius))using(var pen=new Pen(border))e.Graphics.DrawPath(pen,path);}
    }

    static class WindowGlyph
    {
        internal static void DrawClose(Graphics g, Rectangle bounds, Color color, float scale)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = bounds.X + bounds.Width / 2f, y = bounds.Y + bounds.Height / 2f;
            float arm = 9 * scale;
            using (var pen = new Pen(color, 2 * scale))
            {
                g.DrawLine(pen, x - arm, y - arm, x + arm, y + arm);
                g.DrawLine(pen, x + arm, y - arm, x - arm, y + arm);
            }
        }
    }

    enum DialogStatusKind { Success, Search, Download, Warning, Error, Information }
    sealed class DialogStatusIcon : Control
    {
        DialogStatusKind kind;
        internal DialogStatusKind Kind {
            get{return kind;}
            set{kind=value;AccessibleName=value==DialogStatusKind.Success?"Успешно":value==DialogStatusKind.Search?"Проверка":value==DialogStatusKind.Download?"Загрузка обновления":value==DialogStatusKind.Warning?"Предупреждение":value==DialogStatusKind.Information?"Информация":"Ошибка";Invalidate();}
        }
        internal DialogStatusIcon()
        {SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;TabStop=false;AccessibleRole=AccessibleRole.Graphic;}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);var g=e.Graphics;var state=g.Save();
            try {
                g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(Width/28f,Height/28f);
                Color color=kind==DialogStatusKind.Success?Color.FromArgb(44,170,112):kind==DialogStatusKind.Warning?Color.FromArgb(215,151,39):kind==DialogStatusKind.Error?Color.FromArgb(220,86,91):ForeColor;
                using(var pen=new Pen(color,2f)) {
                    pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                    if(kind==DialogStatusKind.Success) {
                        g.DrawEllipse(pen,3,3,22,22);g.DrawLines(pen,new[]{new PointF(8,14),new PointF(12,18),new PointF(20,10)});
                    } else if(kind==DialogStatusKind.Information) {
                        g.DrawEllipse(pen,3,3,22,22);g.DrawLine(pen,14,13,14,20);
                        using(var dot=new SolidBrush(color))g.FillEllipse(dot,12.5f,7,3,3);
                    } else if(kind==DialogStatusKind.Search) {
                        g.DrawEllipse(pen,4,3,15,15);g.DrawLine(pen,17,17,24,24);
                    } else if(kind==DialogStatusKind.Download) {
                        g.DrawLine(pen,14,3,14,18);g.DrawLines(pen,new[]{new PointF(8,12),new PointF(14,18),new PointF(20,12)});
                        g.DrawLines(pen,new[]{new PointF(4,20),new PointF(4,25),new PointF(24,25),new PointF(24,20)});
                    } else if(kind==DialogStatusKind.Warning) {
                        g.DrawPolygon(pen,new[]{new PointF(14,3),new PointF(26,25),new PointF(2,25)});
                        g.DrawLine(pen,14,10,14,17);g.DrawLine(pen,14,21,14,21.5f);
                    } else {
                        g.DrawEllipse(pen,3,3,22,22);g.DrawLine(pen,10,10,18,18);g.DrawLine(pen,18,10,10,18);
                    }
                }
            }finally{g.Restore(state);}
        }
    }

    sealed class DialogCloseButton : Button
    {
        readonly Color ink;
        readonly float scale;
        internal DialogCloseButton(Color ink, float scale)
        {
            this.ink = ink; this.scale = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand; UseVisualStyleBackColor = false;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent.BackColor);
            WindowGlyph.DrawClose(e.Graphics, ClientRectangle, ink, scale);
        }
    }

    sealed class DialogModeOption : RadioButton
    {
        readonly Color fill,ink,accent,border;readonly int radius;
        internal DialogModeOption(string text,Font font,Color fill,Color ink,Color accent,Color border,int radius)
        {Text=text;Font=font;this.fill=fill;this.ink=ink;this.accent=accent;this.border=border;this.radius=radius;Appearance=Appearance.Button;FlatStyle=FlatStyle.Flat;Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.Clear(fill);g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=StyledDialog.Rounded(new RectangleF(1,1,Width-2,Height-2),radius))
            using(var brush=new SolidBrush(Checked?Color.FromArgb(40,accent):fill))using(var pen=new Pen(Checked?accent:border)) {g.FillPath(brush,path);g.DrawPath(pen,path);}
            TextRenderer.DrawText(g,Text,Font,ClientRectangle,Checked?accent:ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            if(Focused&&ShowFocusCues){var focus=ClientRectangle;focus.Inflate(-5,-5);ControlPaint.DrawFocusRectangle(g,focus,ink,fill);}
        }
        protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
    }
    sealed class DialogServicePicker : Button
    {
        readonly Color fill,ink,accent,border;readonly int radius;int selected;
        readonly ContextMenuStrip menu=new ContextMenuStrip();
        internal string[] Items {get;private set;}
        internal int SelectedIndex {get{return selected;}set{if(value<0||value>=Items.Length)throw new ArgumentOutOfRangeException("value");selected=value;Text="Сервис: "+Items[value];Invalidate();}}
        internal DialogServicePicker(string[] items,Font font,Color fill,Color ink,Color accent,Color border,int radius)
        {Items=items;Font=font;this.fill=fill;this.ink=ink;this.accent=accent;this.border=border;this.radius=radius;FlatStyle=FlatStyle.Flat;Cursor=Cursors.Hand;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);menu.BackColor=fill;menu.ForeColor=ink;menu.Font=font;menu.ShowImageMargin=false;AccessibleRole=AccessibleRole.ComboBox;}
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);menu.Items.Clear();
            for(int i=0;i<Items.Length;i++){int index=i;var item=new ToolStripMenuItem(Items[i]){Checked=i==selected,ForeColor=ink,BackColor=fill};item.Click+=(s,args)=>SelectedIndex=index;menu.Items.Add(item);}
            menu.MinimumSize=new Size(Width,0);menu.Show(this,new Point(0,Height));
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(e.Alt&&e.KeyCode==Keys.Down){PerformClick();e.Handled=true;return;}
            if(e.KeyCode==Keys.Down||e.KeyCode==Keys.Up){SelectedIndex=Math.Max(0,Math.Min(Items.Length-1,selected+(e.KeyCode==Keys.Down?1:-1)));e.Handled=true;return;}
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.Clear(fill);g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=StyledDialog.Rounded(new RectangleF(1,1,Width-2,Height-2),radius))using(var brush=new SolidBrush(fill))using(var pen=new Pen(border)){g.FillPath(brush,path);g.DrawPath(pen,path);}
            TextRenderer.DrawText(g,Text,Font,new Rectangle(12,0,Math.Max(0,Width-42),Height),ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            using(var pen=new Pen(accent,2)){float x=Width-18,y=Height/2f;g.DrawLines(pen,new[]{new PointF(x-4,y-2),new PointF(x,y+2),new PointF(x+4,y-2)});}
            if(Focused&&ShowFocusCues){var focus=ClientRectangle;focus.Inflate(-5,-5);ControlPaint.DrawFocusRectangle(g,focus,ink,fill);}
        }
        protected override void Dispose(bool disposing){if(disposing)menu.Dispose();base.Dispose(disposing);}
    }

    sealed class DialogButton : Button
    {
        internal Color? SurfaceColor;
        readonly Color fill, ink, border;
        readonly int radius;
        bool hovered;
        internal DialogButton(string text, Font font, Color fill, Color ink, Color border, int radius)
        {
            Text = text; Font = font; this.fill = fill; this.ink = ink; this.border = border; this.radius = radius;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SurfaceColor??Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = StyledDialog.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), radius))
            using (var brush = new SolidBrush(fill))
            using (var line = new Pen(border))
            {
                g.FillPath(brush, path); g.DrawPath(line, path);
                if (hovered) using (var overlay = new SolidBrush(Color.FromArgb(22, ink))) g.FillPath(overlay, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
            {
                var rect = ClientRectangle; rect.Inflate(-6, -6);
                ControlPaint.DrawFocusRectangle(g, rect, ink, fill);
            }
        }
    }
}
