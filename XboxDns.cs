// HB AI · Smart DNS — переключатель Smart DNS (Comss.one, GeoHide, xbox-dns.ru) в стиле сообщества «Нейросети и AI на практике».
// Сборка: build.bat (csc из .NET Framework 4, есть в каждой Windows 10/11).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;

namespace HbAi.XboxDns
{
    static class Dns
    {
        // Сервисы Smart DNS. Проверено 12.10.2026 из России: Comss.one и GeoHide (Европа) отдают прокси для ChatGPT,
        // Claude, Gemini и ElevenLabs, российский узел GeoHide — для Gemini и ElevenLabs. xbox-dns.ru с 10.10.2026
        // нейросети не пропускает (0.0.0.0). Astracat (135.106.217.200) 12.10.2026 не отвечал, из списка убран.
        public static readonly string[] Name = { "Comss.one", "GeoHide", "GeoHide EU", "Xbox DNS", "Bezmezhau", "DNS-AI" };
        public static readonly string[][] V4 = {
            new[] { "83.220.169.155", "212.109.195.93" },
            new[] { "159.194.200.33", "193.233.112.67" },
            new[] { "143.20.64.192", "45.13.237.213" },
            new[] { "111.88.96.54", "111.88.96.55" },
            new[] { "143.20.64.55", "31.58.216.201" },
            new[] { "194.87.196.13", "186.246.49.127" } };
        public static readonly string[][] V6 = {
            new string[0], new string[0], new string[0],
            new[] { "2a00:ab00:1233:26::50", "2a00:ab00:1233:26::51" }, new string[0], new string[0] };
        public static readonly string[] Opens = {
            "ChatGPT, Claude, Gemini, ElevenLabs", "ChatGPT, Claude, Gemini, ElevenLabs",
            "ChatGPT, Claude, Gemini, ElevenLabs", "только Xbox Live, нейросети — нет", "ChatGPT, Gemini, Spotify, YouTube", "AI-сервисы · только DoH" };
        public static readonly string[] Site = { "comss.ru", "geohide.ru", "geohide.ru", "xbox-dns.ru", "bezmezhau.com", "dns-ai.ru" };
        public static readonly string[] Url = { "https://www.comss.ru/", "https://geohide.ru/", "https://geohide.ru/", "https://xbox-dns.ru/", "https://bezmezhau.com/", "https://dns-ai.ru/#setup" };
        internal static bool EncryptedOnly(int provider) { return provider == 5; }
        // Старые адреса xbox-dns.ru из версии 1.0: если стоят они, показываем Xbox DNS
        static readonly string[] XboxOld = { "111.88.96.50", "111.88.96.51" };

        // -1 — DNS сервиса не стоит, иначе номер набора (IPv4 определяет набор)
        public static int ActiveSet()
        {
            int found = -1;
            var system = new NativeDnsSystem();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                Guid id;
                if (!ni.Supports(NetworkInterfaceComponent.IPv4) || !Guid.TryParse(ni.Id, out id)) continue;
                string[] addresses; FamilyBackup configuration;
                try { configuration = system.Read(id, "IPv4"); addresses = configuration.Servers; }
                catch (IOException) { return -1; }
                catch (UnauthorizedAccessException) { return -1; }
                if (addresses.Length == 0) continue;
                int match = MatchProvider(addresses);
                if (match >= 0 && EncryptedOnly(match) && !EncryptedDns.ExplicitlyConfigured(configuration, match)) return -1;
                if (match < 0 || (found >= 0 && match != found)) return -1;
                found = match;
            }
            return found;
        }
        internal static int MatchProvider(string[] addresses)
        {
            if (addresses.SequenceEqual(XboxOld)) return 3;
            int plain = Array.FindIndex(V4, servers => addresses.SequenceEqual(servers));
            if (plain >= 0) return plain;
            for (int k = 0; k < V4.Length; k++)
                if (EncryptedDns.Templates[k] != null && addresses.SequenceEqual(EncryptedDns.ServersFor(k))) return k;
            return -1;
        }

        public static string Enable(int set)
        {
            if (set < 0 || set >= V4.Length) throw new ArgumentOutOfRangeException("set");
            return DnsElevation.Run(true, EncryptedOnly(set) ? 200 + set : set);
        }

        public static string Disable()
        {
            return DnsElevation.Run(false, 0);
        }

        public static string ResetKnown() { return DnsElevation.Run(false, 1); }

        internal static string ExecuteDirect(bool enable, int set)
        {
            if (enable && !NetworkCheck.HasConnection()) return NetworkCheck.OfflineMessage;
            if (enable && VpnCheck.Scan().NeedsAttention) return "Отключите VPN и повторите подключение. DNS не изменён.";
            if (enable && set < V4.Length && EncryptedOnly(set)) return EncryptedDns.Run(set);
            if (enable && set >= 300 && set < 300 + V4.Length) return EncryptedDns.Run(set - 300, true);
            if (enable && set >= 200 && set < 200 + V4.Length) return EncryptedDns.Run(set - 200);
            if (enable && set >= 100 && set < 100 + AntigravityDns.ServerSets.Length * 2) return AntigravityDns.Run(true, set % 2 == 1, (set - 100) / 2);
            if (!enable && set == 3) return AgCleanup.Run();
            if (!enable && set == 2) return AntigravityDns.Run(false);
            if (enable && (set < 0 || set >= V4.Length)) return "Неизвестный DNS-провайдер.";
            if (!enable && set != 0 && set != 1) return "Неизвестная операция DNS.";
            if (!enable && set == 1)
                return DnsSettingsEngine.Run(false, V4.SelectMany(s => s).Concat(V6.SelectMany(s => s)).Concat(XboxOld).Concat(EncryptedDns.ServersFor(0)).ToArray(), true);
            return DnsSettingsEngine.Run(enable, enable ? V4[set].Concat(V6[set]).ToArray() : new string[0]);
        }

