using System;
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
        Button restartBrowser;
        bool browserBusy;
        int browserRowHeight, maxDialogHeight;
        Label messageText, statusCaption, statusMarker, browserHint;
        Color statusAccent, statusWarning;
        Panel supportPanel;
        Label supportHint;
        bool supportShown;
        int supportGrowth;
        Func<string, string, string> makeReport;

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
            ClientSize = new Size(width, px(190) + contentHeight);

            var heading = new Label { Text = title, Font = titleFont, ForeColor = ink,
                Bounds = new Rectangle(pad, px(24), width - pad * 2 - px(36), px(32)), UseMnemonic = false, UseCompatibleTextRendering = true };
            Controls.Add(heading);
            bool warning = icon == MessageBoxIcon.Warning || icon == MessageBoxIcon.Error;
            var marker = new Label { Text = warning ? "!" : "i", Font = buttonFont,
                ForeColor = accent, BackColor = card, TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(pad, px(66), px(28), px(28)) };
            Controls.Add(marker);
            var caption = new Label { Text = warning ? "Обратите внимание" : "Готово", Font = buttonFont,
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
            ClientSize = new Size(ClientSize.Width, Math.Min(maxDialogHeight, Math.Max(ClientSize.Height + browserRowHeight, px(480))));
            UpdateDialogLayout();
        }
        internal void ShowDnsTestStatus(string text, string caption, bool busy, bool warning)
        {
            messageText.Text = text; messageBody.AutoScrollPosition = Point.Empty;
            statusCaption.Text = caption;
            statusCaption.ForeColor = warning ? statusWarning : statusAccent;
            statusMarker.Text = warning ? "!" : "i";
            statusMarker.ForeColor = statusCaption.ForeColor;
            browserChoice.Enabled = !busy; restartBrowser.Enabled = !busy;
            restartBrowser.Text = busy ? "Проверяем…" : "Проверить";
            SetSupportVisible(warning && !busy && DiagnosticReport.ShouldOffer(Text, text));
            Invalidate(); Update();
        }

        internal void ShowBrowserStatus(string text, bool warning)
        {
            messageText.Text = text;
            messageBody.AutoScrollPosition = Point.Empty;
            statusCaption.Text = warning ? "Не удалось перезапустить" : browserBusy ? "Перезапускаем…" : "Браузер запущен";
            statusCaption.ForeColor = warning ? statusWarning : statusAccent;
            statusMarker.Text = warning ? "!" : "i";
            statusMarker.ForeColor = warning ? statusWarning : statusAccent;
            SetSupportVisible(warning && DiagnosticReport.ShouldOffer(Text, text));
            Invalidate(); Update();
        }
        internal void AddSupportControls(Func<string, string, string> buildReport, bool visible)
        {
            makeReport = buildReport;
            float scale = actionHeight / 42f;
            Func<int,int> px = n => (int)Math.Round(n * scale);
            supportPanel = new Panel {Left=px(28), Width=ClientSize.Width-px(56), Height=px(72), Visible=false};
            var save = new DialogButton("Сохранить отчёт", buttonFont, border, ForeColor, border, px(10)) {
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
                        System.IO.File.WriteAllText(dialog.FileName, makeReport(Text,messageText.Text), new System.Text.UTF8Encoding(true));
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
            if (primaryAction != null && messageBody != null)
            {
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
                    bodyBottom = browserHint.Top - bodyGap;
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
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius))
            using (var pen = new Pen(border)) e.Graphics.DrawPath(pen, path);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { titleFont.Dispose(); bodyFont.Dispose(); buttonFont.Dispose(); }
        }
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

    sealed class DialogButton : Button
    {
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
            g.Clear(Parent.BackColor);
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
