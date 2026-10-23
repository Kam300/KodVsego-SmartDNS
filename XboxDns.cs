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
        public static readonly string[] Name = ProviderCatalog.All.Select(p=>p.name).ToArray();
        public static readonly string[][] V4 = ProviderCatalog.All.Select(p=>p.ipv4).ToArray();
        public static readonly string[][] V6 = ProviderCatalog.All.Select(p=>p.ipv6).ToArray();
        public static readonly string[] Opens = ProviderCatalog.All.Select(p=>p.description).ToArray();
        public static readonly string[] Site = ProviderCatalog.All.Select(p=>p.site).ToArray();
        public static readonly string[] Url = ProviderCatalog.All.Select(p=>p.url).ToArray();
        // Keep historical IDs for restore; availability is shared with Android in config/providers.json.
        internal static bool IsAvailable(int provider) { return provider>=0 && provider<Name.Length && ProviderAvailability.IsEnabled(provider); }
        internal static int[] AvailableProviders { get { return Enumerable.Range(0,Name.Length).Where(IsAvailable).ToArray(); } }
        internal static bool EncryptedOnly(int provider) { return provider>=0&&provider<ProviderCatalog.All.Length&&ProviderCatalog.All[provider].encryptedOnly; }
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
            for (int k = 0; k < V4.Length; k++)
                if (ProviderCatalog.All[k].legacyIpv4.Any(old => addresses.SequenceEqual(old))) return k;
            return -1;
        }
        internal static string[] KnownServers { get { return V4.SelectMany(s=>s).Concat(V6.SelectMany(s=>s)).Concat(XboxOld)
            .Concat(ProviderCatalog.All.SelectMany(p=>p.windowsDohServers)).Concat(ProviderCatalog.All.SelectMany(p=>p.legacyIpv4).SelectMany(s=>s)).Distinct().ToArray(); } }

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
            if(enable&&set>=0&&set<V4.Length&&!IsAvailable(set))return "Провайдер временно недоступен. Выберите другой DNS.";
            if (enable && !NetworkCheck.HasConnection()) return NetworkCheck.OfflineMessage;
            if (enable && VpnCheck.Scan().NeedsAttention) return "Отключите VPN и повторите подключение. DNS не изменён.";
            if (enable && set < V4.Length && EncryptedOnly(set)) return EncryptedDns.Run(set);
            if (enable && set >= 300 && set < 300 + V4.Length) return EncryptedDns.Run(set - 300, true);
            if (enable && set >= 200 && set < 200 + V4.Length) return EncryptedDns.Run(set - 200);
            if (enable && set >= 100 && set < 100 + AntigravityDns.ServerSets.Length * 2) return AntigravityDns.Run(true, set % 2 == 1, (set - 100) / 2);
            if (!enable && set == 3) return AgCleanup.Run();
            if (!enable && set == UninstallCleanup.RestoreOperation) return UninstallCleanup.RestoreNetworkDirect();
            if (!enable && set == 2) return AntigravityDns.Run(false);
            if (enable && (set < 0 || set >= V4.Length)) return "Неизвестный DNS-провайдер.";
            if (!enable && set != 0 && set != 1) return "Неизвестная операция DNS.";
            if (!enable && set == 1)
                return DnsSettingsEngine.Run(false, KnownServers, true);
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
        const int CanvasWidth = 440, CanvasHeight = 878;
        internal static string VersionLabel { get {
            var assembly = Assembly.GetExecutingAssembly();
            var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly,typeof(AssemblyInformationalVersionAttribute));
            return "v" + (info == null ? assembly.GetName().Version.ToString(3) : info.InformationalVersion);
        } }
        float uiScale,minimumCanvasScale,preferredCanvasScale;
        int layoutWidth=CanvasWidth,layoutHeight=CanvasHeight;
        bool layoutReady;
        int LowerY(int y){return y+layoutHeight-CanvasHeight;}
        PointF canvasOffset;
        bool darkTheme;
        Color Bg { get { return Mix(Color.FromArgb(250, 249, 255), Color.FromArgb(8, 8, 13), themeMix); } }
        Color Accent { get { return Mix(Color.FromArgb(0, 127, 159), Cyan, themeMix); } }
        Color Selection { get { return Cyan; } }
        static readonly Color Cyan = Color.FromArgb(2, 165, 207);
        static readonly Color Highlight = Cyan;
        static readonly Color ContrastInk = Color.FromArgb(21, 29, 34);
        static readonly Color ActiveBg = Cyan;
        Color Ink { get { return Mix(ContrastInk, Color.FromArgb(247, 246, 252), themeMix); } }
        Color Muted { get { return Mix(Color.FromArgb(104, 99, 123), Color.FromArgb(151, 148, 168), themeMix); } }
        Color Soft { get { return Mix(Color.FromArgb(80, 69, 117), Color.FromArgb(204, 196, 244), themeMix); } }
        Color CardBg { get { return Mix(Color.FromArgb(241, 239, 250), Color.FromArgb(18, 18, 27), themeMix); } }
        Color CardLine { get { return Mix(Color.FromArgb(222, 217, 239), Color.FromArgb(43, 40, 59), themeMix); } }
        Color Purple { get { return Mix(Color.FromArgb(0, 127, 159), Color.FromArgb(155, 124, 255), themeMix); } }

        readonly PrivateFontCollection fonts = new PrivateFontCollection();
        readonly Dictionary<string, Image> serviceIcons = new Dictionary<string, Image>();
        readonly List<IntPtr> fontMemory = new List<IntPtr>();
        Bitmap lightBackground, darkBackground, toggleGlow;
        float themeMix, powerMix, hoverMix, selectionLeft, scrollMix;
        bool motionReady, powerHovered, themeHovered, resourcesDisposed;
        readonly Stopwatch frameClock = Stopwatch.StartNew();
        long lastFrame;
        FontFamily fHead, fBold, fMed;
        bool active, busy, hasBackup;
        bool hideDnsReminder = ReadHideDnsReminder();
        AgStatus agStatus = new AgStatus();
        bool agLoading = true;
        bool cleanupDone, cleanupBackupAvailable = Directory.Exists(AgCleanupBackup.Root);
        Rectangle rAgCleanup = new Rectangle(280, 807, 114, 26);
        Rectangle rAgOption = new Rectangle(28, 754, 384, 58);
        Rectangle rSecureOption = new Rectangle(28, 678, 384, 58);
        bool secureDns;
        int dnsSet = Dns.AvailableProviders[0];
        int autoGoal = -1;
        int defaultGoal=-1;
        bool manualSelection;
        bool UseDefaultGoal {get{return defaultGoal>=0&&!manualSelection;}}
        bool autoNeedsRetry, checkingAuto;
        long lastAutoCheck;
        readonly CancellationTokenSource autoLifetime = new CancellationTokenSource();
        int SelectedSegment { get { return autoGoal >= 0||(!active&&UseDefaultGoal) ? Dns.Name.Length : dnsSet; } }
        string error;
        float pulse;
        Rectangle rToggle, rClose, rSite, rGroup, rBrand;
        Rectangle rTheme = new Rectangle(332, 24, 36, 34);
        Rectangle rSettings = new Rectangle(288, 24, 36, 34);
        Rectangle rVersion,rUpdateIndicator;
        UpdateRelease availableUpdate;
        bool checkUpdateAtStartup = true;
        readonly ToolTip themeTip = new ToolTip();
        readonly Rectangle[] rSeg = new Rectangle[Dns.Name.Length + 1];
        int[] VisibleSegments { get { return Dns.AvailableProviders.Concat(new[]{Dns.Name.Length}).ToArray(); } }
        int ProviderItemWidth = 108;
        Rectangle providerViewport = new Rectangle(58, 500, 324, 56);
        Rectangle providerPrevious = new Rectangle(28, 500, 30, 56);
        Rectangle providerNext = new Rectangle(382, 500, 30, 56);
        Rectangle ProviderBar {get{return new Rectangle(28,LowerY(500),layoutWidth-56,56);}}
        Rectangle DnsTestButton {get{return new Rectangle(layoutWidth-138,LowerY(630),92,24);}}
        int providerScroll;
        string[] probeLabels = Enumerable.Repeat("Ответы DNS ещё не проверены", Dns.Name.Length).ToArray();
        bool probing;
        bool dnsConfirmed;
        DnsProbeResult[][] probeResults = new DnsProbeResult[Dns.Name.Length][];
        Point dragFrom;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer networkMonitor = new System.Windows.Forms.Timer();
        VpnStatus liveVpn = new VpnStatus();
        bool checkingNetwork;
        bool initializing = true;
        int operationRevision;
        bool networkAvailable = true;
        internal string AntigravityOnlyLabel { get {
            if (agLoading) return null;
            if (agStatus.Error != null) return "Проверьте Antigravity";
            if (agStatus.Checked) return agStatus.Complete ? (agStatus.Provider == 1 ? "Проверьте Antigravity" : "Только Antigravity") : "Antigravity частично";
            return agStatus.External ? "Правила Antigravity" : null;
        } }
        internal string ConnectionLabel { get { return initializing ? "Проверяем настройки…" : busy ? "Применяем настройки…" : !networkAvailable ? "Нет подключения" : liveVpn.NeedsAttention ?
            (liveVpn.Failed ? "Проверьте VPN" : "VPN мешает DNS") : active ? (hasBackup ? (dnsConfirmed ? (autoGoal >= 0 ? (autoNeedsRetry ? "Проверьте Авто" : "Авто · " + AutoDns.Goals[autoGoal].Name) : "DNS включён") : "Проверьте DNS") : "Найден DNS сервиса") : AntigravityOnlyLabel ?? "DNS выключен"; } }
        internal void UpdateVpnStatus(VpnStatus status)
        {
            if (liveVpn.NeedsAttention != status.NeedsAttention)
            {
                probeResults = new DnsProbeResult[Dns.Name.Length][];
                probeLabels = Enumerable.Repeat(status.NeedsAttention ? "Для теста отключите VPN" : "Ответы DNS ещё не проверены", Dns.Name.Length).ToArray();
            }
            bool changed = liveVpn.NeedsAttention != status.NeedsAttention || liveVpn.Failed != status.Failed;
            liveVpn = status;
            if (changed) Invalidate();
        }

        public MainForm()
        {
            LoadFonts();
            LoadServiceIcons();
            darkTheme = ReadDarkTheme();
            defaultGoal=ReadDefaultGoal();
            themeMix = darkTheme ? 1 : 0;
            Text = "Код Всего · Smart DNS";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            using (var desktop = Graphics.FromHwnd(IntPtr.Zero))
                uiScale = Math.Min(desktop.DpiX / 96f,
                    Math.Min((area.Width - 32f) / CanvasWidth, (area.Height - 32f) / CanvasHeight));
            uiScale = Math.Max(0.5f, uiScale);
            preferredCanvasScale=uiScale;
            uiScale=Math.Max(0.5f,preferredCanvasScale*0.85f);
            var initialSize=new Size((int)Math.Round(CanvasWidth*uiScale),(int)Math.Round(CanvasHeight*uiScale));
            minimumCanvasScale=uiScale;
            MinimumSize=new Size((int)Math.Round(CanvasWidth*minimumCanvasScale),(int)Math.Round(CanvasHeight*minimumCanvasScale));
            ClientSize=initialSize;
            MaximizedBounds=area;
            SetStyle(ControlStyles.ResizeRedraw,true);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            DoubleBuffered = true;
            try { Icon = new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")); } catch { }
            UpdateCanvasBounds();


            anim.Interval = 16;
            anim.Tick += (s, e) => AnimateFrame();

            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseWheel += (s, e) => { if (ProviderBar.Contains(CanvasPoint(e.Location))) ScrollProviderPage(e.Delta > 0 ? -1 : 1); };
            EnsureProviderVisible();
            networkMonitor.Interval = 3000;
            networkMonitor.Tick += (s, e) => RefreshNetworkStatus();
            Shown += (s, e) => { RefreshNetworkStatus(); networkMonitor.Start(); };
            Shown += (s, e) => { if(checkUpdateAtStartup)CheckUpdatesQuietly(); };
            motionReady = true;
            SnapMotion();
        }

        void RefreshNetworkStatus()
        {
            if (checkingNetwork || busy || !Visible || WindowState == FormWindowState.Minimized) return;
            checkingNetwork = true;
            int revision = operationRevision;
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
                    if (!AcceptNetworkSnapshot(revision)) return;
                    bool firstRead = initializing;
                    string before = VisualState();
                    if (networkAvailable != connected) {
                        probeResults = new DnsProbeResult[Dns.Name.Length][];
                        probeLabels = Enumerable.Repeat(connected ? "Ответы DNS ещё не проверены" : "Нет сетевого подключения", Dns.Name.Length).ToArray();
                    }
                    networkAvailable = connected; hasBackup = backup; dnsConfirmed = actual >= 0;
                    agStatus = extraDns; agLoading = false;
                    if (actual >= 0) secureDns = strictNow;
                    active = actual >= 0 || availableBackup;
                    if (actual >= 0 && actual != dnsSet) { if (!firstRead) autoGoal = -1; dnsSet = actual; EnsureProviderVisible(); }
                    initializing = false;
                    if(firstRead)EnsureProviderVisible();
                    UpdateVpnStatus(status);
                    if (before != VisualState()) Invalidate();
                    if (firstRead) CheckDnsServers();
                    CheckAutoConnection();
                })); }
                catch (InvalidOperationException) { }
            });
        }

        internal bool AcceptNetworkSnapshot(int revision)
        { return !busy && revision == operationRevision; }

        void CheckUpdatesQuietly()
        {
            var token=autoLifetime.Token;
            ThreadPool.QueueUserWorkItem(_=> {
                UpdateRelease release;
                try {if(!NetworkCheck.HasConnection())return;release=AppUpdate.Check(VersionLabel,token);}
                catch(Exception){return;}
                if(release==null||IsDisposed||token.IsCancellationRequested)return;
                try{BeginInvoke((Action)(()=>{availableUpdate=release;Invalidate();}));}catch(InvalidOperationException){}
            });
        }

        void DrawSettingsButton(Graphics g)
        {
            using(var path=RoundPath(rSettings,12))
            using(var fill=new SolidBrush(CardBg))
            using(var border=new Pen(CardLine)){g.FillPath(fill,path);g.DrawPath(border,path);}
            float x=rSettings.X+rSettings.Width/2f,y=rSettings.Y+rSettings.Height/2f;
            var teeth=new PointF[32];
            for(int i=0;i<teeth.Length;i++) {
                double angle=i*Math.PI/16;float radius=i%4==0||i%4==3?9f:11f;
                teeth[i]=new PointF(x+radius*(float)Math.Cos(angle),y+radius*(float)Math.Sin(angle));
            }
            using(var pen=new Pen(Accent,1.6f)){pen.LineJoin=LineJoin.Round;g.DrawPolygon(pen,teeth);g.DrawEllipse(pen,x-4,y-4,8,8);}
        }
        internal StyledDialog CreateSettingsDialog()
        {
            bool available=Directory.Exists(AgCleanupBackup.Root);
            var dialog=CreateDialog(available?"Копия правил Antigravity\n\nОткройте папку с сохранёнными копиями правил.":"Копия правил Antigravity\n\nСохранённых копий пока нет. Копия создаётся при удалении старых правил.","Настройки",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1,uiScale);
            dialog.AddSettingsControls(available,()=>Open(AgCleanupBackup.Root),AutoDns.Goals.Select(goal=>goal.Name).ToArray(),defaultGoal,url=>Open(url));return dialog;
        }
        void ShowSettings()
        {
            using(var dialog=CreateSettingsDialog()) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                int selected=dialog.SettingsGoal;
                try {
                    using(var key=Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KodVsego\SmartDns"))key.SetValue("DefaultService",selected<0?"":AutoDns.Goals[selected].Name,RegistryValueKind.String);
                    defaultGoal=selected;manualSelection=false;if(selected<0)autoGoal=-1;EnsureProviderVisible();Invalidate();
                }catch(Exception error){ShowMessage("Не удалось сохранить сервис по умолчанию.\n\n"+error.Message,"Настройки",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }
        }
        static int ReadDefaultGoal()
        {
            try{using(var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))return AutoDns.GoalIndex(key==null?null:key.GetValue("DefaultService") as string);}
            catch(System.Security.SecurityException){return -1;}catch(UnauthorizedAccessException){return -1;}catch(IOException){return -1;}
        }

        void ShowUpdates()
        {
            if(busy)return;
            bool working=false;bool msi=AppUpdate.IsMsiManaged(Application.ExecutablePath);UpdateRelease release=availableUpdate;
            using(var cancellation=new CancellationTokenSource())
            using(var dialog=CreateDialog("Проверка новых версий через GitHub.\n\nТекущая версия: "+VersionLabel,"Обновление программы",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1,uiScale)) {
                var token=cancellation.Token;
                dialog.FormClosed+=(s,e)=>cancellation.Cancel();
                Action action=null;
                action=()=> {
                    if(working)return;
                    if(msi&&release!=null) {
                        try{Process.Start(new ProcessStartInfo(AppUpdate.MsiReleasePage(release.Version)){UseShellExecute=true});}
                        catch(Exception error){dialog.ShowUpdateStatus(error.Message,"Не удалось открыть GitHub","Открыть релиз",true);}
                        return;
                    }
                    working=true;
                    bool download=release!=null;
                    dialog.ShowUpdateStatus(download?"Скачиваем обновление.\nНастройки DNS и тема сохранятся.":"Проверяем новые версии на GitHub…",download?"Загрузка":"Проверка обновлений",download?"Скачиваем…":"Проверяем…",false);
                    ThreadPool.QueueUserWorkItem(_=> {
                        UpdateTicket ticket=null;string failure=null;
                        try {
                            if(download) {
                                if(DnsElevation.IsAdministrator())throw new IOException("Для обновления запустите программу обычным способом, без прав администратора.");
                                int last=-1;
                                string package=AppUpdate.Download(release,token,percent=> {
                                    if(percent==last)return;last=percent;
                                    if(IsDisposed||dialog.IsDisposed||token.IsCancellationRequested)return;
                                    try{BeginInvoke((Action)(()=>{if(!dialog.IsDisposed&&!token.IsCancellationRequested)dialog.ShowUpdateStatus("Скачиваем версию "+release.Version+".\nПосле проверки программа перезапустится.","Загрузка "+percent+"%","Скачиваем…",false);}));}catch(InvalidOperationException){}
                                });
                                ticket=AppUpdate.StartInstall(package,release,token);
                            }
                            else release=AppUpdate.Check(VersionLabel,token);
                        }
                        catch(OperationCanceledException){if(ticket!=null)ticket.Cancel();return;}
                        catch(Exception ex){failure=ex.Message;}
                        if(IsDisposed||dialog.IsDisposed||token.IsCancellationRequested){if(ticket!=null)ticket.Cancel();return;}
                        try{BeginInvoke((Action)(()=> {
                            if(dialog.IsDisposed||token.IsCancellationRequested){if(ticket!=null)ticket.Cancel();return;}
                            working=false;
                            if(failure!=null){dialog.ShowUpdateStatus((download?"Не удалось скачать обновление.":"Не удалось проверить обновления.")+"\n\n"+failure,download?"Текущая версия сохранена":"GitHub недоступен",download?"Повторить загрузку":"Повторить проверку",true);return;}
                            if(ticket!=null) {
                                try {ticket.Commit();dialog.DialogResult=DialogResult.OK;dialog.Close();Close();}
                                catch(Exception ex){ticket.Cancel();dialog.ShowUpdateStatus(ex.Message,"Не удалось перезапустить","Повторить",true);}
                                return;
                            }
                            availableUpdate=release;Invalidate();
                            if(msi&&release!=null){dialog.ShowUpdateStatus("Установлена: "+VersionLabel+".\nНовая версия: "+release.Version+".\nДля этой копии нужен установщик MSI со страницы релиза.","Доступно обновление MSI","Открыть релиз",true);return;}
                            dialog.ShowUpdateStatus(release==null?"Новых стабильных версий нет.\nСейчас установлена "+VersionLabel:"Установлена: "+VersionLabel+".\nНовая версия: "+release.Version+".\nСкачивание: "+Math.Ceiling(release.Size/1024d)+" КБ.\nПосле установки программа перезапустится.",
                                release==null?"Обновлений нет":"Доступно обновление",release==null?"Проверить ещё раз":"Обновить",true);
                        }));}catch(InvalidOperationException){if(ticket!=null)ticket.Cancel();}
                    });
                };
                dialog.AddUpdateControls(()=>action());
                if(msi&&release!=null)dialog.ShowUpdateStatus("Установлена: "+VersionLabel+".\nНовая версия: "+release.Version+".\nДля этой копии нужен установщик MSI со страницы релиза.","Доступно обновление MSI","Открыть релиз",true);
                else if(release!=null)dialog.ShowUpdateStatus("Установлена: "+VersionLabel+".\nНовая версия: "+release.Version+".\nПосле установки программа перезапустится.","Доступно обновление","Обновить",true);
                else action();
                dialog.ShowDialog(this);
            }
        }

        void CheckAutoConnection()
        {
            if (checkingAuto || autoGoal < 0 || !active || !dnsConfirmed || busy || initializing ||
                !networkAvailable || liveVpn.NeedsAttention || frameClock.ElapsedMilliseconds - lastAutoCheck < 60000) return;
            checkingAuto = true; lastAutoCheck = frameClock.ElapsedMilliseconds;
            int goal = autoGoal, provider = dnsSet, revision = operationRevision;
            bool encrypted = secureDns;
            var token = autoLifetime.Token;
            ThreadPool.QueueUserWorkItem(_ => {
                bool available = false;
                try { available = AutoDns.Best(AutoDns.Search(goal, encrypted, new AutoDnsProbe(), token, null, provider)) != null; }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
                if (IsDisposed || token.IsCancellationRequested) return;
                try { BeginInvoke((Action)(() => {
                    checkingAuto = false;
                    if (!AcceptNetworkSnapshot(revision) || autoGoal != goal || dnsSet != provider) return;
                    if (autoNeedsRetry != !available) { autoNeedsRetry = !available; Invalidate(); }
                })); } catch (InvalidOperationException) { }
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if(e.CloseReason==CloseReason.UserClosing&&(busy||agLoading))e.Cancel=true;
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed) {
                resourcesDisposed = true;
                autoLifetime.Cancel(); autoLifetime.Dispose();
                anim.Dispose(); networkMonitor.Dispose(); themeTip.Dispose();
                foreach (var image in serviceIcons.Values) image.Dispose();
                serviceIcons.Clear();
                if (lightBackground != null) lightBackground.Dispose();
                if (darkBackground != null) darkBackground.Dispose();
                if (toggleGlow != null) toggleGlow.Dispose();
                fonts.Dispose();
                foreach (var ptr in fontMemory) Marshal.FreeCoTaskMem(ptr);
                fontMemory.Clear();
            }
            base.Dispose(disposing);
        }

        static bool ReadDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))
                    return key == null || !string.Equals(key.GetValue("Theme") as string, "Light", StringComparison.Ordinal);
            }
            catch (System.Security.SecurityException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
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
            Color surface = Mix(CardBg, Accent, themeHovered ? .16f : .07f);
            using (var path = RoundPath(rTheme, 12))
            using (var fill = new LinearGradientBrush(rTheme, Mix(surface, Accent, .07f), surface, 90f))
            using (var line = new Pen(Mix(CardLine, Accent, themeHovered ? .65f : .25f)))
            { g.FillPath(fill, path); g.DrawPath(line, path); }
            var state = g.Save();
            g.TranslateTransform(rTheme.X + 6, rTheme.Y + 5);
            using (var pen = new Pen(Accent, 1.7f) { StartCap=LineCap.Round, EndCap=LineCap.Round, LineJoin=LineJoin.Round })
            {
                if (darkTheme)
                {
                    using (var fill = new SolidBrush(Color.FromArgb(40, Accent))) g.FillEllipse(fill,8,8,8,8);
                    g.DrawEllipse(pen,8,8,8,8);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(pen,12+(float)Math.Cos(a)*7,12+(float)Math.Sin(a)*7,
                            12+(float)Math.Cos(a)*9.5f,12+(float)Math.Sin(a)*9.5f);
                    }
                }
                else
                {
                    using(var moon=new GraphicsPath())
                    {
                        moon.AddBezier(12.4f,3.2f,7.4f,2.7f,3.3f,6.5f,3.3f,11.5f);
                        moon.AddBezier(3.3f,11.5f,3.3f,16.6f,7.4f,20.7f,12.5f,20.7f);
                        moon.AddBezier(12.5f,20.7f,16.2f,20.7f,19.3f,18.5f,20.6f,15.4f);
                        moon.AddBezier(20.6f,15.4f,16.1f,16.5f,11.7f,13.4f,11.7f,8.8f);
                        moon.AddBezier(11.7f,8.8f,11.7f,6.8f,12,5,12.4f,3.2f);moon.CloseFigure();
                        using(var fill=new SolidBrush(Color.FromArgb(36,Accent)))g.FillPath(fill,moon);
                        g.DrawPath(pen,moon);
                    }
                    using(var star=new GraphicsPath())using(var fill=new SolidBrush(Accent)){
                        star.AddPolygon(new[]{new PointF(18,2.5f),new PointF(19,5.1f),new PointF(21.5f,6),new PointF(19,7),new PointF(18,9.5f),new PointF(17,7),new PointF(14.5f,6),new PointF(17,5.1f)});g.FillPath(fill,star);
                    }
                }
            }
            g.Restore(state);
        }

        Rectangle rToggleGlow()
        {
            var r = rToggle; r.Inflate(40, 40);
            return Rectangle.Ceiling(new RectangleF(canvasOffset.X+r.X * uiScale,canvasOffset.Y+r.Y * uiScale,r.Width * uiScale,r.Height * uiScale));
        }

        // Use logical pixels for fonts and geometry alike. Point fonts apply the
        // monitor DPI a second time when drawing on a scaled canvas.
        static Font UiFont(FontFamily family, float points)
        {
            return new Font(family, points * 96f / 72f, GraphicsUnit.Pixel);
        }

        Point CanvasPoint(Point point)
        {
            return new Point((int)Math.Round((point.X-canvasOffset.X)/uiScale),(int)Math.Round((point.Y-canvasOffset.Y)/uiScale));
        }

        void LoadServiceIcons()
        {
            var names = new[] { "chatgpt", "claude", "gemini", "elevenlabs", "xbox", "spotify" };
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
                    fontMemory.Add(ptr);
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

        static Color Mix(Color from, Color to, float amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb((int)(from.A + (to.A - from.A) * amount),
                (int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount),
                (int)(from.B + (to.B - from.B) * amount));
        }

        string VisualState()
        {
            return ConnectionLabel + "|" + dnsSet + "|" + secureDns + "|" + hasBackup + "|" + dnsConfirmed + "|" +
                agLoading + "|" + agStatus.Checked + "|" + agStatus.Complete + "|" + agStatus.External + "|" +
                agStatus.Error + "|" + agStatus.Provider + "|" + agStatus.RecoveryNeeded;
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SystemParametersInfoW(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

        bool MotionAllowed { get {
            bool enabled;
            return !SystemInformation.TerminalServerSession && SystemParametersInfoW(0x1042, 0, out enabled, 0) && enabled;
        } }

        // No continuous animation when connected. Frames are requested only for transitions or a pending operation.
        internal void SnapMotion()
        {
            themeMix = darkTheme ? 1 : 0; powerMix = active ? 1 : 0; hoverMix = powerHovered ? 1 : 0;
            selectionLeft = rSeg[SelectedSegment].X; scrollMix = providerScroll;
            anim.Stop();
        }

        bool MotionPending()
        {
            return Math.Abs(themeMix - (darkTheme ? 1 : 0)) > 0.002f || Math.Abs(powerMix - (active ? 1 : 0)) > 0.002f ||
                Math.Abs(hoverMix - (powerHovered ? 1 : 0)) > 0.002f ||
                Math.Abs(selectionLeft - rSeg[SelectedSegment].X) > 0.1f || Math.Abs(scrollMix - providerScroll) > 0.1f;
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);
            if (!motionReady || IsDisposed || Disposing) return;
            if (!MotionAllowed) { SnapMotion(); return; }
            if (Visible && WindowState != FormWindowState.Minimized && (busy || MotionPending()) && !anim.Enabled)
            { lastFrame = frameClock.ElapsedTicks; anim.Start(); }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) anim.Stop();
            else if (motionReady) Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateCanvasBounds();
            if (WindowState == FormWindowState.Minimized) anim.Stop();
            else if (motionReady) Invalidate();
        }
        void UpdateCanvasBounds()
        {
            if(minimumCanvasScale<=0||ClientSize.Width<=0||ClientSize.Height<=0)return;
            uiScale=Math.Max(0.1f,Math.Min(preferredCanvasScale,Math.Min(ClientSize.Width/(float)CanvasWidth,ClientSize.Height/(float)CanvasHeight)));
            canvasOffset=PointF.Empty;
            int width=Math.Max(CanvasWidth,(int)Math.Round(ClientSize.Width/uiScale)),height=Math.Max(CanvasHeight,(int)Math.Round(ClientSize.Height/uiScale));
            if(!layoutReady||width!=layoutWidth||height!=layoutHeight) {
                layoutReady=true;
                int first=providerScroll/ProviderItemWidth;
                layoutWidth=width;layoutHeight=height;
                rClose=new Rectangle(width-58,22,36,36);rTheme=new Rectangle(width-108,24,36,34);rSettings=new Rectangle(width-152,24,36,34);
                rToggle=new Rectangle((width-200)/2,196+(height-CanvasHeight)/2,200,200);
                rSecureOption=new Rectangle(28,LowerY(678),width-56,58);rAgOption=new Rectangle(28,LowerY(754),width-56,58);rAgCleanup=new Rectangle(width-160,LowerY(775),114,26);
                providerViewport=new Rectangle(58,LowerY(500),width-116,56);providerPrevious=new Rectangle(28,LowerY(500),30,56);providerNext=new Rectangle(width-58,LowerY(500),30,56);
                int visible=Math.Min(VisibleSegments.Length,Math.Max(3,providerViewport.Width/108));ProviderItemWidth=providerViewport.Width/visible;
                for(int k=0;k<rSeg.Length;k++)rSeg[k]=Rectangle.Empty;
                var segments=VisibleSegments;for(int slot=0;slot<segments.Length;slot++)rSeg[segments[slot]]=new Rectangle(58+slot*ProviderItemWidth,LowerY(500),ProviderItemWidth,56);
                providerScroll=Math.Max(0,Math.Min(VisibleSegments.Length*ProviderItemWidth-providerViewport.Width,first*ProviderItemWidth));
                selectionLeft=rSeg[SelectedSegment].X;EnsureProviderVisible();scrollMix=providerScroll;
            }
            var previous=Region;
            Region=WindowState==FormWindowState.Maximized?null:RoundRegion(ClientRectangle,(int)Math.Round(22*uiScale));
            if(previous!=null)previous.Dispose();
        }
        protected override void SetClientSizeCore(int width,int height)
        {base.SetClientSizeCore(width,height);UpdateCanvasBounds();}
        internal int ResizeHitTest(Point point)
        {
            if(WindowState!=FormWindowState.Normal||!ClientRectangle.Contains(point))return 0;
            int edge=Math.Max(8,(int)Math.Round(10*minimumCanvasScale));
            int corner=Math.Max(edge,(int)Math.Round(18*uiScale));
            if(point.Y<corner){if(point.X<corner)return 13;if(point.X>=ClientSize.Width-corner)return 14;}
            if(point.Y>=ClientSize.Height-corner){if(point.X<corner)return 16;if(point.X>=ClientSize.Width-corner)return 17;}
            bool left=point.X<edge,right=point.X>=ClientSize.Width-edge,top=point.Y<edge,bottom=point.Y>=ClientSize.Height-edge;
            return top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:0;
        }
        protected override void WndProc(ref Message message)
        {
            if(message.Msg==0x84) {
                long position=message.LParam.ToInt64();
                var point=PointToClient(new Point((short)(position&0xffff),(short)((position>>16)&0xffff)));
                int hit=ResizeHitTest(point);if(hit!=0){message.Result=new IntPtr(hit);return;}
            }
            base.WndProc(ref message);
            if(message.Msg==0x5)UpdateCanvasBounds();
        }

        void AnimateFrame()
        {
            if (!Visible || WindowState == FormWindowState.Minimized || !MotionAllowed) { SnapMotion(); return; }
            long now = frameClock.ElapsedTicks;
            float delta = Math.Min(0.05f, (float)(now - lastFrame) / Stopwatch.Frequency); lastFrame = now;
            float step = 1 - (float)Math.Exp(-delta / 0.055f);
            themeMix += ((darkTheme ? 1 : 0) - themeMix) * step;
            powerMix += ((active ? 1 : 0) - powerMix) * step;
            hoverMix += ((powerHovered ? 1 : 0) - hoverMix) * step;
            selectionLeft += (rSeg[SelectedSegment].X - selectionLeft) * step;
            scrollMix += (providerScroll - scrollMix) * step;
            pulse = (float)(frameClock.Elapsed.TotalSeconds * 0.7 % 1);
            bool transitioning = MotionPending();
            if (!transitioning)
            {
                themeMix = darkTheme ? 1 : 0; powerMix = active ? 1 : 0; hoverMix = powerHovered ? 1 : 0;
                selectionLeft = rSeg[SelectedSegment].X; scrollMix = providerScroll;
            }
            anim.Interval = transitioning ? 33 : 80;
            if (transitioning) Invalidate(); else Invalidate(rToggleGlow());
            if (!busy && !transitioning) anim.Stop();
        }

        Bitmap MakeBackground(bool dark, int width, int height)
        {
            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var canvas = Graphics.FromImage(bitmap))
            {
                canvas.Clear(dark ? Color.FromArgb(8, 8, 13) : Color.FromArgb(250, 249, 255));
                canvas.ScaleTransform(width / (float)CanvasWidth, height / (float)CanvasHeight);
                Glow(canvas, new RectangleF(65, -265, 620, 600), Color.FromArgb(dark ? 82 : 30, 121, 80, 230));
                Glow(canvas, new RectangleF(-300, 500, 620, 600), Color.FromArgb(dark ? 38 : 22, 42, 99, 195));
            }
            return bitmap;
        }

        void DrawBackground(Graphics g)
        {
            int width, height;
            PhysicalSize(g, layoutWidth, layoutHeight, out width, out height);
            if (lightBackground != null && (lightBackground.Width != width || lightBackground.Height != height)) { lightBackground.Dispose(); lightBackground = null; }
            if (darkBackground != null && (darkBackground.Width != width || darkBackground.Height != height)) { darkBackground.Dispose(); darkBackground = null; }
            if (themeMix >= 0.998f) {
                if (darkBackground == null) darkBackground = MakeBackground(true, width, height);
                DrawPhysicalImage(g, darkBackground, PointF.Empty, 1); return;
            }
            if (lightBackground == null) lightBackground = MakeBackground(false, width, height);
            DrawPhysicalImage(g, lightBackground, PointF.Empty, 1);
            if (themeMix <= 0.002f) return;
            if (darkBackground == null) darkBackground = MakeBackground(true, width, height);
            DrawPhysicalImage(g, darkBackground, PointF.Empty, themeMix);
        }

        static void PhysicalSize(Graphics g, float width, float height, out int pixelsWide, out int pixelsHigh)
        {
            using (var transform = g.Transform) {
                var elements = transform.Elements;
                pixelsWide = Math.Max(1, (int)Math.Round(width * Math.Abs(elements[0])));
                pixelsHigh = Math.Max(1, (int)Math.Round(height * Math.Abs(elements[3])));
            }
        }

        static void DrawPhysicalImage(Graphics g, Bitmap image, PointF logicalOrigin, float opacity)
        {
            // The cache already matches device pixels. Do not resample it on every frame.
            var origin = new[] { logicalOrigin };
            using (var transform = g.Transform) transform.TransformPoints(origin);
            var bounds = new Rectangle((int)Math.Round(origin[0].X), (int)Math.Round(origin[0].Y), image.Width, image.Height);
            var state = g.Save();
            try {
                g.ResetTransform();
                if (opacity >= 0.998f) { g.DrawImageUnscaled(image, bounds.Location); return; }
                using (var attributes = new System.Drawing.Imaging.ImageAttributes()) {
                    var matrix = new System.Drawing.Imaging.ColorMatrix(); matrix.Matrix33 = opacity;
                    attributes.SetColorMatrix(matrix);
                    g.DrawImage(image, bounds, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                }
            }
            finally { g.Restore(state); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var state = g.Save();
            try
            {
                g.Clear(Bg);
                g.TranslateTransform(canvasOffset.X,canvasOffset.Y);
                g.ScaleTransform(uiScale, uiScale);
                PaintCanvas(g);
            }
            finally { g.Restore(state); }
        }

        void PaintCanvas(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int W = layoutWidth;

            DrawBackground(g);
            DrawThemeButton(g);
            DrawSettingsButton(g);

            // плашка сверху
            using (var f = UiFont(fBold, 11f))
            {
                string pill = "Smart DNS · " + Dns.Site[dnsSet];
                var sz = g.MeasureString(pill, f);
                var pr = new RectangleF(30, 24, Math.Min(sz.Width + 46,rSettings.Left-40), 34);
                rSite = Rectangle.Round(pr);
                using (var path = RoundPath(pr, 17))
                using (var b = new SolidBrush(Color.FromArgb(24, Purple)))
                using (var pen = new Pen(Color.FromArgb(100, Purple)))
                { g.FillPath(b, path); g.DrawPath(pen, path); }
                using (var b = new SolidBrush(Accent)) g.FillEllipse(b, pr.X + 16, pr.Y + 12, 10, 10);
                using (var b = new SolidBrush(Soft))
                using(var format=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap,LineAlignment=StringAlignment.Center})
                    g.DrawString(pill,f,b,new RectangleF(pr.X+32,pr.Y,pr.Width-40,pr.Height),format);
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
                using (var yb = new SolidBrush(Highlight)) g.FillPath(yb, path);
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
            if (!busy && autoGoal >= 0 && autoNeedsRetry && networkAvailable && !liveVpn.NeedsAttention)
                sub = AutoDns.Goals[autoGoal].Name + ": соединение не подтверждено · повторите Авто";
            if(!busy&&!active&&UseDefaultGoal&&networkAvailable&&!liveVpn.NeedsAttention)sub="Для подключения: "+AutoDns.Goals[defaultGoal].Name;
            using (var f = UiFont(fHead, busy ? 12f : 15f))
            using (var b = new SolidBrush((active || ((agStatus.Checked || agStatus.External) && agStatus.Error == null)) && !busy ? Accent : Ink))
                Centered(g, status, f, b, LowerY(420));
            using (var f = UiFont(fMed, 10.5f))
            using (var b = new SolidBrush(error != null ? (darkTheme ? Color.FromArgb(255, 151, 151) : Color.FromArgb(181, 59, 65)) : Muted))
                Centered(g, sub, f, b, LowerY(454), W - 60);

            // выбор набора адресов
            using (var path = RoundPath(ProviderBar, 16))
            using (var b = new SolidBrush(CardBg))
            using (var pen = new Pen(CardLine))
            { g.FillPath(b, path); g.DrawPath(pen, path); }
            var providerState = g.Save();
            g.SetClip(providerViewport);
            if(!rSeg[SelectedSegment].IsEmpty)
            using (var path = RoundPath(new RectangleF(selectionLeft - scrollMix + 5, LowerY(505), ProviderItemWidth-10, 46), 12))
            using (var b = new SolidBrush(Selection)) g.FillPath(b, path);
            for (int k = 0; k < rSeg.Length; k++)
            {
                if(rSeg[k].IsEmpty)continue;
                var sr = new RectangleF(rSeg[k].X - scrollMix + 5, rSeg[k].Y + 5, rSeg[k].Width - 10, rSeg[k].Height - 10);
                bool on = Math.Abs(rSeg[k].X - selectionLeft) < ProviderItemWidth/2f;
                string label = k == Dns.Name.Length ? "Авто · beta" : k == 3 ? "Xbox" : k == 2 ? "GeoHide EU" : Dns.Name[k];
                using (var f = UiFont(fBold, 9.5f))
                using (var b = new SolidBrush(on ? ContrastInk : Soft))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(label, f, b, sr, sf);
            }
            g.Restore(providerState);
            DrawProviderArrow(g,providerPrevious,-1,providerScroll>0);
            DrawProviderArrow(g,providerNext,1,providerScroll<VisibleSegments.Length*ProviderItemWidth-providerViewport.Width);

            // карточка с адресами
            var card = new RectangleF(28, LowerY(574), W - 56, 86);
            using (var path = RoundPath(card, 16))
            using (var b = new SolidBrush(CardBg))
            using (var pen = new Pen(CardLine))
            { g.FillPath(b, path); g.DrawPath(pen, path); }
            using (var fl = UiFont(fBold, 9.5f))
            using (var fv = UiFont(fMed, 10.5f))
            using (var bl = new SolidBrush(Purple))
            using (var bv = new SolidBrush(Ink))
            {
                g.DrawString("DNS", fl, bl, card.X + 20, card.Y + 10);
                g.DrawString(string.Join("  /  ", secureDns && EncryptedDns.Templates[dnsSet] != null ? EncryptedDns.ServersFor(dnsSet) : Dns.V4[dnsSet]), fv, bv, card.X + 76, card.Y + 8);
            }
            DrawServiceIcons(g, new RectangleF(card.X + 20, card.Y + 33, card.Width - 40, 23));
            using (var f = UiFont(fMed, 8f))
            using (var b = new SolidBrush(Accent)) g.DrawString(probing ? "Проверяем ответы DNS…" : probeLabels[dnsSet], f, b, 48, LowerY(636));
            using (var path = RoundPath(DnsTestButton, 8))
            using (var b = new SolidBrush(Selection)) g.FillPath(b, path);
            using (var f = UiFont(fBold, 8f))
            using (var b = new SolidBrush(ContrastInk))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("Тест DNS", f, b, DnsTestButton, sf);

            using (var path = RoundPath(rSecureOption, 16))
            using (var fill = new SolidBrush(CardBg))
            using (var line = new Pen(CardLine)) { g.FillPath(fill, path); g.DrawPath(line, path); }
            using (var path = RoundPath(new Rectangle(46, LowerY(696), 20, 20), 5))
            using (var fill = new SolidBrush(secureDns ? ActiveBg : Bg))
            using (var line = new Pen(Accent, 1.5f)) { g.FillPath(fill, path); g.DrawPath(line, path); }
            if (secureDns) using (var pen = new Pen(ContrastInk, 2f)) g.DrawLines(pen, new[] { new Point(50, LowerY(706)), new Point(55, LowerY(711)), new Point(63, LowerY(701)) });
            using (var title = UiFont(fBold, 10f))
            using (var body = UiFont(fMed, 8f))
            using (var ink = new SolidBrush(Ink))
            using (var muted = new SolidBrush(Muted)) {
                g.DrawString(EncryptedDns.Templates[dnsSet] == null ? "DoH недоступен" : "Зашифрованный DNS", title, ink, 76, LowerY(686));
                g.DrawString(EncryptedDns.Templates[dnsSet] == null ? "У этого провайдера настроен только обычный DNS" : secureDns && (agStatus.External || agStatus.Checked) ? "DoH · Antigravity по отдельным правилам" : "DoH адаптера · IPv6 приостанавливается", body, muted, 76, LowerY(710));
            }
            using (var path = RoundPath(rAgOption, 16))
            using (var fill = new SolidBrush(CardBg))
            using (var line = new Pen(CardLine))
            { g.FillPath(fill, path); g.DrawPath(line, path); }
            var check = new Rectangle(46, LowerY(771), 20, 20);
            using (var path = RoundPath(check, 5))
            using (var fill = new SolidBrush(agStatus.Checked ? ActiveBg : Bg))
            using (var line = new Pen(Accent, 1.5f))
            { g.FillPath(fill, path); g.DrawPath(line, path); }
            if (agStatus.Checked)
                using (var pen = new Pen(ContrastInk, 2f))
                    g.DrawLines(pen, new[] { new Point(50, LowerY(781)), new Point(55, LowerY(786)), new Point(63, LowerY(776)) });
            using (var title = UiFont(fBold, 10f))
            using (var body = UiFont(fMed, 8.5f))
            using (var ink = new SolidBrush(Ink))
            using (var muted = new SolidBrush(Muted))
            {
                g.DrawString("Доп. DNS Antigravity", title, ink, 76, LowerY(764));
                string note = agLoading ? "Проверяем настройки…" : agStatus.Error != null ? "Не удалось проверить · нажмите для повтора" :
                    agStatus.RecoveryNeeded ? "Снимите галочку: завершить восстановление" : agStatus.External ? "Есть правила другой программы" : agStatus.Checked ?
                    (agStatus.Complete ? (agStatus.Provider == 1 ? "Comss: смените DNS" : "Включено · " + (agStatus.Provider >= 0 ? AntigravityDns.ProviderNames[agStatus.Provider] : "DNS")) +
                        (agStatus.Provider != AntigravityDns.ProviderForMainSet(dnsSet) ? " · смена после отключения" : "") : "Включено частично · снимите для отмены") :
                    (cleanupDone ? "Старые правила удалены" : !Dns.IsAvailable(dnsSet) ? "Провайдер отключён · выберите другой DNS" : secureDns ? "С шифрованием — через общий DoH" : dnsSet == 0 ? "Comss для этой опции отключён" : dnsSet == 3 ? "Выберите DNS для нейросетей выше" : "Будет использован: " + Dns.Name[dnsSet]);
                bool showCleanup = agStatus.External;
                using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString(string.IsNullOrEmpty(note)?"Может помочь при ошибке 400 в Antigravity":note, body, muted, new RectangleF(76, LowerY(788), showCleanup ? W-242 : W-120, 22), format);
            }
            if (agStatus.External)
            {
                using (var path = RoundPath(rAgCleanup, 8))
                using (var fill = new SolidBrush(Selection)) g.FillPath(fill, path);
                using (var f = UiFont(fBold, 8f))
                using (var b = new SolidBrush(ContrastInk))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("Удалить старые", f, b, rAgCleanup, format);
            }

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
                    g.DrawString("Код ", fb, dark, new PointF(28, LowerY(830)), format);
                    g.DrawString("Всего", fb, bb, new PointF(28 + prefix, LowerY(830)), format);
                }
                rBrand = new Rectangle(28, LowerY(828), (int)Math.Ceiling(g.MeasureString(brand, fb).Width), 30);
            }
            using (var f = UiFont(fMed, 9.5f))
            using (var b = new SolidBrush(Muted))
            {
                var t = "t.me/TotalC0de";
                var sz = g.MeasureString(t, f);
                rGroup = new Rectangle((int)(W - 28 - sz.Width), LowerY(835), (int)sz.Width, (int)sz.Height);
                g.DrawString(t, f, b, rGroup.X, rGroup.Y);
            }
            using (var f = UiFont(fMed, 8.5f))
            using (var b = new SolidBrush(Muted))
            using (var format = new StringFormat {Alignment=StringAlignment.Center, LineAlignment=StringAlignment.Center, FormatFlags=StringFormatFlags.NoWrap})
            {
                int left=rBrand.Right+4,width=Math.Max(0,rGroup.Left-rBrand.Right-8);
                rVersion=new Rectangle(left,LowerY(830),width,25);
                var versionBounds=rVersion;
                rUpdateIndicator=Rectangle.Empty;
                if(availableUpdate!=null&&width>=24) {
                    int groupWidth=Math.Min(width,(int)Math.Ceiling(g.MeasureString(VersionLabel,f).Width)+16);
                    int groupLeft=left+(width-groupWidth)/2;
                    versionBounds=new Rectangle(groupLeft,rVersion.Top,groupWidth-16,rVersion.Height);
                    rUpdateIndicator=new Rectangle(versionBounds.Right+5,rVersion.Top+(rVersion.Height-8)/2,8,8);
                    using(var halo=new SolidBrush(Color.FromArgb(35,Accent)))
                        g.FillEllipse(halo,rUpdateIndicator.X-3,rUpdateIndicator.Y-3,14,14);
                    using(var fill=new SolidBrush(Accent))g.FillEllipse(fill,rUpdateIndicator);
                }
                g.DrawString(VersionLabel,f,b,versionBounds,format);
            }
        }

        void DrawToggle(Graphics g)
        {
            var r = new RectangleF(rToggle.X, rToggle.Y, rToggle.Width, rToggle.Height);
            r.Inflate(hoverMix * 3, hoverMix * 3);
            if (powerMix > 0.005f || busy)
            {
                var gr = new RectangleF(rToggle.X - 40, rToggle.Y - 40, rToggle.Width + 80, rToggle.Height + 80);
                int width, height;
                PhysicalSize(g, gr.Width, gr.Height, out width, out height);
                if (toggleGlow != null && (toggleGlow.Width != width || toggleGlow.Height != height)) { toggleGlow.Dispose(); toggleGlow = null; }
                if (toggleGlow == null)
                {
                    toggleGlow = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using (var canvas = Graphics.FromImage(toggleGlow))
                        Glow(canvas, new RectangleF(0, 0, width, height), Color.FromArgb(255, Cyan));
                }
                DrawPhysicalImage(g, toggleGlow, gr.Location, (busy ? 30 : 38 * powerMix) / 255f);
            }
            // кольцо
            using (var b = new SolidBrush(Mix(CardBg, ActiveBg, powerMix))) g.FillEllipse(b, r);
            using (var pen = new Pen(Mix(CardLine, Cyan, Math.Max(powerMix, hoverMix)), 2f)) g.DrawEllipse(pen, r);
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
            using (var pen = new Pen(Mix(Accent, ContrastInk, powerMix), 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, c.X - 32, c.Y - 32, 64, 64, -60, 300);
                g.DrawLine(pen, c.X, c.Y - 40, c.X, c.Y - 6);
            }
            bool separateOnly = !active && AntigravityOnlyLabel != null;
            using (var f = UiFont(fHead, separateOnly ? 9f : 13f))
            using (var b = new SolidBrush(Mix(Soft, ContrastInk, powerMix)))
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
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            {
                float w = maxW > 0 ? maxW : layoutWidth;
                g.DrawString(s, f, b, new RectangleF((layoutWidth - w) / 2, y, w, 40), sf);
            }
        }

        void OnDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            e = new MouseEventArgs(e.Button, e.Clicks, CanvasPoint(e.Location).X, CanvasPoint(e.Location).Y, e.Delta);
            if (rClose.Contains(e.Location)) { Close(); return; }
            if (rTheme.Contains(e.Location)) { SetTheme(!darkTheme, true); return; }
            if (rSettings.Contains(e.Location)) { ShowSettings(); return; }
            if (rVersion.Contains(e.Location)) { ShowUpdates(); return; }
            if (initializing) return;
            if (rSecureOption.Contains(e.Location)) {
                if (EncryptedDns.Templates[dnsSet] == null) return;
                if (busy) return;
                if (Dns.EncryptedOnly(dnsSet)) { ShowMessage("DNS-AI работает только через зашифрованный DNS. Шифрование для него обязательно.", "DNS-AI", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!secureDns && EncryptedDns.Templates[dnsSet] == null) { ShowMessage("Выберите Comss.one, Bezmezhau, DNS-AI или MagicDNS. Для остальных провайдеров параметры системного DoH пока не подтверждены.", "Зашифрованный DNS", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (active) Apply(true, null, false, !secureDns); else { secureDns = !secureDns; Invalidate(); }
                return;
            }
            if (providerPrevious.Contains(e.Location)) { ScrollProviderPage(-1); return; }
            if (providerNext.Contains(e.Location)) { ScrollProviderPage(1); return; }
            if (DnsTestButton.Contains(e.Location)) { ShowDnsDiagnostics(); return; }
            if (rAgCleanup.Contains(e.Location) && agStatus.External) {
                if (busy || agLoading) return;
                RemoveOldAntigravity();
                return;
            }
            if (rAgOption.Contains(e.Location)) { ToggleAntigravity(); return; }
            if (rGroup.Contains(e.Location)) { Open("https://t.me/TotalC0de"); return; }
            if (rSite.Contains(e.Location)) { Open(Dns.Url[dnsSet]); return; }
            if (rBrand.Contains(e.Location)) { Open("https://t.me/TotalC0de"); return; }
            var c = new Point(rToggle.X + rToggle.Width / 2, rToggle.Y + rToggle.Height / 2);
            double dx = e.X - c.X, dy = e.Y - c.Y;
            if (dx * dx + dy * dy <= rToggle.Width * rToggle.Width / 4.0) { Toggle(); return; }
            for (int k = 0; k < rSeg.Length; k++)
                if (providerViewport.Contains(e.Location) && rSeg[k].Contains(new Point(e.X + (int)Math.Round(scrollMix), e.Y))) { if(k == Dns.Name.Length) ShowAutoDns(); else PickSet(k); return; }
            dragFrom = e.Location;
        }

        void OnMove(object s, MouseEventArgs e)
        {
            e = new MouseEventArgs(e.Button, e.Clicks, CanvasPoint(e.Location).X, CanvasPoint(e.Location).Y, e.Delta);
            var c = new Point(rToggle.X + rToggle.Width / 2, rToggle.Y + rToggle.Height / 2);
            double dx = e.X - c.X, dy = e.Y - c.Y;
            bool overPower = dx * dx + dy * dy <= rToggle.Width * rToggle.Width / 4.0;
            if (powerHovered != overPower) { powerHovered = overPower; Invalidate(rToggleGlow()); }
            bool overTheme=rTheme.Contains(e.Location);if(themeHovered!=overTheme){themeHovered=overTheme;Invalidate();}
            bool hand = rSettings.Contains(e.Location) || rVersion.Contains(e.Location) || (rSecureOption.Contains(e.Location) && EncryptedDns.Templates[dnsSet] != null) || rAgOption.Contains(e.Location) || rTheme.Contains(e.Location) || rClose.Contains(e.Location) || rGroup.Contains(e.Location) || rSite.Contains(e.Location) || rBrand.Contains(e.Location) || ProviderBar.Contains(e.Location) || DnsTestButton.Contains(e.Location)
                        || dx * dx + dy * dy <= rToggle.Width * rToggle.Width / 4.0;
            Cursor = hand && !busy ? Cursors.Hand : Cursors.Default;
            string tip = rSettings.Contains(e.Location)?"Настройки":providerPrevious.Contains(e.Location)?"Предыдущие 3 варианта":providerNext.Contains(e.Location)?"Следующие 3 варианта":rVersion.Contains(e.Location)?(availableUpdate==null?"Проверить обновления":"Доступна новая версия "+availableUpdate.Version):rTheme.Contains(e.Location) ? (darkTheme ? "Светлая тема" : "Тёмная тема") : null;
            if (themeTip.GetToolTip(this) != tip) themeTip.SetToolTip(this, tip);
            if (e.Button == MouseButtons.Left && !hand)
                Location = new Point(Location.X + (int)Math.Round((e.X - dragFrom.X) * uiScale),
                    Location.Y + (int)Math.Round((e.Y - dragFrom.Y) * uiScale));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (powerHovered) { powerHovered = false; Invalidate(rToggleGlow()); }
            if(themeHovered){themeHovered=false;Invalidate();}
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
        void DrawProviderArrow(Graphics g,Rectangle bounds,int direction,bool enabled)
        {
            var button=bounds;button.Inflate(-3,-6);
            using(var path=RoundPath(button,9))
            using(var fill=new SolidBrush(enabled?CardLine:CardBg))g.FillPath(fill,path);
            float x=bounds.Left+bounds.Width/2f,y=bounds.Top+bounds.Height/2f;
            using(var pen=new Pen(enabled?Accent:Muted,2f)) {
                pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;
                g.DrawLines(pen,new[]{new PointF(x-direction*3,y-6),new PointF(x+direction*3,y),new PointF(x-direction*3,y+6)});
            }
        }
        internal void ScrollProviderPage(int direction)
        {ScrollProviders(Math.Sign(direction)*(providerViewport.Width/ProviderItemWidth)*ProviderItemWidth);}
        void ScrollProviders(int delta)
        {
            providerScroll = Math.Max(0, Math.Min(VisibleSegments.Length * ProviderItemWidth - providerViewport.Width, providerScroll + delta));
            Invalidate();
        }
        void EnsureProviderVisible()
        {
            if(rSeg[SelectedSegment].IsEmpty)return;
            if (rSeg[SelectedSegment].Left - providerScroll < providerViewport.Left)
                ScrollProviders(rSeg[SelectedSegment].Left - providerScroll - providerViewport.Left);
            else if (rSeg[SelectedSegment].Right - providerScroll > providerViewport.Right)
                ScrollProviders(rSeg[SelectedSegment].Right - providerScroll - providerViewport.Right);
        }
        void CheckDnsServers()
        {
            if (probing) return;
            if (!Dns.IsAvailable(dnsSet)) { probeLabels[dnsSet] = "Провайдер отключён · выберите другой DNS"; Invalidate(); return; }
            if (secureDns || Dns.EncryptedOnly(dnsSet)) { probeLabels = Enumerable.Repeat("Режим DoH · тест через HTTPS", Dns.Name.Length).ToArray(); Invalidate(); return; }
            if (!networkAvailable) { probeLabels = Enumerable.Repeat("Нет сетевого подключения", Dns.Name.Length).ToArray(); Invalidate(); return; }
            if (liveVpn.NeedsAttention)
            {
                probeLabels = Enumerable.Repeat("Для теста отключите VPN", Dns.Name.Length).ToArray();
                Invalidate(); return;
            }
            probing = true; Invalidate();
            int provider = dnsSet, revision = operationRevision;
            ThreadPool.QueueUserWorkItem(_ => {
                DnsProbeResult[] results = null;
                string failure = null;
                try { results = DnsProbe.CheckAvailability(provider); }
                catch (Exception ex) { failure = ex.Message; }
                var vpn = VpnCheck.Scan();
                if (IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => {
                    probing = false;
                    if (!AcceptNetworkSnapshot(revision)) return;
                    UpdateVpnStatus(vpn);
                    if (vpn.NeedsAttention) { probing = false; probeLabels = Enumerable.Repeat("Для теста отключите VPN", Dns.Name.Length).ToArray(); Invalidate(); return; }
                    if (failure != null) probeLabels[provider] = "Не удалось проверить DNS · повторите тест";
                    else
                    {
                        probeResults[provider] = results;
                        int count = results.Count(r => r.Host == "example.com" && r.Ok);
                        probeLabels[provider] = Dns.EncryptedOnly(provider) ? (count > 0 ? "DoH отвечает через HTTPS" : "DoH не ответил") : "Ответы DNS: " + count + "/"+Dns.V4[provider].Length;
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
                var choices=Dns.AvailableProviders;int selected=Array.IndexOf(choices,dnsSet);
                dialog.AddDnsTestControls(choices.Select(p=>Dns.Name[p]).ToArray(),Math.Max(0,selected), index => RunDnsTest(dialog, choices[index]));
                dialog.ShowDialog(this);
            }
        }
        void ShowAutoDns(bool startImmediately=false)
        {
            if (busy || initializing) return;
            AutoDnsResult chosen = null; int chosenGoal = -1; bool running = false;
            using (var cancellation = new CancellationTokenSource())
            using (var dialog = CreateDialog("Выберите сервис и нажмите «Подобрать DNS».",
                "Авто-подключение · beta", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, uiScale))
            {
                var token = cancellation.Token;
                dialog.SetPrimaryText("Закрыть");
                dialog.FormClosed += (s, e) => cancellation.Cancel();
                dialog.AddAutoDnsControls(AutoDns.Goals.Select(g => g.Name).ToArray(), startImmediately&&UseDefaultGoal?defaultGoal:autoGoal >= 0 ? autoGoal : defaultGoal>=0?defaultGoal:0, goal => {
                    if (running) return;
                    DohHttp2.ResetAddressHealth();
                    chosen = null; chosenGoal = -1;
                    dialog.SetPrimaryText("Отмена");
                    running = true;
                    bool encrypted = secureDns;
                    dialog.ShowAutoDnsStatus("Проверяем сеть и подходящие DNS…", "Подбираем для " + AutoDns.Goals[goal].Name, true, false);
                    ThreadPool.QueueUserWorkItem(_ => {
                        AutoDnsResult[] results = null; string failure = null, failureDetail=null;
                        try {
                            if (!NetworkCheck.HasConnection()) throw new IOException(NetworkCheck.OfflineMessage);
                            if (VpnCheck.Scan().NeedsAttention) throw new IOException("Отключите VPN и повторите подбор.");
                            if (AutoDns.Candidates(goal, encrypted).Length == 0) throw new IOException("Для этой цели нет DNS с выбранным шифрованием. Снимите галочку DoH для обычного DNS или выберите другую цель.");
                            int completed=0,total=AutoDns.Candidates(goal,encrypted).Length;
                            results = AutoDns.Search(goal, encrypted, new AutoDnsProbe(), token, result => {
                                int done=Interlocked.Increment(ref completed);
                                if(token.IsCancellationRequested||dialog.IsDisposed||IsDisposed)return;
                                try {BeginInvoke((Action)(()=> {
                                    if(!token.IsCancellationRequested&&!dialog.IsDisposed&&running)
                                        dialog.ShowAutoDnsStatus("Проверено "+done+" из "+total+" вариантов.\nНастройки пока не меняем.","Подбираем DNS",true,false);
                                }));}catch(InvalidOperationException){}
                            });
                            token.ThrowIfCancellationRequested();
                            if (!NetworkCheck.HasConnection()) throw new IOException(NetworkCheck.OfflineMessage);
                            if (VpnCheck.Scan().NeedsAttention) throw new IOException("Во время проверки обнаружен VPN. Отключите VPN и повторите подбор.");
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex) { failure = ex.Message; failureDetail=ex.ToString(); }
                        if (token.IsCancellationRequested || dialog.IsDisposed || IsDisposed) return;
                        try { BeginInvoke((Action)(() => {
                            if (token.IsCancellationRequested || dialog.IsDisposed) return;
                            running = false;
                            if (failure != null) {
                                string hint=failure.Contains("VPN")?"Отключите VPN и повторите подбор.":failure==NetworkCheck.OfflineMessage?"Подключитесь к Wi-Fi или Ethernet.":"Повторите подбор или выберите DNS вручную.";
                                dialog.ShowAutoDnsResult("Не удалось завершить подбор",hint,failure);
                                dialog.SetDiagnosticDetails("Goal="+AutoDns.Goals[goal].Name+"; DoH="+encrypted+"\n"+failureDetail+(results==null?"":"\n"+AutoDns.Diagnostic(goal,results)),DiagnosticReport.ShouldOffer("Авто",failure));return;
                            }
                            var best = AutoDns.Best(results);
                            string report = AutoDns.Report(goal, results);
                            if (best == null) { dialog.ShowAutoDnsResult("Нет подходящего варианта","Попробуйте позже или выберите DNS вручную.\nНастройки не изменены.",report); dialog.SetDiagnosticDetails(AutoDns.Diagnostic(goal,results),true); return; }
                            dialog.ShowAutoDnsResult(Dns.Name[best.Provider]+" для "+AutoDns.Goals[goal].Name,
                                best.NeedsBrowser?"":"Сервер доступен.\nМожно подключить выбранный DNS.",
                                report,"Подключить "+Dns.Name[best.Provider]);
                            dialog.SetDiagnosticDetails(AutoDns.Diagnostic(goal,results),results.Any(r=>!r.Ok));
                            // One review of the concrete choice before the existing elevation/apply flow.
                            chosen = best; chosenGoal = goal;
                            if(startImmediately){dialog.DialogResult=DialogResult.OK;dialog.Close();}
                        })); } catch (InvalidOperationException) { }
                    });
                }, () => {
                    chosen = null; chosenGoal = -1;
                    dialog.ShowAutoDnsStatus("Нажмите «Подобрать DNS».\nПроверим подходящие варианты.", "Найдём подходящий DNS", false, false);
                });
                if(startImmediately)dialog.Shown+=(s,e)=>dialog.BeginInvoke((Action)(()=>dialog.StartAutoDnsSearch()));
                if (dialog.ShowDialog(this) != DialogResult.OK || chosen == null || running) return;
            }
            Apply(true, chosen.Provider, false, chosen.Encrypted, chosenGoal);
        }

        void RunDnsTest(StyledDialog dialog, int provider)
        {
            bool encrypted = secureDns || Dns.EncryptedOnly(provider);
            if (encrypted && EncryptedDns.Templates[provider] == null) { dialog.ShowDnsTestStatus("Для этого провайдера HTTPS-тест пока недоступен. Выберите Comss.one, Bezmezhau, DNS-AI или MagicDNS. Обычные DNS-запросы не отправлялись.", "Нет параметров DoH", false, true); return; }
            if (!NetworkCheck.HasConnection()) { dialog.ShowDnsTestStatus(NetworkCheck.OfflineMessage, "Нет подключения", false, true); return; }
            var vpn = VpnCheck.Scan();
            if (vpn.NeedsAttention)
            {
                dialog.ShowDnsTestStatus("Отключите VPN и нажмите «Проверить» ещё раз. VPN может перехватывать запросы к любому серверу.\n\n" + vpn.Details,
                    "Сначала отключите VPN", false, true); return;
            }
            dialog.ShowDnsTestStatus("Проверяем серверы " + Dns.Name[provider] + " и домены сервисов…", "Проверяем " + Dns.Name[provider], true, false);
            DohHttp2.ResetAddressHealth();
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
                    probeLabels[provider] = encrypted ? "DoH: " + results.Count(r => r.Ok) + "/" + results.Length + " ответов" : "Ответы DNS: " + count + "/"+Dns.V4[provider].Length;
                    string report = encrypted ? "HTTPS: " + EncryptedDns.Templates[provider] + "\n\n" + string.Join("\n", results.Select(r => r.Host + " — " + (r.Ok ? r.Milliseconds + " мс" : r.Error))) + "\n\nЭто проверка сервера через HTTPS, не DNS браузера. Системные настройки не изменялись." : DnsProbe.Report(provider, results);
                    dialog.ShowDnsTestStatus(report, (encrypted ? "HTTPS-ответы: " : "Прямые ответы: ") + results.Count(r => r.Ok) + "/" + results.Length, false, results.Any(r => !r.Ok), (results.Any(r=>!r.Ok)?"Часть запросов завершилась с ошибкой.":"Серверы ответили на все запросы.")+"\nНастройки DNS не изменялись. Ответ DNS не подтверждает доступ к сервису.");
                    dialog.SetDiagnosticDetails(string.Join("\n\n",results.Select(r=>r.Server+"; host="+r.Host+"; elapsed="+r.Milliseconds+" ms; error="+r.Error+"\n"+r.Detail)),results.Any(r=>!r.Ok));
                    Invalidate();
                })); } catch (InvalidOperationException) { }
            });
        }
        void PickSet(int k)
        {
            if(!Dns.IsAvailable(k))return;
            if (busy || (k == dnsSet && autoGoal < 0&&!UseDefaultGoal)) return;
            manualSelection=true;
            if (active) Apply(true, k, encrypted: EncryptedDns.Templates[k] != null && secureDns || Dns.EncryptedOnly(k));
            else { autoGoal = -1; dnsSet = k; if (Dns.EncryptedOnly(k)) secureDns = true; else if (EncryptedDns.Templates[k] == null) secureDns = false; EnsureProviderVisible(); Invalidate(); }
        }

        void Toggle()
        {
            if (busy) return;
            if (!active && (autoGoal >= 0||UseDefaultGoal)) { ShowAutoDns(true); return; }
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
            operationRevision++; busy = true; agLoading = true; Invalidate();
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
            if (enable && secureDns) {
                if (agStatus.External) RemoveOldAntigravity();
                else ShowMessage(active ? "Antigravity использует общий DoH. Отдельная галочка DNS не нужна." : "Для шифрованного подключения Antigravity включите DNS большой кнопкой. Будет использован выбранный DoH без отдельных DNS-правил.", "Режим DoH", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int provider = AntigravityDns.ProviderForMainSet(dnsSet);
            if (enable && agStatus.Error == null && provider < 0)
            {
                ShowMessage(Dns.EncryptedOnly(dnsSet) ? "DNS-AI используется через общий DoH. Включите его большой кнопкой. Отдельные DNS-правила Antigravity для него не создаются." : dnsSet == 0 ? "Comss для доп. DNS Antigravity пока отключён. Выберите другой DNS выше. Общий DNS Comss остаётся доступен." : "Для Antigravity выберите выше MagicDNS или Bezmezhau. Xbox DNS предназначен для Xbox.",
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
            operationRevision++; busy = true; agLoading = true; Invalidate();
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

        void Apply(bool turnOn, int? selectedSet = null, bool resetKnown = false, bool? encrypted = null, int? autoTarget = null)
        {
            int requestedSet = selectedSet ?? dnsSet;
            if(turnOn&&!Dns.IsAvailable(requestedSet)){ShowMessage("Этот провайдер временно скрыт. Выберите другой DNS.","Провайдер недоступен",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            bool useEncrypted = (encrypted ?? secureDns) || Dns.EncryptedOnly(requestedSet);
            if (turnOn && useEncrypted && EncryptedDns.Templates[requestedSet] == null) { ShowMessage("Для DoH выберите Comss.one, Bezmezhau, DNS-AI или MagicDNS либо снимите галочку шифрования.", "DoH недоступен", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
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
            operationRevision++; busy = true; error = null; Invalidate();
            DiagnosticReport.Record("Общий DNS", "enable=" + turnOn + "; provider=" + Dns.Name[requestedSet] + "; DoH=" + useEncrypted);
            int set = selectedSet ?? dnsSet;
            dnsSet = set;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;string networkDiagnostic=null;
                try {
                    DnsProbeResult encryptedProbe=null;
                    if(turnOn&&useEncrypted&&NetworkCheck.HasConnection())encryptedProbe=DnsProbe.CheckHttps(EncryptedDns.Templates[set],"example.com");
                    if (turnOn && !NetworkCheck.HasConnection()) err = NetworkCheck.OfflineMessage;
                    else if (turnOn && useEncrypted && encryptedProbe!=null && !encryptedProbe.Ok) {
                        err = "Сервер не ответил через HTTPS. Обычный DNS не использовался, настройки не изменены.";
                        networkDiagnostic="DoH preflight; provider="+Dns.Name[set]+"; endpoint="+encryptedProbe.Server+"; host="+encryptedProbe.Host+"; elapsed="+encryptedProbe.Milliseconds+" ms\n"+encryptedProbe.Detail;
                    }
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
                    if (turnOn && err == null) { autoGoal = autoTarget ?? -1; autoNeedsRetry = false; lastAutoCheck = frameClock.ElapsedMilliseconds; }
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
                        if(networkDiagnostic!=null)using(var dialog=CreateDialog(err,"Ошибка DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button1)) {
                            dialog.SetDiagnosticDetails(networkDiagnostic,true);dialog.ShowDialog(this);
                        }
                        else ShowMessage( err, "Ошибка DNS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            if (args.Length == 1 && args[0] == "--install-update") return AppUpdate.Install();
            if (args.Length > 0 && args[0] == "--prepare-uninstall") return UninstallCleanup.Handle(args);
            if (args.Length != 0) return DnsElevation.Handle(args, Dns.ExecuteDirect);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return SingleInstance.Run(()=>new MainForm());
        }
    }
}