        public static bool HasBackup()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HbAi\SmartDns"))
                return key != null && key.GetValue("Backup") != null;
        }
        internal static bool HasAvailableBackup()
        {
            try {
                var adapters = new NativeDnsSystem().Adapters(false);
                return new RegistryDnsBackupStore().Load().Any(record => adapters.Any(adapter => adapter.Id == new Guid(record.Guid)));
            }
            catch (Exception) { return true; } // Keep recovery available if the backup cannot be inspected.
        }
        internal static bool StrictConfigured()
        {
            var system = new NativeDnsSystem();
            var adapters = system.Adapters(true).Where(a => a.V4).ToArray();
            return adapters.Length > 0 && adapters.All(a => {
                var state = system.Read(a.Id, "IPv4");
                return state.DohCaptured && state.Servers.Length > 0 && state.Doh.Length == state.Servers.Length &&
                    state.Doh.All(d => (d.Flags & 2) != 0 && (d.Flags & 4) == 0) && !system.ReadIpv6Binding(a.Id);
            });
        }

    }

    class MainForm : Form
    {
        const int CanvasWidth = 440, CanvasHeight = 988;
        internal static string VersionLabel { get { return "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }
        readonly float uiScale;
        bool darkTheme;
        Color Bg { get { return darkTheme ? Color.FromArgb(15, 24, 30) : Color.FromArgb(252, 253, 254); } }
        Color Accent { get { return darkTheme ? Color.FromArgb(69, 203, 230) : Color.FromArgb(0, 127, 159); } }
        Color Selection { get { return darkTheme ? Color.FromArgb(29, 66, 79) : Color.FromArgb(201, 237, 244); } }
        static readonly Color Sand = Color.FromArgb(238, 215, 173);
        static readonly Color ContrastInk = Color.FromArgb(21, 29, 34);
        static readonly Color ActiveBg = Color.FromArgb(0, 127, 159);
        Color Ink { get { return darkTheme ? Color.FromArgb(235, 243, 246) : ContrastInk; } }
        Color Muted { get { return darkTheme ? Color.FromArgb(160, 184, 194) : Color.FromArgb(82, 102, 111); } }
        Color Soft { get { return darkTheme ? Color.FromArgb(190, 217, 226) : Color.FromArgb(51, 79, 91); } }
        Color CardBg { get { return darkTheme ? Color.FromArgb(23, 38, 47) : Color.FromArgb(240, 247, 249); } }
        Color CardLine { get { return darkTheme ? Color.FromArgb(44, 67, 78) : Color.FromArgb(210, 227, 232); } }

        readonly PrivateFontCollection fonts = new PrivateFontCollection();
        readonly Dictionary<string, Image> serviceIcons = new Dictionary<string, Image>();
        FontFamily fHead, fBold, fMed;
        bool active, busy, hasBackup;
        bool hideDnsReminder = ReadHideDnsReminder();
        AgStatus agStatus = new AgStatus();
        bool agLoading = true;
        bool cleanupDone, cleanupBackupAvailable = Directory.Exists(AgCleanupBackup.Root);
        readonly Rectangle rAgCleanup = new Rectangle(280, 807, 114, 26);
        readonly Rectangle rAgOption = new Rectangle(28, 754, 384, 86);
        readonly Rectangle rSecureOption = new Rectangle(28, 678, 384, 58);
        bool secureDns;
        int dnsSet;
        string error;
        float pulse;
        Rectangle rToggle, rClose, rSite, rGroup, rBrand, rTelegram;
        readonly Rectangle rTheme = new Rectangle(332, 24, 36, 34);
        readonly ToolTip themeTip = new ToolTip();
        readonly Rectangle[] rSeg = new Rectangle[Dns.Name.Length];
        readonly Rectangle providerViewport = new Rectangle(48, 500, 344, 56);
        int providerScroll;
        string[] probeLabels = Enumerable.Repeat("Ответы DNS ещё не проверены", Dns.Name.Length).ToArray();
        bool probing;
        bool dnsConfirmed;
        DnsProbeResult[][] probeResults = new DnsProbeResult[Dns.Name.Length][];
        Point dragFrom;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer networkMonitor = new System.Windows.Forms.Timer();
        VpnStatus liveVpn = VpnCheck.Scan();
        bool checkingNetwork;
        bool networkAvailable = NetworkCheck.HasConnection();
        internal string AntigravityOnlyLabel { get {
            if (agLoading) return null;
            if (agStatus.Error != null) return "Проверьте Antigravity";
            if (agStatus.Checked) return agStatus.Complete ? (agStatus.Provider == 1 ? "Проверьте Antigravity" : "Только Antigravity") : "Antigravity частично";
            return agStatus.External ? "Правила Antigravity" : null;
        } }
        internal string ConnectionLabel { get { return busy ? "Применяем настройки…" : !networkAvailable ? "Нет подключения" : liveVpn.NeedsAttention ?
            (liveVpn.Failed ? "Проверьте VPN" : "VPN мешает DNS") : active ? (hasBackup ? (dnsConfirmed ? "DNS включён" : "Проверьте DNS") : "Найден DNS сервиса") : AntigravityOnlyLabel ?? "DNS выключен"; } }
        internal void UpdateVpnStatus(VpnStatus status)
        {
            if (liveVpn.NeedsAttention != status.NeedsAttention)
            {
                probeResults = new DnsProbeResult[Dns.Name.Length][];
                probeLabels = Enumerable.Repeat(status.NeedsAttention ? "Для теста отключите VPN" : "Ответы DNS ещё не проверены", Dns.Name.Length).ToArray();
            }
            liveVpn = status; Invalidate();
        }

        public MainForm()
        {
            LoadFonts();
            LoadServiceIcons();
            darkTheme = ReadDarkTheme();
            Text = "Код Всего · Smart DNS";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            using (var desktop = Graphics.FromHwnd(IntPtr.Zero))
                uiScale = Math.Min(desktop.DpiX / 96f,
                    Math.Min((area.Width - 32f) / CanvasWidth, (area.Height - 32f) / CanvasHeight));
            uiScale = Math.Max(0.5f, uiScale);
            ClientSize = new Size((int)Math.Round(CanvasWidth * uiScale), (int)Math.Round(CanvasHeight * uiScale));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            DoubleBuffered = true;
            try { Icon = new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")); } catch { }
            Region = RoundRegion(ClientRectangle, (int)Math.Round(22 * uiScale));

            rClose = new Rectangle(CanvasWidth - 58, 22, 36, 36);
            rToggle = new Rectangle((CanvasWidth - 200) / 2, 196, 200, 200);

            anim.Interval = 30;
            anim.Tick += (s, e) => { pulse = (pulse + 0.035f) % 1f; if (busy || active) Invalidate(rToggleGlow()); };
            anim.Start();

            int cur = Dns.ActiveSet();
            dnsConfirmed = cur >= 0;
            hasBackup = Dns.HasBackup();
            active = cur >= 0 || (hasBackup && Dns.HasAvailableBackup());
            dnsSet = cur >= 0 ? cur : 0;
            if (cur >= 0)
                try { secureDns = Dns.EncryptedOnly(cur) || Dns.StrictConfigured(); } catch (Exception) { }
            for (int k = 0; k < rSeg.Length; k++)
            {
                rSeg[k] = new Rectangle(48 + k * 110, 500, 110, 56);
            }
            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseWheel += (s, e) => { if (new Rectangle(28, 500, 384, 56).Contains(CanvasPoint(e.Location))) ScrollProviders(e.Delta > 0 ? -90 : 90); };
            EnsureProviderVisible();
            Shown += (s, e) => CheckDnsServers();
            networkMonitor.Interval = 3000;
            networkMonitor.Tick += (s, e) => {
                if (checkingNetwork || busy) return;
                checkingNetwork = true;
                ThreadPool.QueueUserWorkItem(_ => {
                    var status = VpnCheck.Scan();
                    bool connected = NetworkCheck.HasConnection();
                    var extraDns = AntigravityDns.GetStatus();
                    int actual = -1; bool backup = hasBackup, availableBackup = false, strictNow = false;
                    try { actual = Dns.ActiveSet(); backup = Dns.HasBackup(); availableBackup = backup && Dns.HasAvailableBackup(); if (actual >= 0) strictNow = Dns.EncryptedOnly(actual) || Dns.StrictConfigured(); }
                    catch (Exception) { }
                    if (IsDisposed || Disposing) return;
                    try { BeginInvoke((Action)(() => {
                        checkingNetwork = false;
                        if (busy) return;
                        if (networkAvailable != connected) {
                            probeResults = new DnsProbeResult[Dns.Name.Length][];
                            probeLabels = Enumerable.Repeat(connected ? "Ответы DNS ещё не проверены" : "Нет сетевого подключения", Dns.Name.Length).ToArray();
                        }
                        networkAvailable = connected; hasBackup = backup; dnsConfirmed = actual >= 0;
                        agStatus = extraDns; agLoading = false;
                        if (actual >= 0) secureDns = strictNow;
                        active = actual >= 0 || availableBackup;
                        if (actual >= 0 && actual != dnsSet) { dnsSet = actual; EnsureProviderVisible(); }
                        UpdateVpnStatus(status);
                    })); }
                    catch (InvalidOperationException) { }
                });
            };
            Shown += (s, e) => networkMonitor.Start();
            Shown += (s, e) => ThreadPool.QueueUserWorkItem(_ => {
                var status = AntigravityDns.GetStatus();
                if (IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => { agStatus = status; agLoading = false; Invalidate(); })); }
                catch (InvalidOperationException) { }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                anim.Dispose(); networkMonitor.Dispose(); themeTip.Dispose();
                foreach (var image in serviceIcons.Values) image.Dispose();
                serviceIcons.Clear();
            }
            base.Dispose(disposing);
        }

        static bool ReadDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))
                    return key != null && string.Equals(key.GetValue("Theme") as string, "Dark", StringComparison.Ordinal);
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        static bool ReadHideDnsReminder()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))
                    return key != null && object.Equals(key.GetValue("HideDnsReminder"), 1);
            }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
        }

        void ShowDnsReminder()
        {
            if (hideDnsReminder) return;
            using (var dialog = CreateDialog("Настройки DNS применены.\n\nПолностью закройте и заново откройте браузер или приложение, в котором используете сервис. " +
                "Сам переключатель DNS перезапускать не требуется.\n\nЕсли сервис не открывается, отключите VPN и попробуйте ещё раз.",
                "После смены DNS", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, 0, true))
            {
                if (dialog.ShowDialog(this) != DialogResult.Ignore) return;
            }
            hideDnsReminder = true;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KodVsego\SmartDns"))
                    key.SetValue("HideDnsReminder", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                if (!(ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)) throw;
                ShowMessage("Выбор сохранён только до закрытия программы: не удалось записать настройку пользователя.",
                    "Настройка напоминания", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void SetTheme(bool dark, bool persist)
        {
            darkTheme = dark;
            BackColor = Bg;
            themeTip.SetToolTip(this, null);
            Invalidate();
            if (!persist) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KodVsego\SmartDns"))
                    key.SetValue("Theme", dark ? "Dark" : "Light", RegistryValueKind.String);
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        void DrawThemeButton(Graphics g)
        {
            using (var path = RoundPath(rTheme, 12))
            using (var fill = new SolidBrush(CardBg))
            using (var line = new Pen(CardLine))
            { g.FillPath(fill, path); g.DrawPath(line, path); }
            float x = rTheme.X + 18, y = rTheme.Y + 17;
            using (var pen = new Pen(Accent, 1.7f))
            using (var fill = new SolidBrush(Accent))
            using (var cutout = new SolidBrush(CardBg))
            {
                if (darkTheme)
                {
                    g.DrawEllipse(pen, x - 5, y - 5, 10, 10);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(pen, x + (float)Math.Cos(a) * 8, y + (float)Math.Sin(a) * 8,
                            x + (float)Math.Cos(a) * 11, y + (float)Math.Sin(a) * 11);
                    }
                }
                else
                {
                    g.FillEllipse(fill, x - 9, y - 9, 18, 18);
                    g.FillEllipse(cutout, x - 3, y - 12, 17, 17);
                }
            }
        }

        Rectangle rToggleGlow()
        {
            var r = rToggle; r.Inflate(40, 40);
            return Rectangle.Ceiling(new RectangleF(r.X * uiScale, r.Y * uiScale, r.Width * uiScale, r.Height * uiScale));
        }

        // Use logical pixels for fonts and geometry alike. Point fonts apply the
        // monitor DPI a second time when drawing on a scaled canvas.
        static Font UiFont(FontFamily family, float points)
        {
            return new Font(family, points * 96f / 72f, GraphicsUnit.Pixel);
        }

        Point CanvasPoint(Point point)
        {
            return new Point((int)Math.Round(point.X / uiScale), (int)Math.Round(point.Y / uiScale));
        }

        void LoadServiceIcons()
        {
            var names = new[] { "chatgpt", "claude", "gemini", "elevenlabs", "xbox", "spotify", "youtube" };
            foreach (var name in names)
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("service-icons." + name + ".png"))
                {
                    // Keep text usable in development builds without embedded artwork.
                    if (stream == null) continue;
                    using (var source = Image.FromStream(stream)) serviceIcons.Add(name, new Bitmap(source));
                }
        }

        void DrawServiceIcons(Graphics g, RectangleF row)
        {
            string[] labels = dnsSet == 3 ? new[] { "Xbox Live", "нейросети — нет" } :
                dnsSet == 5 ? new[] { "AI-сервисы · только DoH" } : Dns.Opens[dnsSet].Split(new[] { ", " }, StringSplitOptions.None);
            string[] keys = dnsSet == 3 ? new[] { "xbox", "" } :
                dnsSet == 5 ? new[] { "" } : labels.Select(label => label.ToLowerInvariant()).ToArray();
            var groupIcons = dnsSet == 5 ? new[] { "chatgpt", "claude", "gemini", "elevenlabs" } : new string[0];
            const float iconSize = 18, iconGap = 5, itemGap = 12;
            using (var font = UiFont(fMed, 8.5f))
            using (var ink = new SolidBrush(Ink))
            using (var muted = new SolidBrush(Muted))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                float groupWidth = groupIcons.Count(key => serviceIcons.ContainsKey(key)) * (iconSize + iconGap);
                float width = groupWidth;
                for (int i = 0; i < labels.Length; i++)
                    width += g.MeasureString(labels[i], font, PointF.Empty, format).Width +
                        (serviceIcons.ContainsKey(keys[i]) ? iconSize + iconGap : 0) + (i == 0 ? 0 : itemGap);
                // Fit the complete row in logical coordinates; the canvas handles DPI.
                float scale = Math.Min(1, row.Width / width);
                var state = g.Save();
                try
                {
                    g.TranslateTransform(row.X, row.Y + (row.Height - 23 * scale) / 2);
                    g.ScaleTransform(scale, scale);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    float x = 0;
                    foreach (var key in groupIcons)
                    {
                        Image icon;
                        if (!serviceIcons.TryGetValue(key, out icon)) continue;
                        g.DrawImage(icon, new RectangleF(x, 2.5f, iconSize, iconSize));
                        x += iconSize + iconGap;
                    }
                    for (int i = 0; i < labels.Length; i++)
                    {
                        Image icon;
                        if (serviceIcons.TryGetValue(keys[i], out icon))
                        {
                            g.DrawImage(icon, new RectangleF(x, 2.5f, iconSize, iconSize));
                            x += iconSize + iconGap;
                        }
                        float textHeight = font.GetHeight(g);
                        g.DrawString(labels[i], font, i == 1 && dnsSet == 3 ? muted : ink,
                            new PointF(x, (23 - textHeight) / 2), format);
                        x += g.MeasureString(labels[i], font, PointF.Empty, format).Width + itemGap;
                    }
                }
                finally { g.Restore(state); }
            }
        }

        void LoadFonts()
        {
            foreach (var name in new[] { "Unbounded-ExtraBold.ttf", "Manrope-Bold.ttf", "Manrope-Medium.ttf" })
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) continue;
                    var data = new byte[s.Length];
                    s.Read(data, 0, data.Length);
                    IntPtr ptr = Marshal.AllocCoTaskMem(data.Length);
                    Marshal.Copy(data, 0, ptr, data.Length);
                    fonts.AddMemoryFont(ptr, data.Length);
                }
            }
            fHead = Find("HB Unbounded", "Segoe UI");
            fBold = Find("HB Manrope Bold", "Segoe UI");
            fMed = Find("HB Manrope Medium", "Segoe UI");
        }

        FontFamily Find(string name, string fallback)
        {
            foreach (var f in fonts.Families) if (f.Name.StartsWith(name)) return f;
            return new FontFamily(fallback);
        }

        static Region RoundRegion(Rectangle r, int rad) { return new Region(RoundPath(r, rad)); }

        static GraphicsPath RoundPath(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var state = g.Save();
            try
            {
                g.ScaleTransform(uiScale, uiScale);
                PaintCanvas(g);
            }
            finally { g.Restore(state); }
        }

        void PaintCanvas(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int W = CanvasWidth;

            // Both themes retain the sand and cyan brand colors.
            Glow(g, new RectangleF(W - 330, -260, 560, 520), Color.FromArgb(darkTheme ? 25 : 120, 238, 215, 173));
            Glow(g, new RectangleF(-260, 460, 520, 460), Color.FromArgb(darkTheme ? 30 : 45, 2, 165, 207));
            DrawThemeButton(g);

            // плашка сверху
            using (var f = UiFont(fBold, 11f))
            {
                string pill = "Smart DNS · " + Dns.Site[dnsSet];
                var sz = g.MeasureString(pill, f);
                var pr = new RectangleF(30, 24, sz.Width + 46, 34);
                rSite = Rectangle.Round(pr);
                using (var path = RoundPath(pr, 17))
                using (var b = new SolidBrush(Color.FromArgb(22, 2, 165, 207)))
                using (var pen = new Pen(Color.FromArgb(70, 0, 127, 159)))
                { g.FillPath(b, path); g.DrawPath(pen, path); }
                using (var b = new SolidBrush(Accent)) g.FillEllipse(b, pr.X + 16, pr.Y + 12, 10, 10);
                using (var b = new SolidBrush(Soft)) g.DrawString(pill, f, b, pr.X + 32, pr.Y + (34 - sz.Height) / 2 + 1);
            }
            // крестик
            WindowGlyph.DrawClose(g, rClose, Muted, 1f);

            // заголовок
            using (var f = UiFont(fHead, 22f))
            using (var b = new SolidBrush(Ink))
            {
                g.DrawString("Доступ", f, b, 28, 82);
                var w = g.MeasureString("Доступ ", f).Width;
                string hl = "к сервисам";
                var sz = g.MeasureString(hl, f);
                using (var path = RoundPath(new RectangleF(28, 124, sz.Width + 6, sz.Height - 2), 8))
                using (var yb = new SolidBrush(Sand)) g.FillPath(yb, path);
                using (var db = new SolidBrush(ContrastInk)) g.DrawString(hl, f, db, 31, 124);
            }

            DrawToggle(g);

            // статус
            string status = ConnectionLabel;
            string sub = error != null ? error : busy ? "Секунду" : active ? (hasBackup ? (dnsConfirmed ? Dns.Name[dnsSet] + ": " + Dns.Opens[dnsSet] : "Разные DNS или настройки изменены извне") : "Копии прежних настроек нет") : "DNS сервисов не обнаружен";
            if (error == null && !busy && !active && AntigravityOnlyLabel != null)
                sub = agStatus.Error != null ? "Не удалось проверить отдельные DNS-правила" :
                    agStatus.Checked && agStatus.Complete ? (agStatus.Provider == 1 ? "Comss: доступ к API не подтверждён" : (agStatus.Provider >= 0 ? AntigravityDns.ProviderNames[agStatus.Provider] + " · " : "") + "общий DNS выключен") :
                    agStatus.Checked ? "Проверьте доп. DNS Antigravity" : "Другой анлокер · общий DNS выключен";
            if (!busy && liveVpn.NeedsAttention) sub = liveVpn.Failed ? "Не удалось проверить сетевые адаптеры" : "Отключите VPN: браузер может обходить этот DNS";
            if (!busy && !networkAvailable) sub = "Подключитесь к Wi-Fi или Ethernet";
            using (var f = UiFont(fHead, busy ? 12f : 15f))
            using (var b = new SolidBrush((active || ((agStatus.Checked || agStatus.External) && agStatus.Error == null)) && !busy ? Accent : Ink))
                Centered(g, status, f, b, 420);
            using (var f = UiFont(fMed, 10.5f))
            using (var b = new SolidBrush(error != null ? (darkTheme ? Color.FromArgb(255, 151, 151) : Color.FromArgb(181, 59, 65)) : Muted))
                Centered(g, sub, f, b, 454, W - 60);

            // выбор набора адресов
            using (var path = RoundPath(new RectangleF(28, 500, W - 56, 56), 16))
            using (var b = new SolidBrush(CardBg))
            using (var pen = new Pen(CardLine))
            { g.FillPath(b, path); g.DrawPath(pen, path); }
            var providerState = g.Save();
            g.SetClip(providerViewport);
            for (int k = 0; k < rSeg.Length; k++)
            {
                var sr = new RectangleF(rSeg[k].X - providerScroll + 5, rSeg[k].Y + 5, rSeg[k].Width - 10, rSeg[k].Height - 10);
                bool on = k == dnsSet;
                if (on)
                    using (var path = RoundPath(sr, 12))
                    using (var b = new SolidBrush(Selection)) g.FillPath(b, path);
                string label = k == 3 ? "Xbox" : k == 2 ? "GeoHide EU" : Dns.Name[k];
                using (var f = UiFont(fBold, 9.5f))
                using (var b = new SolidBrush(on ? Ink : Soft))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(label, f, b, sr, sf);
            }
            g.Restore(providerState);
            using (var f = UiFont(fBold, 14f))
            using (var b = new SolidBrush(Accent))
            { g.DrawString("‹", f, b, 30, 515); g.DrawString("›", f, b, 396, 515); }

            // карточка с адресами
            var card = new RectangleF(28, 574, W - 56, 86);
            using (var path = RoundPath(card, 16))
            using (var b = new SolidBrush(CardBg))
            using (var pen = new Pen(CardLine))
            { g.FillPath(b, path); g.DrawPath(pen, path); }
            using (var fl = UiFont(fBold, 9.5f))
            using (var fv = UiFont(fMed, 10.5f))
            using (var bl = new SolidBrush(Accent))
            using (var bv = new SolidBrush(Ink))
            {
                g.DrawString("DNS", fl, bl, card.X + 20, card.Y + 10);
                g.DrawString(string.Join("  /  ", secureDns && EncryptedDns.Templates[dnsSet] != null ? EncryptedDns.ServersFor(dnsSet) : Dns.V4[dnsSet]), fv, bv, card.X + 76, card.Y + 8);
            }
            DrawServiceIcons(g, new RectangleF(card.X + 20, card.Y + 33, card.Width - 40, 23));
            using (var f = UiFont(fMed, 8f))
            using (var b = new SolidBrush(Accent)) g.DrawString(probing ? "Проверяем ответы DNS…" : probeLabels[dnsSet], f, b, 48, 636);
            using (var path = RoundPath(new RectangleF(302, 630, 92, 24), 8))
            using (var b = new SolidBrush(Selection)) g.FillPath(b, path);
            using (var f = UiFont(fBold, 8f))
            using (var b = new SolidBrush(Ink))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("Тест DNS", f, b, new RectangleF(302, 630, 92, 24), sf);

            using (var path = RoundPath(rSecureOption, 16))
            using (var fill = new SolidBrush(CardBg))
            using (var line = new Pen(CardLine)) { g.FillPath(fill, path); g.DrawPath(line, path); }
            using (var path = RoundPath(new Rectangle(46, 696, 20, 20), 5))
            using (var fill = new SolidBrush(secureDns ? ActiveBg : Bg))
            using (var line = new Pen(Accent, 1.5f)) { g.FillPath(fill, path); g.DrawPath(line, path); }
            if (secureDns) using (var pen = new Pen(Color.White, 2f)) g.DrawLines(pen, new[] { new Point(50, 706), new Point(55, 711), new Point(63, 701) });
            using (var title = UiFont(fBold, 10f))
            using (var body = UiFont(fMed, 8f))
            using (var ink = new SolidBrush(Ink))
            using (var muted = new SolidBrush(Muted)) {
                g.DrawString("Зашифрованный DNS", title, ink, 76, 686);
                g.DrawString(EncryptedDns.Templates[dnsSet] == null ? "DoH: Comss.one, GeoHide, Bezmezhau или DNS-AI" : secureDns && (agStatus.External || agStatus.Checked) ? "DoH · Antigravity по отдельным правилам" : "DoH адаптера · IPv6 приостанавливается", body, muted, 76, 710);
            }
            using (var path = RoundPath(rAgOption, 16))
            using (var fill = new SolidBrush(CardBg))
            using (var line = new Pen(CardLine))
            { g.FillPath(fill, path); g.DrawPath(line, path); }
            var check = new Rectangle(46, 771, 20, 20);
            using (var path = RoundPath(check, 5))
            using (var fill = new SolidBrush(agStatus.Checked ? ActiveBg : Bg))
            using (var line = new Pen(Accent, 1.5f))
            { g.FillPath(fill, path); g.DrawPath(line, path); }
            if (agStatus.Checked)
                using (var pen = new Pen(Color.White, 2f))
                    g.DrawLines(pen, new[] { new Point(50, 781), new Point(55, 786), new Point(63, 776) });
            using (var title = UiFont(fBold, 10f))
            using (var body = UiFont(fMed, 8.5f))
            using (var ink = new SolidBrush(Ink))
            using (var muted = new SolidBrush(Muted))
            {
                g.DrawString("Доп. DNS Antigravity", title, ink, 76, 764);
                g.DrawString("Может помочь при ошибке 400 в Antigravity", body, muted, 76, 788);
                string note = agLoading ? "Проверяем настройки…" : agStatus.Error != null ? "Не удалось проверить · нажмите для повтора" :
                    agStatus.RecoveryNeeded ? "Снимите галочку: завершить восстановление" : agStatus.External ? "Есть правила другой программы" : agStatus.Checked ?
                    (agStatus.Complete ? (agStatus.Provider == 1 ? "Comss: смените DNS" : "Включено · " + (agStatus.Provider >= 0 ? AntigravityDns.ProviderNames[agStatus.Provider] : "DNS")) +
                        (agStatus.Provider != AntigravityDns.ProviderForMainSet(dnsSet) ? " · смена после отключения" : "") : "Включено частично · снимите для отмены") :
                    (cleanupDone ? "Старые правила удалены" : secureDns ? "Используется системный DoH" : dnsSet == 0 ? "Comss для этой опции отключён" : dnsSet == 3 ? "Выберите DNS для нейросетей выше" : "Будет использован: " + Dns.Name[dnsSet]);
                bool showCleanup = agStatus.External || cleanupBackupAvailable;
                using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString(note, body, muted, new RectangleF(76, 810, showCleanup ? 198 : 320, 22), format);
            }
            if (agStatus.External || cleanupBackupAvailable)
            {
                using (var path = RoundPath(rAgCleanup, 8))
                using (var fill = new SolidBrush(Selection)) g.FillPath(fill, path);
                using (var f = UiFont(fBold, 8f))
                using (var b = new SolidBrush(Ink))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(agStatus.External ? "Удалить старые" : "Копия правил", f, b, rAgCleanup, format);
            }

            // кнопка Telegram-канала
            rTelegram = new Rectangle(28, 858, W - 56, 58);
            using (var path = RoundPath(rTelegram, 16))
            using (var b = new LinearGradientBrush(rTelegram, Sand, Color.FromArgb(2, 165, 207), LinearGradientMode.Horizontal))
                g.FillPath(b, path);
            using (var f = UiFont(fHead, 11.5f))
            using (var b = new SolidBrush(ContrastInk))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("Telegram · TotalC0de  →", f, b, rTelegram, sf);

            // подвал
            using (var fb = UiFont(fHead, 13f))
            using (var bb = new SolidBrush(Accent))
            using (var dark = new SolidBrush(Ink))
            {
                const string brand = "Код Всего";
                using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    float prefix = g.MeasureString("Код ", fb, PointF.Empty, format).Width;
                    g.DrawString("Код ", fb, dark, new PointF(28, 940), format);
                    g.DrawString("Всего", fb, bb, new PointF(28 + prefix, 940), format);
                }
                rBrand = new Rectangle(28, 938, (int)Math.Ceiling(g.MeasureString(brand, fb).Width), 30);
            }
            using (var f = UiFont(fMed, 9.5f))
            using (var b = new SolidBrush(Muted))
            {
                var t = "t.me/TotalC0de";
                var sz = g.MeasureString(t, f);
                rGroup = new Rectangle((int)(W - 28 - sz.Width), 945, (int)sz.Width, (int)sz.Height);
                g.DrawString(t, f, b, rGroup.X, rGroup.Y);
            }
            using (var f = UiFont(fMed, 8.5f))
            using (var b = new SolidBrush(Muted))
            using (var format = new StringFormat {Alignment=StringAlignment.Center, LineAlignment=StringAlignment.Center, FormatFlags=StringFormatFlags.NoWrap})
                g.DrawString(VersionLabel, f, b, new RectangleF(rBrand.Right + 4, 940, Math.Max(0,rGroup.Left-rBrand.Right-8), 25), format);
        }

        void DrawToggle(Graphics g)
        {
            var r = new RectangleF(rToggle.X, rToggle.Y, rToggle.Width, rToggle.Height);
            if (active || busy)
            {
                float k = (float)(0.5 + 0.5 * Math.Sin(pulse * Math.PI * 2));
                var gr = r; gr.Inflate(28 + 10 * k, 28 + 10 * k);
                Glow(g, gr, Color.FromArgb((int)(busy ? 60 : 90 + 40 * k), Accent));
            }
            // кольцо
            using (var b = new SolidBrush(active ? ActiveBg : (darkTheme ? CardBg : Color.White))) g.FillEllipse(b, r);
            using (var pen = new Pen(active || (!agLoading && agStatus.Checked && agStatus.Error == null) ? Accent : Color.FromArgb(161, 194, 204), 3f)) g.DrawEllipse(pen, r);
            if (busy)
            {
                using (var pen = new Pen(Accent, 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    var ar = r; ar.Inflate(-10, -10);
                    g.DrawArc(pen, ar, pulse * 360f, 90f);
                }
            }
            // значок питания
            var c = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2 - 14);
            using (var pen = new Pen(active ? Color.White : Accent, 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, c.X - 32, c.Y - 32, 64, 64, -60, 300);
                g.DrawLine(pen, c.X, c.Y - 40, c.X, c.Y - 6);
            }
            bool separateOnly = !active && AntigravityOnlyLabel != null;
            using (var f = UiFont(fHead, separateOnly ? 9f : 13f))
            using (var b = new SolidBrush(active ? Color.White : Soft))
            {
                string t = active ? "ВКЛ" : separateOnly ? "ВКЛЮЧИТЬ DNS" : "ВЫКЛ";
                var sz = g.MeasureString(t, f);
                g.DrawString(t, f, b, c.X - sz.Width / 2, r.Y + r.Height - 62);
            }
        }

        static void Glow(Graphics g, RectangleF r, Color c)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (var pb = new PathGradientBrush(path))
                {
                    pb.CenterColor = c;
                    pb.SurroundColors = new[] { Color.FromArgb(0, c) };
                    g.FillEllipse(pb, r);
                }
            }
        }

        void Centered(Graphics g, string s, Font f, Brush b, float y, float maxW = 0)
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
            float w = maxW > 0 ? maxW : CanvasWidth;
            g.DrawString(s, f, b, new RectangleF((CanvasWidth - w) / 2, y, w, 40), sf);
        }

        void OnDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            e = new MouseEventArgs(e.Button, e.Clicks, CanvasPoint(e.Location).X, CanvasPoint(e.Location).Y, e.Delta);
            if (rClose.Contains(e.Location)) { Close(); return; }
            if (rTheme.Contains(e.Location)) { SetTheme(!darkTheme, true); return; }
            if (rSecureOption.Contains(e.Location)) {
                if (busy) return;
                if (Dns.EncryptedOnly(dnsSet)) { ShowMessage("DNS-AI работает только через зашифрованный DNS. Шифрование для него обязательно.", "DNS-AI", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!secureDns && EncryptedDns.Templates[dnsSet] == null) { ShowMessage("Выберите Comss.one, GeoHide, Bezmezhau или DNS-AI. Для остальных провайдеров параметры системного DoH пока не подтверждены.", "Зашифрованный DNS", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (active) Apply(true, null, false, !secureDns); else { secureDns = !secureDns; Invalidate(); }
                return;
            }
            if (new Rectangle(28, 500, 20, 56).Contains(e.Location)) { ScrollProviders(-110); return; }
            if (new Rectangle(392, 500, 20, 56).Contains(e.Location)) { ScrollProviders(110); return; }
            if (new Rectangle(28, 628, 384, 32).Contains(e.Location)) { ShowDnsDiagnostics(); return; }
            if (rAgCleanup.Contains(e.Location) && (agStatus.External || cleanupBackupAvailable)) {
                if (busy || agLoading) return;
                if (agStatus.External) RemoveOldAntigravity(); else Open(AgCleanupBackup.Root);
                return;
            }
            if (rAgOption.Contains(e.Location)) { ToggleAntigravity(); return; }
            if (rGroup.Contains(e.Location)) { Open("https://t.me/TotalC0de"); return; }
            if (rSite.Contains(e.Location)) { Open(Dns.Url[dnsSet]); return; }
            if (rBrand.Contains(e.Location) || rTelegram.Contains(e.Location)) { Open("https://t.me/TotalC0de"); return; }
            var c = new Point(rToggle.X + rToggle.Width / 2, rToggle.Y + rToggle.Height / 2);
            double dx = e.X - c.X, dy = e.Y - c.Y;
            if (dx * dx + dy * dy <= rToggle.Width * rToggle.Width / 4.0) { Toggle(); return; }
            for (int k = 0; k < rSeg.Length; k++)
                if (providerViewport.Contains(e.Location) && rSeg[k].Contains(new Point(e.X + providerScroll, e.Y))) { PickSet(k); return; }
            dragFrom = e.Location;
        }

        void OnMove(object s, MouseEventArgs e)
        {
            e = new MouseEventArgs(e.Button, e.Clicks, CanvasPoint(e.Location).X, CanvasPoint(e.Location).Y, e.Delta);
            var c = new Point(rToggle.X + rToggle.Width / 2, rToggle.Y + rToggle.Height / 2);
            double dx = e.X - c.X, dy = e.Y - c.Y;
            bool hand = rSecureOption.Contains(e.Location) || rAgOption.Contains(e.Location) || rTheme.Contains(e.Location) || rClose.Contains(e.Location) || rGroup.Contains(e.Location) || rSite.Contains(e.Location) || rBrand.Contains(e.Location) || rTelegram.Contains(e.Location) || new Rectangle(28, 500, 384, 56).Contains(e.Location) || new Rectangle(28, 628, 384, 32).Contains(e.Location)
                        || dx * dx + dy * dy <= rToggle.Width * rToggle.Width / 4.0;
            Cursor = hand && !busy ? Cursors.Hand : Cursors.Default;
            string tip = rTheme.Contains(e.Location) ? (darkTheme ? "Светлая тема" : "Тёмная тема") : null;
            if (themeTip.GetToolTip(this) != tip) themeTip.SetToolTip(this, tip);
            if (e.Button == MouseButtons.Left && !hand)
                Location = new Point(Location.X + (int)Math.Round((e.X - dragFrom.X) * uiScale),
                    Location.Y + (int)Math.Round((e.Y - dragFrom.Y) * uiScale));
        }

        static void Open(string url) { try { Process.Start(url); } catch { } }

        internal StyledDialog CreateDialog(string message, string title, MessageBoxButtons buttons,
            MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, float requestedScale = 0, bool allowDismiss = false)
        {
            var dialog = new StyledDialog(this, darkTheme, fBold, fMed, message, title, buttons, icon, defaultButton, requestedScale, allowDismiss);
            if (allowDismiss) dialog.AddBrowserControls(BrowserCatalog.Find(), browser => RestartSelectedBrowser(dialog, browser));
            if (buttons == MessageBoxButtons.OK)
                dialog.AddSupportControls((eventTitle, eventMessage) => DiagnosticReport.Create(eventTitle, eventMessage,
                    "Выбранный DNS: " + Dns.Name[dnsSet] + "\nDoH selected: " + secureDns + "; encrypted-only: " + Dns.EncryptedOnly(dnsSet) +
                    "\nОбщий DNS active: " + active + "; confirmed: " + dnsConfirmed + "; backup: " + hasBackup +
                    "\nAntigravity checked: " + agStatus.Checked + "; complete: " + agStatus.Complete + "; provider: " + agStatus.Provider + "; external: " + agStatus.External),
                    (icon == MessageBoxIcon.Warning || icon == MessageBoxIcon.Error) && DiagnosticReport.ShouldOffer(title, message));
            return dialog;
        }

        void RestartSelectedBrowser(StyledDialog owner, BrowserChoice browser)
        {
            if (DnsElevation.IsAdministrator())
            {
                owner.ShowBrowserStatus("Для перезапуска браузера запустите переключатель DNS обычным способом, без прав администратора.", true);
                return;
            }
            owner.SetBrowserBusy(true);
            owner.ShowBrowserStatus("Определяем активные профили " + browser.Name + ". Затем закроем его окна обычным способом и откроем снова.\n\nЕсли браузер попросит сохранить данные, ответьте в его окне. Ожидание завершения — до 15 секунд; принудительного закрытия не будет.", false);
            ThreadPool.QueueUserWorkItem(_ => {
                string error = null;
                try { BrowserRestart.Run(browser, new WindowsBrowserSystem()); }
                catch (Exception e) { error = e.Message; }
                if (IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => {
                    if (owner.IsDisposed) return;
                    owner.SetBrowserBusy(false);
                    owner.ShowBrowserStatus(error ?? (browser.Name + " запущен. Для ранее работающего браузера использованы сохранённые профили.\n\nМожно продолжать работу. Восстановление вкладок зависит от возможностей браузера."), error != null);
                })); } catch (InvalidOperationException) { }
            });
        }

        DialogResult ShowMessage(string message, string title, MessageBoxButtons buttons,
            MessageBoxIcon icon, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        {
            using (var dialog = CreateDialog(message, title, buttons, icon, defaultButton))
                return dialog.ShowDialog(this);
        }

        // смена набора: если DNS уже включён, сразу переключаем адреса
        void ScrollProviders(int delta)
        {
            providerScroll = Math.Max(0, Math.Min(rSeg.Length * 110 - providerViewport.Width, providerScroll + delta));
            Invalidate();
        }
        void EnsureProviderVisible()
        {
            if (rSeg[dnsSet].Left - providerScroll < providerViewport.Left)
                ScrollProviders(rSeg[dnsSet].Left - providerScroll - providerViewport.Left);
            else if (rSeg[dnsSet].Right - providerScroll > providerViewport.Right)
                ScrollProviders(rSeg[dnsSet].Right - providerScroll - providerViewport.Right);
        }
        void CheckDnsServers()
        {
            if (probing) return;
            if (secureDns || Dns.EncryptedOnly(dnsSet)) { probeLabels = Enumerable.Repeat("Режим DoH · тест через HTTPS", Dns.Name.Length).ToArray(); Invalidate(); return; }
            if (!NetworkCheck.HasConnection()) { probeLabels = Enumerable.Repeat("Нет сетевого подключения", Dns.Name.Length).ToArray(); Invalidate(); return; }
            if (VpnCheck.Scan().NeedsAttention)
            {
                probeLabels = Enumerable.Repeat("Для теста отключите VPN", Dns.Name.Length).ToArray();
                Invalidate(); return;
            }
            probing = true; Invalidate();
            ThreadPool.QueueUserWorkItem(_ => {
                var tasks = Enumerable.Range(0, Dns.Name.Length).Select(k => System.Threading.Tasks.Task.Factory.StartNew(() => DnsProbe.CheckAvailability(k))).ToArray();
                System.Threading.Tasks.Task.WaitAll(tasks);
                var vpn = VpnCheck.Scan();
                if (IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => {
                    UpdateVpnStatus(vpn);
                    if (vpn.NeedsAttention) { probing = false; probeLabels = Enumerable.Repeat("Для теста отключите VPN", Dns.Name.Length).ToArray(); Invalidate(); return; }
                    for (int k = 0; k < tasks.Length; k++)
                    {
                        probeResults[k] = tasks[k].Result;
                        int count = probeResults[k].Where(r => r.Host == "example.com" && r.Ok).Count();
                        probeLabels[k] = Dns.EncryptedOnly(k) ? (count > 0 ? "DoH отвечает через HTTPS" : "DoH не ответил") : "Ответы DNS: " + count + "/2";
                    }
                    probing = false; Invalidate();
                })); } catch (InvalidOperationException) { }
            });
        }
        void ShowDnsDiagnostics()
        {
            using (var dialog = CreateDialog("Выберите DNS и нажмите «Проверить».\n\nПроверим основной и резервный серверы, время ответа и домены сервисов. Результаты появятся здесь.",
                "Тест DNS", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1))
            {
                dialog.AddDnsTestControls(Dns.Name, dnsSet, provider => RunDnsTest(dialog, provider));
                dialog.ShowDialog(this);
            }
        }
        void RunDnsTest(StyledDialog dialog, int provider)
        {
            bool encrypted = secureDns || Dns.EncryptedOnly(provider);
            if (encrypted && EncryptedDns.Templates[provider] == null) { dialog.ShowDnsTestStatus("Для этого провайдера HTTPS-тест пока недоступен. Выберите Comss.one, GeoHide, Bezmezhau или DNS-AI. Обычные DNS-запросы не отправлялись.", "Нет параметров DoH", false, true); return; }
            if (!NetworkCheck.HasConnection()) { dialog.ShowDnsTestStatus(NetworkCheck.OfflineMessage, "Нет подключения", false, true); return; }
            var vpn = VpnCheck.Scan();
            if (vpn.NeedsAttention)
            {
                dialog.ShowDnsTestStatus("Отключите VPN и нажмите «Проверить» ещё раз. VPN может перехватывать запросы к любому серверу.\n\n" + vpn.Details,
                    "Сначала отключите VPN", false, true); return;
            }
            dialog.ShowDnsTestStatus("Проверяем оба сервера " + Dns.Name[provider] + " и домены сервисов…", "Проверяем " + Dns.Name[provider], true, false);
            ThreadPool.QueueUserWorkItem(_ => {
                DnsProbeResult[] results = null; string failure = null;
                try {
                    results = encrypted ? DnsProbe.HostsForProvider(provider).Select(host => DnsProbe.CheckHttps(EncryptedDns.Templates[provider], host)).ToArray() : DnsProbe.CheckProvider(provider);
                }
                catch (Exception ex) { failure = ex.Message; }
                if (IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => {
                    if (dialog.IsDisposed || dialog.Disposing) return;
                    if (failure != null) { dialog.ShowDnsTestStatus(failure, "Не удалось проверить", false, true); return; }
                    if (VpnCheck.Scan().NeedsAttention) { dialog.ShowDnsTestStatus("Во время теста обнаружен VPN. Отключите его и повторите проверку.", "Результат недостоверен", false, true); return; }
                    probeResults[provider] = results;
                    int count = results.Count(r => r.Host == "example.com" && r.Ok);
                    probeLabels[provider] = encrypted ? "DoH: " + results.Count(r => r.Ok) + "/" + results.Length + " ответов" : "Ответы DNS: " + count + "/2";
                    string report = encrypted ? "HTTPS: " + EncryptedDns.Templates[provider] + "\n\n" + string.Join("\n", results.Select(r => r.Host + " — " + (r.Ok ? r.Milliseconds + " мс" : r.Error))) + "\n\nЭто проверка сервера через HTTPS, не DNS браузера. Системные настройки не изменялись." : DnsProbe.Report(provider, results);
                    dialog.ShowDnsTestStatus(report, (encrypted ? "HTTPS-ответы: " : "Прямые ответы: ") + results.Count(r => r.Ok) + "/" + results.Length, false, results.Any(r => !r.Ok));
                    Invalidate();
                })); } catch (InvalidOperationException) { }
            });
        }
        void PickSet(int k)
        {
            if (busy || k == dnsSet) return;
            if (active) Apply(true, k);
            else { dnsSet = k; if (Dns.EncryptedOnly(k)) secureDns = true; EnsureProviderVisible(); Invalidate(); }
        }

        void Toggle()
        {
            if (busy) return;
            bool resetKnown = active && !hasBackup;
            if (resetKnown && ShowMessage(
                "DNS сервиса уже задан, но копии прежних настроек нет.\n\nПерейти на автоматическое получение DNS? " +
                "Изменятся только активные адаптеры, в которых все ручные DNS-адреса соответствующего семейства входят в список программы. " +
                "Смешанные и сторонние адреса останутся без изменений.\n\nЭто не восстановит неизвестные прежние ручные настройки.",
                "Нет резервной копии DNS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Apply(!active, null, resetKnown);
        }

        void RemoveOldAntigravity()
        {
            if (busy || agLoading) return;
            using (var dialog = CreateDialog("Удалить старые DNS-правила Antigravity?\n\nAntigravity перейдёт на выбранный системный DNS. Полная копия правил сохранится. Старый анлокер может создать их снова.",
                "Удалить старые правила?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))
            {
                dialog.SetPrimaryText("Удалить");
                if (dialog.ShowDialog(this) != DialogResult.Yes) return;
            }
            busy = true; agLoading = true; Invalidate();
            ThreadPool.QueueUserWorkItem(_ => {
                string err;
                try { err = DnsElevation.Run(false, 3); }
                catch (Exception ex) { err = ex.Message; }
                var status = AntigravityDns.GetStatus();
                bool backup = Directory.Exists(AgCleanupBackup.Root);
                if (IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => {
                    busy = false; agLoading = false; agStatus = status;
                    cleanupBackupAvailable = backup;
                    cleanupDone = err == null && status.Error == null && !status.External;
                    Invalidate(); Update();
                    if (err != null || status.Error != null)
                        ShowMessage(err ?? status.Error, "Удаление старых правил", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else if (status.External)
                        ShowMessage("Есть другие правила Antigravity. Они не удалены. Проверьте настройки прежнего анлокера.", "Правила Antigravity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                })); } catch (InvalidOperationException) { }
            });
        }

        void ToggleAntigravity()
        {
            if (busy || agLoading) return;
            bool enable = !agStatus.Checked;
            if (enable && active && secureDns) {
                if (agStatus.External) RemoveOldAntigravity();
                else ShowMessage("В режиме DoH отдельная галочка DNS не нужна. Ошибка 400 может требовать другого маршрута подключения, а не смены DNS.", "Режим DoH", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int provider = AntigravityDns.ProviderForMainSet(dnsSet);
            if (enable && agStatus.Error == null && provider < 0)
            {
                ShowMessage(Dns.EncryptedOnly(dnsSet) ? "DNS-AI используется через общий DoH. Включите его большой кнопкой. Отдельные DNS-правила Antigravity для него не создаются." : dnsSet == 0 ? "Comss для доп. DNS Antigravity пока отключён. Выберите другой DNS выше. Общий DNS Comss остаётся доступен." : "Для Antigravity выберите выше GeoHide, GeoHide EU или Bezmezhau. Xbox DNS предназначен для Xbox.",
                    "Выберите DNS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string providerName = provider >= 0 ? AntigravityDns.ProviderNames[provider] : "DNS";
            bool force = false;
            if (enable && agStatus.External)
            {
                using (var dialog = CreateDialog("Antigravity уже настроен другой программой.\n\nЗаменить её DNS на " + providerName + "? Прежние настройки сохранятся.\n\nЗапущенный Antigravity будет перезапущен. Сохраните изменения перед продолжением.",
                    "Заменить DNS Antigravity?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))
                {
                    dialog.SetPrimaryText("Заменить");
                    if (dialog.ShowDialog(this) != DialogResult.Yes) return;
                }
                force = true;
            }
            if (enable && agStatus.Error == null)
            {
                var vpn = VpnCheck.Scan();
                if (vpn.NeedsAttention) { ShowMessage("Отключите VPN и повторите подключение.\n\n" + vpn.Details,
                    "Подключение заблокировано", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (!force && ShowMessage("Включить " + providerName + " для Antigravity?\n\nЗапущенный Antigravity будет перезапущен. Сохраните изменения перед продолжением.\n\nСнимите галочку, чтобы отменить DNS-настройку.",
                    "Доп. DNS Antigravity", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            bool readOnly = agStatus.Error != null;
            if (!readOnly) DiagnosticReport.Record("Antigravity", "enable=" + enable + "; provider=" + providerName + "; force=" + force);
            busy = true; agLoading = true; Invalidate();
            ThreadPool.QueueUserWorkItem(_ => {
                string err = null;
                try { if (!readOnly) err = DnsElevation.Run(enable, enable ? (force ? 101 : 100) + provider * 2 : 2); }
                catch (Exception e) { err = e.Message; }
                var status = AntigravityDns.GetStatus();
                if (!readOnly && enable && err == null && status.Error == null && (!status.Complete || status.Provider != provider))
                    err = "Windows не подтвердила выбранные DNS-настройки Antigravity. Перезапуск не выполнялся.";
                if (AntigravityRestart.Ready(enable, readOnly, err, status, provider))
                {
                    try { AntigravityRestart.Run(); }
                    catch (Exception ex) { err = "Настройки DNS применены, но перезапуск Antigravity не завершён.\n\n" + ex.Message; }
                }
                if (IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => {
                    busy = false; agLoading = false; agStatus = status;
                    Invalidate(); Update();
                    if (err != null || status.Error != null)
                        ShowMessage(err ?? ("Не удалось проверить правила Antigravity: " + status.Error), "Настройка Antigravity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Invalidate();
                })); } catch (InvalidOperationException) { }
            });
        }

        void Apply(bool turnOn, int? selectedSet = null, bool resetKnown = false, bool? encrypted = null)
        {
            int requestedSet = selectedSet ?? dnsSet;
            bool useEncrypted = (encrypted ?? secureDns) || Dns.EncryptedOnly(requestedSet);
            if (turnOn && useEncrypted && EncryptedDns.Templates[requestedSet] == null) { ShowMessage("Для DoH выберите Comss.one, GeoHide, Bezmezhau или DNS-AI либо снимите галочку шифрования.", "DoH недоступен", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            bool keepAntigravity = false;
            if (turnOn)
            {
                if (!NetworkCheck.HasConnection()) { networkAvailable = false; Invalidate(); ShowMessage(NetworkCheck.OfflineMessage, "Нет подключения", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                var vpnStatus = VpnCheck.Scan();
                if (vpnStatus.NeedsAttention) { ShowMessage("Отключите VPN и повторите подключение.\n\n" + vpnStatus.Details,
                    "Подключение заблокировано", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (useEncrypted)
                {
                    try
                    {
                        var policies = NrptRegistryReader.Read(true);
                        if (EncryptedDns.CanKeepAntigravity(NrptRegistryReader.Read(false), policies))
                        {
                            using (var dialog = CreateDialog("Подключить DoH, сохранив настройки Antigravity?\n\nДва домена Antigravity продолжат использовать прежние DNS. Остальные запросы пойдут через DoH. IPv6 временно приостановится.", "DoH и Antigravity", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))
                            {
                                dialog.SetPrimaryText("Подключить");
                                if (dialog.ShowDialog(this) != DialogResult.Yes) return;
                            }
                            keepAntigravity = true;
                        }
                        else if (policies.Length > 0) { ShowMessage(EncryptedDns.PolicyConflict(policies), "Отдельные DNS-правила", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    }
                    catch (Exception ex) { ShowMessage("Не удалось проверить DNS-правила: " + ex.Message, "Ошибка DNS", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                }
            }
            busy = true; error = null; Invalidate();
            DiagnosticReport.Record("Общий DNS", "enable=" + turnOn + "; provider=" + Dns.Name[requestedSet] + "; DoH=" + useEncrypted);
            int set = selectedSet ?? dnsSet;
            dnsSet = set;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;
                try {
                    if (turnOn && !NetworkCheck.HasConnection()) err = NetworkCheck.OfflineMessage;
                    else if (turnOn && useEncrypted && !DnsProbe.CheckHttps(EncryptedDns.Templates[set], "example.com").Ok)
                        err = "Сервер не ответил через HTTPS. Обычный DNS не использовался, настройки не изменены.";
                    else if (turnOn && !useEncrypted && !DnsProbe.CheckAvailability(set).Any(r => r.Host == "example.com" && r.Ok))
                        err = NetworkCheck.HasConnection() ? "Сетевое подключение есть, но оба DNS-сервера не ответили. Возможны отсутствие интернета или блокировка запросов. Настройки не изменены. Подробнее — «Тест DNS»." : NetworkCheck.OfflineMessage;
                    else err = turnOn ? (useEncrypted ? DnsElevation.Run(true, (keepAntigravity ? 300 : 200) + set) : Dns.Enable(set)) : resetKnown ? Dns.ResetKnown() : Dns.Disable();
                }
                catch (Exception ex) { err = ex.Message; }
                Thread.Sleep(600);
                int now = -1;
                bool pending = false;
                bool availableBackup = false, connected = NetworkCheck.HasConnection();
                try { now = Dns.ActiveSet(); pending = Dns.HasBackup(); availableBackup = pending && Dns.HasAvailableBackup(); }
                catch (Exception ex) { err = err ?? ex.Message; }
                if (IsDisposed || Disposing) return;
                BeginInvoke((Action)(() =>
                {
                    busy = false;
                    hasBackup = pending;
                    if (turnOn && err == null) secureDns = useEncrypted;
                    networkAvailable = connected;
                    dnsConfirmed = now >= 0;
                    active = now >= 0 || availableBackup;
                    if (now >= 0) dnsSet = now;
                    if (turnOn && err == null && now != set) err = "Выбранные DNS не подтверждены на всех активных адаптерах. Другой адаптер или VPN может использовать прежние DNS. Проверьте подключения Windows.";
                    EnsureProviderVisible();
                    Invalidate(); Update();
                    if (err != null && err.StartsWith("[DNS-DEFERRED]", StringComparison.Ordinal))
                    {
                        error = "Копия недоступного адаптера сохранена";
                        ShowMessage(err.Substring("[DNS-DEFERRED]".Length), "Восстановление отложено", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (err == NetworkCheck.OfflineMessage)
                    {
                        error = null;
                        ShowMessage(err, "Нет подключения", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (err != null)
                    {
                        error = "Не удалось применить все настройки";
                        ShowMessage( err, "Ошибка DNS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    else if (turnOn)
                        ShowDnsReminder();
                    Invalidate();
                }));
            });
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { ProcessSecurity.Initialize(); }
            catch (Exception error)
            {
                if (args.Length == 0)
                    MessageBox.Show(error.Message, "Ошибка безопасного запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 3;
            }
            if (args.Length != 0) return DnsElevation.Handle(args, Dns.ExecuteDirect);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
