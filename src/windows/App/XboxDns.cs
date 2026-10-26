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
        public static string[] Name {get{return RuntimeProviders.Snapshot.Names;}}
        public static string[][] V4 {get{return RuntimeProviders.Snapshot.V4;}}
        public static string[][] V6 {get{return RuntimeProviders.Snapshot.V6;}}
        public static string[] Opens {get{return RuntimeProviders.Snapshot.Descriptions;}}
        public static string[] Site {get{return RuntimeProviders.Snapshot.Sites;}}
        public static string[] Url {get{return RuntimeProviders.Snapshot.Urls;}}
        // Keep historical IDs for restore; availability is shared with Android in config/providers.json.
        internal static bool IsAvailable(int provider) { return provider>=0 && provider<Name.Length && (CustomDns.IsCustom(provider)?RuntimeProviders.All[provider].enabled:ProviderAvailability.IsEnabled(provider)); }
        internal static int[] AvailableProviders { get { return Enumerable.Range(0,Name.Length).Where(IsAvailable).ToArray(); } }
        internal static bool EncryptedOnly(int provider) { return provider>=0&&provider<RuntimeProviders.All.Length&&RuntimeProviders.All[provider].encryptedOnly; }
        // Старые адреса xbox-dns.ru из версии 1.0: если стоят они, показываем Xbox DNS
        static readonly string[] XboxOld = { "111.88.96.50", "111.88.96.51" };

        // -1 — DNS сервиса не стоит, иначе номер набора (IPv4 определяет набор)
        public static int ActiveSet()
        {return ActiveSetFor(-1);}
        internal static int ActiveSetFor(int preferred)
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
                int match = MatchConfiguration(configuration,preferred);
                if (match >= 0 && EncryptedOnly(match) && !EncryptedDns.ExplicitlyConfigured(configuration, match)) return -1;
                if (match < 0 || (found >= 0 && match != found)) return -1;
                found = match;
            }
            return found;
        }
        internal static int MatchConfiguration(FamilyBackup state,int preferred)
        {
            if(state.DohCaptured&&state.Doh!=null&&state.Doh.Any(d=>d.Flags!=0)){
                if(IsAvailable(preferred)&&EncryptedDns.ExplicitlyConfigured(state,preferred))return preferred;
                for(int id=0;id<Name.Length;id++)if(EncryptedDns.ExplicitlyConfigured(state,id))return id;
            }
            if(IsAvailable(preferred)&&!EncryptedOnly(preferred)&&state.Servers.SequenceEqual(V4[preferred]))return preferred;
            return MatchProvider(state.Servers);
        }
        internal static int MatchProvider(string[] addresses)
        {
            if(addresses==null||addresses.Length==0)return -1;
            if (addresses.SequenceEqual(XboxOld)) return 3;
            int plain = Array.FindIndex(V4, servers => addresses.SequenceEqual(servers));
            if (plain >= 0) return plain;
            for (int k = 0; k < V4.Length; k++)
                if (EncryptedDns.Templates[k] != null && (addresses.SequenceEqual(EncryptedDns.ServersFor(k)) || addresses.SequenceEqual(DotDns.Servers(k)))) return k;
            for (int k = 0; k < V4.Length; k++)
                if (RuntimeProviders.All[k].legacyIpv4.Any(old => addresses.SequenceEqual(old))) return k;
            return -1;
        }
        internal static string[] KnownServers { get { return V4.SelectMany(s=>s).Concat(V6.SelectMany(s=>s)).Concat(XboxOld)
            .Concat(RuntimeProviders.All.SelectMany(p=>p.windowsDotServers)).Concat(RuntimeProviders.All.SelectMany(p=>p.windowsDohServers)).Concat(RuntimeProviders.All.SelectMany(p=>p.legacyIpv4).SelectMany(s=>s)).Distinct().ToArray(); } }

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
        internal static bool NeedsKnownReset() { return new DnsSettingsEngine(new NativeDnsSystem(), new RegistryDnsBackupStore()).NeedsKnownReset(KnownServers); }

        internal static string ExecuteDirect(bool enable, int set)
        {
            if(enable&&set>=0&&set<V4.Length&&!IsAvailable(set))return "Провайдер временно недоступен. Выберите другой DNS.";
            if (enable && !NetworkCheck.HasConnection()) return NetworkCheck.OfflineMessage;
            if (enable && VpnCheck.Scan().NeedsAttention) return "Отключите VPN и повторите подключение. DNS не изменён.";
            if (enable && set < V4.Length && EncryptedOnly(set)) return EncryptedDns.Run(set);
            if (enable && set >= 500 && set < 500 + V4.Length) return EncryptedDns.Run(set - 500, true, true);
            if (enable && set >= 400 && set < 400 + V4.Length) return EncryptedDns.Run(set - 400, false, true);
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
                    state.Doh.All(EncryptedDns.IsStrict) && !system.ReadIpv6Binding(a.Id);
            });
        }

        internal static bool TlsConfigured() {
            var system = new NativeDnsSystem(); var adapters = system.Adapters(true).Where(a=>a.V4).ToArray();
            return adapters.Length>0 && adapters.All(a=> { var state=system.Read(a.Id,"IPv4"); return state.DohCaptured && state.Doh.Length>0 && state.Doh.All(d=>d.Kind==2 && EncryptedDns.IsStrict(d)); });
        }
    }

    partial class MainForm : Form
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
        static readonly Color ActiveBg = Cyan, Amber = Color.FromArgb(244, 185, 77);
        Color WarningColor { get { return darkTheme ? Amber : Color.FromArgb(186, 118, 12); } }
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
        readonly ITunnelSession googleTunnel;readonly bool profilePreferences;bool googleMode,googleClosing,googleChecking;CancellationTokenSource googleCheck;
        readonly ITunnelSession localSession;bool localMode;
        int[] hiddenProviders=new int[0];
        internal int[] UiProviders {get{return Dns.AvailableProviders.Where(p=>!hiddenProviders.Contains(p)).ToArray();}}
        internal void SetProviderVisibility(int[] hidden)
        {
            var ids=ProviderPreferences.Normalize(hidden);if(Dns.AvailableProviders.All(ids.Contains))throw new InvalidOperationException("Оставьте видимым хотя бы один DNS-провайдер.");
            hiddenProviders=ids;
            if(!active&&!ManagedDns.Running&&hiddenProviders.Contains(dnsSet)){dnsSet=UiProviders.First();if(!DotDns.Available(dnsSet))tlsDns=false;if(Dns.EncryptedOnly(dnsSet))secureDns=true;}
            EnsureProviderVisible();Invalidate();
        }
        bool localAntigravity,localAntigravityTls,pendingAntigravityTls;int localAntigravityProvider=5,pendingAntigravityProvider=-1;
        bool HasLegacyAntigravityState {get{return !ManagedMode&&(agStatus.Checked||agStatus.External||agStatus.RecoveryNeeded||agStatus.Error!=null);}}
        internal bool LocalAntigravity {get{return localAntigravity;}}
        internal int LocalAntigravityProvider {get{return localAntigravityProvider;}}
        internal Rectangle LocalAntigravityProviderButton {get{return new Rectangle(layoutWidth-160,LowerY(761),114,24);}}
        bool ManagedMode {get{return googleMode||localMode;}}
        ITunnelSession ManagedDns {get{return localMode?localSession:googleTunnel;}}
        internal bool LocalMode {get{return localMode;}}
        CancellationTokenSource resolverCancel;int resolverRevision,resolverProvider=-1;DnsTransport resolverTransport;bool resolverGoogle,resolverLocal,resolverChecking,autoResolverCheck=true;
        ResolverCheckResult resolverResult;string resolverStamp;
        internal bool ResolverResultCurrent { get { return active&&!busy&&resolverProvider==dnsSet&&resolverTransport==Transport&&resolverGoogle==googleMode&&resolverLocal==localMode; } }
        internal Rectangle CheckerStatusBounds {get{return new Rectangle(30,LowerY(476),layoutWidth-60,18);}}
        internal bool CheckerConfirmed {get{return !initializing&&ResolverResultCurrent&&!resolverChecking&&resolverResult!=null&&resolverResult.Official!=null&&resolverResult.Official.State==ProviderCheckState.Confirmed;}}
        internal bool CheckerRejected {get{return !initializing&&ResolverResultCurrent&&!resolverChecking&&resolverResult!=null&&resolverResult.Official!=null&&resolverResult.Official.State==ProviderCheckState.NotConfirmed;}}
        internal bool CheckerWarning {get{return active&&!busy&&!initializing&&ResolverResultCurrent&&!resolverChecking&&resolverResult!=null&&(CheckerRejected||(!CheckerConfirmed&&!(resolverResult.Official!=null&&resolverResult.Official.State==ProviderCheckState.ProbePassed)&&!(resolverResult.Official==null&&resolverResult.Comparison==ResolverComparison.Shared)));}}
        internal bool LatencyWarning {get{return active&&!busy&&!initializing&&LatencyResultCurrent&&!latencyChecking&&connectionLatency!=null&&connectionLatency.Error!=null;}}
        internal bool ConnectionWarning {get{return !initializing&&!busy&&(!networkAvailable||liveVpn.NeedsAttention||error!=null||(active&&(CheckerWarning||LatencyWarning||(!ManagedMode&&(!hasBackup||!dnsConfirmed||(autoGoal>=0&&autoNeedsRetry))))));}}
        internal string CheckerCaption {
            get {
                const string prefix="Чекер сайта: ";
                if(initializing)return prefix+"ожидаем состояние DNS";
                if(busy)return prefix+"ждём применения DNS";
                if(!active)return prefix+"DNS выключен";
                if(!ResolverResultCurrent)return prefix+(autoResolverCheck?"ещё не проверен":"автопроверка выключена");
                if(resolverChecking)return prefix+"проверяем "+Dns.Name[dnsSet]+"…";
                if(resolverResult==null)return prefix+"нет результата";
                if(CheckerConfirmed)return prefix+Dns.Name[dnsSet]+" подтверждён";
                if(CheckerRejected)return prefix+Dns.Name[dnsSet]+" не подтверждён";
                if(resolverResult.Official!=null&&resolverResult.Official.State==ProviderCheckState.ProbePassed)return prefix+"Malw отвечает";
                if(resolverResult.Comparison==ResolverComparison.NetworkChanged||resolverResult.Official!=null&&resolverResult.Official.State==ProviderCheckState.NetworkChanged)return prefix+"сеть изменилась · повторите";
                return prefix+(resolverResult.Official==null&&resolverResult.Comparison==ResolverComparison.Shared?"нет подтверждения провайдера":"не удалось проверить");
            }
        }
        internal string CheckerTip {get{return CheckerCaption+"\n"+(ResolverResultCurrent&&resolverResult!=null&&resolverResult.Official!=null?"Источник: "+resolverResult.Official.Source+"; результат от "+resolverResult.Official.CheckedUtc.ToLocalTime().ToString("HH:mm:ss")+".\n":"")+(ResolverResultCurrent&&resolverResult!=null&&resolverResult.Official!=null&&resolverResult.Official.State==ProviderCheckState.ProbePassed?"Проверочный адрес Malw ответил.\nЕго чекер не различает обычный Malw DNS и Malw CF (Cloudflare).\nПоэтому использование Cloudflare этим тестом не подтверждено.\n":"")+"Проверяется системный DNS Windows.\nСобственный DoH браузера и ответы ИИ не проверяются.\nПодробности: Тест DNS → Применение DNS.";}}
        CancellationTokenSource latencyCancel;int latencyRevision,latencyProvider=-1;DnsTransport latencyTransport;bool latencyLocal,latencyGoogle,latencyChecking;ProviderLatency connectionLatency;long nextLatencyAt;string latencyStamp;
        internal Rectangle LatencyStatusBounds {get{return new Rectangle(30,LowerY(416),layoutWidth-60,34);}}
        internal bool LatencyResultCurrent {get{return active&&!busy&&!initializing&&latencyProvider==dnsSet&&latencyTransport==Transport&&latencyLocal==localMode&&latencyGoogle==googleMode;}}
        internal string ConnectionLatencyLabel {get{return !active||busy||initializing?null:!LatencyResultCurrent||connectionLatency==null?(latencyChecking&&LatencyResultCurrent?"… мс":"— мс"):connectionLatency.Error!=null?"— мс":connectionLatency.Milliseconds==0?"<1 мс":connectionLatency.Milliseconds+" мс";}}
        internal string ConnectionStatusCaption {get{return ConnectionLabel+(ConnectionLatencyLabel==null?"":" · "+ConnectionLatencyLabel);}}
        internal string ConnectionLatencyTip {get{return "Время ответа DNS, не ICMP-пинг. Запрос example.com.\n"+(LatencyResultCurrent&&connectionLatency!=null?(connectionLatency.Path??"")+"\nЗамер: "+connectionLatency.MeasuredUtc.ToLocalTime().ToString("HH:mm:ss")+(connectionLatency.Error==null?"":"; нет ответа или ошибка проверки.")+"\n":"")+"Обновляется каждые 30 секунд при открытом окне. Нажмите статус для повторного замера. Пинг не подтверждает применение DNS или доступ к ИИ.";}}
        ContextMenuStrip connectionMenu,connectionProtocolMenu;
        internal ConnectionMethod ConnectionMethod {get{return localMode?ConnectionMethod.Application:googleMode?ConnectionMethod.Google:ConnectionMethod.Windows;}}
        internal bool ConnectionChangeLocked {get{return initializing||busy||active||ManagedDns.Running;}}
        internal Rectangle ConnectionModeButton {get{return new Rectangle(rSecureOption.X+12,rSecureOption.Y+24,rSecureOption.Width-168,28);}}
        internal Rectangle ConnectionProtocolButton {get{return new Rectangle(rSecureOption.Right-148,rSecureOption.Y+24,136,28);}}
        internal string ConnectionHint {get{return ConnectionUi.Hint(ConnectionMethod);}}
        internal string ConnectionCardHint {get{return Transport==DnsTransport.Plain?"Открытые DNS-запросы · возможен перехват":ConnectionHint;}}
        bool hideDnsReminder = ReadHiddenRestartPrompt(RestartPrompts.BrowserValue);
        bool hideAntigravityRestart = ReadHiddenRestartPrompt(RestartPrompts.AntigravityValue);
        AgStatus agStatus = new AgStatus();
        bool agLoading = true;
        bool cleanupDone, cleanupBackupAvailable = Directory.Exists(AgCleanupBackup.Root);
        Rectangle rAgCleanup = new Rectangle(280, 807, 114, 26);
        Rectangle rAgOption = new Rectangle(28, 754, 384, 58);
        Rectangle rSecureOption = new Rectangle(28, 672, 384, 72);
        bool secureDns, tlsDns;
        internal DnsTransport Transport { get { return secureDns ? (tlsDns ? DnsTransport.Tls : DnsTransport.Https) : DnsTransport.Plain; } }
        internal bool TransportAvailable(DnsTransport mode) { return mode == DnsTransport.Plain ? !ManagedMode&&!Dns.EncryptedOnly(dnsSet) : mode == DnsTransport.Tls ? DotDns.Available(dnsSet) : EncryptedDns.Templates[dnsSet] != null; }
        internal void SelectTransport(DnsTransport mode) {
            if (busy || initializing || (ManagedMode&&(ManagedDns.Running||mode==DnsTransport.Plain)) || !TransportAvailable(mode) || Transport == mode) return;
            if (active) Apply(true, transport: mode);
            else { operationRevision++; secureDns = mode != DnsTransport.Plain; tlsDns = mode == DnsTransport.Tls; }
            if(ManagedMode)SaveGooglePreference();
            probeResults = new DnsProbeResult[Dns.Name.Length][];
            probeLabels = Enumerable.Repeat("Ответы DNS ещё не проверены",Dns.Name.Length).ToArray(); Invalidate();
        }
        internal void SelectConnectionMethod(ConnectionMethod method,bool persist=true)
        {
            if(method==ConnectionMethod||ConnectionChangeLocked)return;
            switch(method){case ConnectionMethod.Application:SelectLocalMode(true,persist);break;case ConnectionMethod.Google:SelectGoogleMode(true,persist);break;case ConnectionMethod.Windows:if(localMode)SelectLocalMode(false,persist);else SelectGoogleMode(false,persist);break;default:throw new ArgumentOutOfRangeException("method");}
        }
        internal string TransportUnavailableHint(DnsTransport transport)
        {return transport==DnsTransport.Plain?(ManagedMode?"В способах 2 и 3 используется только шифрованный DNS":Dns.Name[dnsSet]+" работает только с шифрованием"):Dns.Name[dnsSet]+" не публикует адрес для "+ConnectionUi.ProtocolName(transport);}
        internal void SelectConnectionTransport(DnsTransport transport){if(!ConnectionChangeLocked)SelectTransport(transport);}
        internal ContextMenuStrip CreateConnectionMenu()
        {return ConnectionUi.Methods(ConnectionMethod,ConnectionChangeLocked,method=>SelectConnectionMethod(method),new Font(fMed,Math.Max(12f,14f*uiScale),GraphicsUnit.Pixel),CardBg,Ink,Accent,CardLine,(int)Math.Round(Math.Min(rSecureOption.Width,420)*uiScale));}
        internal ContextMenuStrip CreateConnectionProtocolMenu()
        {return ConnectionUi.Protocols(Transport,new[]{TransportAvailable(DnsTransport.Plain),TransportAvailable(DnsTransport.Https),TransportAvailable(DnsTransport.Tls)},ConnectionChangeLocked,SelectConnectionTransport,new Font(fMed,Math.Max(12f,14f*uiScale),GraphicsUnit.Pixel),CardBg,Ink,Accent,CardLine,(int)Math.Round(Math.Min(rSecureOption.Width,420)*uiScale),TransportUnavailableHint);}
        void ShowConnectionProtocolMenu(){if(connectionProtocolMenu!=null)connectionProtocolMenu.Dispose();connectionProtocolMenu=CreateConnectionProtocolMenu();ShowConnectionPopup(connectionProtocolMenu);}
        void ShowConnectionMenu()
        {
            if(connectionMenu!=null)connectionMenu.Dispose();connectionMenu=CreateConnectionMenu();
            ShowConnectionPopup(connectionMenu);
        }
        void ShowConnectionPopup(ContextMenuStrip popup)
        {
            var anchor=new Point((int)(canvasOffset.X+rSecureOption.X*uiScale),(int)(canvasOffset.Y+rSecureOption.Bottom*uiScale));
            if(PointToScreen(anchor).Y+popup.Height>Screen.FromControl(this).WorkingArea.Bottom)anchor.Y=(int)(canvasOffset.Y+rSecureOption.Top*uiScale)-popup.Height;
            popup.Show(this,anchor);
        }
        int dnsSet = Dns.AvailableProviders[0];
        int autoGoal = -1;
        int defaultGoal=-1;
        bool manualSelection;
        bool UseDefaultGoal {get{return defaultGoal>=0&&!manualSelection;}}
        bool autoNeedsRetry, checkingAuto;
        long lastAutoCheck;
        readonly CancellationTokenSource autoLifetime = new CancellationTokenSource();
        int SelectedSegment { get { return !googleMode&&(autoGoal >= 0||(!active&&UseDefaultGoal)) ? Dns.Name.Length : dnsSet; } }
        string error;
        float pulse;
        Rectangle rToggle, rClose, rSite, rGroup, rBrand;
        Rectangle rTheme = new Rectangle(332, 24, 36, 34);
        Rectangle rSettings = new Rectangle(288, 24, 36, 34);
        Rectangle rVersion,rUpdateIndicator;
        UpdateRelease availableUpdate;
        bool checkUpdateAtStartup = true;
        readonly ToolTip themeTip = new ToolTip();
        Rectangle[] rSeg = new Rectangle[Dns.Name.Length + 1];
        int[] VisibleSegments { get { return localMode?new[]{Dns.Name.Length}.Concat(UiProviders.Where(TunnelSession.EncryptedProvider)).ToArray():googleMode?UiProviders.Where(TunnelSession.GoogleProvider).ToArray():new[]{Dns.Name.Length}.Concat(UiProviders).ToArray(); } }
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
        readonly AntigravityStartupRecovery startupRecovery=new AntigravityStartupRecovery();
        bool startupRecoveryEnabled=AntigravityStartupRecovery.ReadEnabled();
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
        internal string ConnectionLabel { get { if(localMode)return initializing?"Проверяем настройки…":ManagedDns.Stopping?"Восстанавливаем DNS…":busy&&active?"Переключаем DNS…":busy?"Запускаем локальный DNS…":autoGoal>=0?"Авто · "+AutoDns.Goals[autoGoal].Name:!active&&UseDefaultGoal?"Авто · "+AutoDns.Goals[defaultGoal].Name:active?"Локальный DNS включён":"Локальный DNS · beta";if(googleMode)return initializing?"Проверяем настройки…":ManagedDns.Stopping?"Восстанавливаем DNS…":busy?"Подключаем Gemini…":"Gemini / NotebookLM";return initializing ? "Проверяем настройки…" : busy ? "Применяем настройки…" : !networkAvailable ? "Нет подключения" : liveVpn.NeedsAttention ?
            (liveVpn.Failed ? "Проверьте VPN" : "VPN мешает DNS") : active ? (hasBackup ? (dnsConfirmed ? (autoGoal >= 0 ? (autoNeedsRetry ? "Проверьте Авто" : "Авто · " + AutoDns.Goals[autoGoal].Name) : "DNS включён") : "Проверьте DNS") : "Найден DNS сервиса") : AntigravityOnlyLabel ?? "DNS выключен"; } }
        internal string EncryptionNote { get {
            if(localMode)return "Локальный DNS: Windows → 127.0.0.1 / ::1 → выбранный HTTPS или TLS. Работает, пока приложение открыто.";
            string protocol = tlsDns ? "DoT" : "DoH";
            if (EncryptedDns.Templates[dnsSet] == null) return "У этого провайдера настроен только обычный DNS";
            if (secureDns && agStatus.WindowsUpdateException)
                return protocol + (agStatus.External || agStatus.Checked ? " · исключения Windows Update и Antigravity" : " · исключение для Windows Update");
            return protocol + (secureDns && (agStatus.External || agStatus.Checked) ? " · Antigravity по отдельным правилам" : " адаптера · IPv6 приостанавливается");
        } }
        internal bool AntigravityUsesSharedDoh { get { return secureDns && !agLoading && !agStatus.Checked && !agStatus.External && agStatus.Error == null; } }
        internal bool SharedAntigravityDeclared {get{return Dns.IsAvailable(dnsSet)&&RuntimeProviders.All[dnsSet].autoGoals.Contains("Antigravity");}}
        internal bool AntigravityCardInfoOnly {get{return AntigravityUsesSharedDoh||!SharedAntigravityDeclared&&!agLoading&&!agStatus.Checked&&!agStatus.External&&agStatus.Error==null;}}
        internal string SharedAntigravityTitle {get{return SharedAntigravityDeclared?"Antigravity · общий DNS":"Antigravity · не заявлено";}}
        internal string PlainAntigravityNote {get{return !Dns.IsAvailable(dnsSet)?"Провайдер отключён · выберите другой DNS":dnsSet==0?"Comss для этой опции отключён":AntigravityDns.ProviderForMainSet(dnsSet)<0?"Отдельный маршрут для этого DNS не заявлен":"Будет использован: "+Dns.Name[dnsSet];}}
        internal string SharedAntigravityNote { get { return !SharedAntigravityDeclared?"Поддержка Antigravity у "+Dns.Name[dnsSet]+" не заявлена.":active&&dnsConfirmed?"Общий DNS настроен · Antigravity не проверен":"Включите DNS большой кнопкой · доступ не проверен"; } }
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

        public MainForm():this(new TunnelSession()){}
        internal MainForm(ITunnelSession session,bool readProfilePreference=true,ITunnelSession local=null)
        {
            googleTunnel=session;localSession=local??new TunnelSession(null,true);profilePreferences=readProfilePreference;googleTunnel.Changed+=GoogleTunnelChanged;localSession.Changed+=GoogleTunnelChanged;
            LoadFonts();
            LoadServiceIcons();
            darkTheme = ReadDarkTheme();
            defaultGoal=ReadDefaultGoal();
            if(readProfilePreference)try{using(var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))hiddenProviders=ProviderPreferences.Read(key);if(Dns.AvailableProviders.All(hiddenProviders.Contains))hiddenProviders=new int[0];}catch(Exception){}
            if(readProfilePreference)try{using(var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))autoResolverCheck=key==null||!object.Equals(key.GetValue("AutoResolverCheck"),0);}catch(Exception){}
            if(readProfilePreference)try{using(var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))googleMode=key!=null&&object.Equals(key.GetValue("GoogleTunnelMode"),1);}catch(Exception){}
            if(googleMode){secureDns=true;tlsDns=true;manualSelection=true;try{using(var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns")){
                object saved=key==null?null:key.GetValue("GoogleTunnelProvider");if(saved is int&&TunnelSession.GoogleProvider((int)saved))dnsSet=(int)saved;
                string protocol=key==null?null:key.GetValue("GoogleTunnelProtocol") as string;tlsDns=protocol=="HTTPS"?false:DotDns.Available(dnsSet)&&dnsSet!=6;
            }}catch(Exception){}if(readProfilePreference&&!DnsTunnelNative.IsAvailable())error=MissingWintunInlineError;}
            if(readProfilePreference)try{using(var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))if(key!=null&&object.Equals(key.GetValue("LocalResolverMode"),1)){
                localMode=true;googleMode=false;secureDns=true;manualSelection=defaultGoal<0;
                object saved=key.GetValue("LocalResolverProvider");if(saved is int&&TunnelSession.EncryptedProvider((int)saved))dnsSet=(int)saved;
                tlsDns=key.GetValue("LocalResolverProtocol") as string=="TLS"&&DotDns.Available(dnsSet);
            }}catch(Exception){}
            if(readProfilePreference)try{using(var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))if(key!=null){localAntigravity=object.Equals(key.GetValue("LocalAntigravityEnabled"),1);localAntigravityTls=object.Equals(key.GetValue("LocalAntigravityTls"),1);object provider=key.GetValue("LocalAntigravityProvider");if(provider is int&&LocalDnsRouting.Supports((int)provider))localAntigravityProvider=(int)provider;else if(provider!=null)localAntigravity=false;}}catch(Exception){}
            if(hiddenProviders.Contains(dnsSet))dnsSet=UiProviders.First();
            if(!LocalDnsRouting.Supports(localAntigravityProvider)){localAntigravityProvider=LocalDnsRouting.Supports(5)?5:LocalDnsRouting.Providers.DefaultIfEmpty(-1).First();if(localAntigravityProvider<0)localAntigravity=false;}
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
            networkMonitor.Tick += (s, e) => {RefreshConnectionLatency();RefreshNetworkStatus();if(profilePreferences){startupRecovery.Tick(startupRecoveryEnabled);TickAntigravityHealth();}};
            Shown += (s, e) => { RefreshNetworkStatus(); networkMonitor.Start(); };
            Shown += (s, e) => { if(checkUpdateAtStartup)CheckUpdatesQuietly(); };
            motionReady = true;
            SnapMotion();
        }

        void RefreshNetworkStatus()
        {
            if (checkingNetwork || busy || !Visible || WindowState == FormWindowState.Minimized) return;
            if (ManagedDns.Running) { if (!profilePreferences) return; checkingNetwork = true; ThreadPool.QueueUserWorkItem(_ => { var vpn = VpnCheck.Scan(); bool connected = NetworkCheck.HasConnection(); if (IsDisposed || Disposing) return; try { BeginInvoke((Action)(() => { checkingNetwork = false; if (busy || !ManagedDns.Running) return; bool vpnChanged = liveVpn.NeedsAttention != vpn.NeedsAttention, netChanged = networkAvailable != connected; networkAvailable = connected; UpdateVpnStatus(vpn); if (vpnChanged && active) StartResolverCheck(); else if (netChanged) Invalidate(); })); } catch (InvalidOperationException) { } }); return; }
            checkingNetwork = true;
            int revision = operationRevision;
            ThreadPool.QueueUserWorkItem(_ => {
                var status = VpnCheck.Scan();
                bool connected = NetworkCheck.HasConnection();
                string observerNetwork=null;try{observerNetwork=new MangoResolverCheckIo().Stamp();}catch(Exception){}
                var extraDns = AntigravityDns.GetStatus();
                int actual = -1; bool backup = hasBackup, availableBackup = false, strictNow = false, tlsNow = false;
                try { actual = Dns.ActiveSetFor(dnsSet); backup = Dns.HasBackup(); availableBackup = backup && Dns.HasAvailableBackup(); if (actual >= 0) { strictNow = Dns.EncryptedOnly(actual) || Dns.StrictConfigured(); tlsNow = strictNow && Dns.TlsConfigured(); } }
                catch (Exception) { }
                if (IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => {
                    checkingNetwork = false;
                    if (!AcceptNetworkSnapshot(revision)) return;
                    bool firstRead = initializing;
                    if(resolverStamp!=null&&resolverStamp!=observerNetwork)CancelResolverCheck();
                    if(latencyStamp!=null&&latencyStamp!=observerNetwork)CancelConnectionLatency();
                    string before = VisualState();
                    if (networkAvailable != connected) {
                        probeResults = new DnsProbeResult[Dns.Name.Length][];
                        probeLabels = Enumerable.Repeat(connected ? "Ответы DNS ещё не проверены" : "Нет сетевого подключения", Dns.Name.Length).ToArray();
                    }
                    networkAvailable = connected; hasBackup = backup; dnsConfirmed = actual >= 0;
                    agStatus = extraDns; agLoading = false;
                    if (actual >= 0&&!ManagedMode) { secureDns = strictNow; tlsDns = tlsNow; }
                    active = !ManagedMode&&(actual >= 0 || availableBackup);
                    if (actual >= 0 && actual != dnsSet&&!ManagedMode) { if (!firstRead) autoGoal = -1; dnsSet = actual; EnsureProviderVisible(); }
                    initializing = false;
                    if(firstRead)EnsureProviderVisible();
                    UpdateVpnStatus(status);
                    if (before != VisualState()) Invalidate();
                    if (firstRead&&!ManagedMode) CheckDnsServers();
                    if(firstRead&&active)StartResolverCheck();
                    if(!ManagedMode)CheckAutoConnection();
                })); }
                catch (InvalidOperationException) { }
            });
        }

        internal bool AcceptNetworkSnapshot(int revision)
        { return !busy && !ManagedDns.Running && revision == operationRevision; }

        void CheckUpdatesQuietly()
        {
            var token=autoLifetime.Token;
            ThreadPool.QueueUserWorkItem(_=> {
                UpdateRelease release;
                try {if(!NetworkCheck.HasConnection())return;release=AppUpdate.Check(VersionLabel,token);}
                catch(OperationCanceledException){return;}
                catch(Exception problem){DiagnosticReport.Record("Фоновая проверка обновлений",problem.ToString());return;}
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
            var dialog=CreateDialog(available?"Копия правил Antigravity\n\nОткройте папку с сохранёнными копиями правил.":"Копия правил Antigravity\n\nСохранённых копий пока нет. Копия создаётся при удалении старых правил.","Настройки",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1,Math.Min(uiScale,(Screen.FromControl(this).WorkingArea.Height-32f)/800f));
            dialog.AddSettingsControls(available,()=>Open(AgCleanupBackup.Root),AutoDns.Goals.Select(goal=>goal.Name).ToArray(),defaultGoal,url=>Open(url));
            dialog.AddRestartSettingsControls(!hideDnsReminder,!hideAntigravityRestart);
            dialog.AddStartupRecoveryControl(startupRecoveryEnabled);
            dialog.AddLogSettingsAction(update=>SaveApplicationLog(dialog,update));dialog.AddResolverSettingsAction(autoResolverCheck);
            dialog.AddProviderSettingsControls(hiddenProviders,Transport);
            dialog.AddAntigravitySettingsControls(localAntigravity,localAntigravityProvider,ManagedDns.Running||busy,localMode&&localAntigravity&&ManagedDns.Connected&&!ManagedDns.Stopping&&!busy);
            dialog.SetAntigravityAutoDns(agAutoDns);
            dialog.CustomDnsLocked=id=>busy||active&&id==dnsSet||ManagedDns.Running&&localAntigravity&&id==localAntigravityProvider;
            return dialog;
        }
        string LogContext(){return "Provider="+Dns.Name[dnsSet]+"; transport="+Transport+"; Google profile="+googleMode+"; local resolver="+localMode+"; separate Antigravity="+localAntigravity+"; Antigravity transport="+(localAntigravityTls?"DoT":"DoH")+"; Antigravity provider="+localAntigravityProvider+"; active="+active+"; worker running="+ManagedDns.Running+"; connected="+ManagedDns.Connected+"; stopping="+ManagedDns.Stopping+"; statistics="+ManagedDns.Statistics+"; startup recovery="+startupRecoveryEnabled+"; startup page="+startupRecovery.Observation+"; Antigravity auto DNS="+agAutoDns+"; auto status="+agAutoStatus;}
        void SaveApplicationLog(Form owner,Action<string> update)
        {
            using(var save=new SaveFileDialog{Filter="Журнал (*.txt)|*.txt",FileName="SmartDNS-"+VersionLabel+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt"}) {
                if(save.ShowDialog(owner)!=DialogResult.OK)return;string file=save.FileName,context=LogContext();update("Сохраняем журнал…");
                ThreadPool.QueueUserWorkItem(_=>{string result;
                    try{File.WriteAllText(file,DiagnosticReport.Export(context),new System.Text.UTF8Encoding(true));result="Журнал сохранён · скачать ещё раз";}
                    catch(Exception problem){DiagnosticReport.Record("Сохранение журнала",problem.ToString());result="Не удалось сохранить · повторить";}
                    if(!owner.IsDisposed)try{owner.BeginInvoke((Action)(()=>update(result)));}catch(InvalidOperationException){}
                });
            }
        }
        internal bool GoogleMode{get{return googleMode;}}
        void SaveGooglePreference()
        {
            if(!profilePreferences)return;
            try{using(var key=Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KodVsego\SmartDns")){key.SetValue("GoogleTunnelMode",googleMode?1:0,RegistryValueKind.DWord);key.SetValue("LocalResolverMode",localMode?1:0,RegistryValueKind.DWord);string prefix=localMode?"LocalResolver":"GoogleTunnel";key.SetValue(prefix+"Provider",dnsSet,RegistryValueKind.DWord);key.SetValue(prefix+"Protocol",tlsDns?"TLS":"HTTPS",RegistryValueKind.String);}}
            catch(Exception problem){DiagnosticReport.Record("Настройки профиля",problem.ToString());}
        }
        internal const string MissingWintunTitle="Нужен файл wintun.dll (способ 3)";
        internal const string MissingWintunInlineError="Нет wintun.dll рядом с EXE · выберите способ 2 или скачайте ZIP / MSI";
        internal const string MissingWintunMessage="Экспериментальный режим «3 · Gemini / NotebookLM · beta» создаёт виртуальный адаптер и требует файл wintun.dll рядом с программой. В одиночный EXE он не встроен, чтобы избежать ложных срабатываний антивирусов.\n\nКак решить прямо сейчас:\n• Нажмите «Способ 2» ниже — включится режим «2 · Через приложение · beta» (DoH / DoT для всего ПК из одного EXE без драйверов).\n\nКак включить способ 3:\n• Скачайте установщик MSI или архив Portable ZIP с GitHub (в них wintun.dll уже в комплекте), либо положите официальный wintun.dll (Wintun 0.14.1 x64) в папку с этим EXE:";
        internal StyledDialog CreateMissingWintunDialog()
        {
            var dialog=CreateDialog(MissingWintunMessage,MissingWintunTitle,MessageBoxButtons.YesNo,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1);
            dialog.SetPrimaryText("Способ 2");
            dialog.AddBodyLinks(
                new KeyValuePair<string,Action>("Скачать MSI или Portable ZIP на GitHub ↗",()=>Open(AppUpdate.MsiReleasePage(VersionLabel.TrimStart('v')))),
                new KeyValuePair<string,Action>("Официальный сайт Wintun (wintun.net) ↗",()=>Open("https://www.wintun.net/")),
                new KeyValuePair<string,Action>("Открыть папку с этим EXE ↗",()=>Open(AppDomain.CurrentDomain.BaseDirectory)));
            return dialog;
        }
        void ShowMissingWintunHelp()
        {
            error=MissingWintunInlineError;Invalidate();
            using(var dialog=CreateMissingWintunDialog())
                if(dialog.ShowDialog(this)==DialogResult.Yes)SelectLocalMode(true,true);
        }
        internal void SelectGoogleMode(bool enabled,bool persist=false)
        {
            if(initializing||busy||ManagedDns.Running)return;
            if(!ManagedMode&&active&&dnsConfirmed){error="Сначала выключите обычный DNS большой кнопкой";Invalidate();return;}
            CancelResolverCheck();CancelConnectionLatency();operationRevision++;localMode=false;googleMode=enabled;manualSelection=true;autoGoal=-1;error=null;
            if(enabled){if(!TunnelSession.GoogleProvider(dnsSet))dnsSet=0;secureDns=true;tlsDns=dnsSet!=6&&DotDns.Available(dnsSet);active=false;dnsConfirmed=false;}
            if(persist)SaveGooglePreference();
            DiagnosticReport.Record("Профиль подключения","Gemini / NotebookLM="+enabled+"; DNS scope=entire PC");
            layoutReady=false;UpdateCanvasBounds();Invalidate();if(enabled&&persist&&profilePreferences&&!DnsTunnelNative.IsAvailable())ShowMissingWintunHelp();
        }
        internal void SelectLocalMode(bool enabled,bool persist=false)
        {
            if(initializing||busy||ManagedDns.Running)return;
            if(!ManagedMode&&active&&dnsConfirmed){error="Сначала выключите обычный DNS большой кнопкой";Invalidate();return;}
            CancelResolverCheck();CancelConnectionLatency();operationRevision++;localMode=enabled;googleMode=false;manualSelection=!enabled||defaultGoal<0;autoGoal=-1;error=null;
            if(enabled){if(!TunnelSession.EncryptedProvider(dnsSet))dnsSet=5;secureDns=true;tlsDns=false;active=false;dnsConfirmed=false;}
            if(persist)SaveGooglePreference();
            DiagnosticReport.Record("Профиль подключения","Local resolver="+enabled+"; loopback only; DNS scope=entire PC");EnsureProviderVisible();layoutReady=false;UpdateCanvasBounds();Invalidate();
        }
        internal bool EnableAntigravityAppMode(int provider=-1,bool persist=true)
        {
            if(initializing||busy||ManagedDns.Running)return false;
            if(!ManagedMode&&active&&dnsConfirmed){error="Сначала выключите обычный DNS большой кнопкой";Invalidate();return false;}
            int chosen=LocalDnsRouting.Supports(provider)?provider:LocalDnsRouting.Supports(localAntigravityProvider)?localAntigravityProvider:LocalDnsRouting.Supports(5)?5:LocalDnsRouting.Providers.DefaultIfEmpty(5).First();
            SelectLocalMode(true,false);
            tlsDns=false;secureDns=true;
            if(persist)SaveGooglePreference();
            SelectLocalAntigravity(true,chosen);
            return localMode&&localAntigravity;
        }
        internal void SelectLocalAntigravity(bool enabled,int provider)
        {
            if(!localMode||busy||!LocalDnsRouting.Supports(provider))return;
            if(ManagedDns.Running){if(enabled&&localAntigravity)SwitchLocalAntigravity(provider);return;}
            SaveLocalAntigravity(enabled,provider);
        }
        internal bool SwitchLocalAntigravity(int provider,bool tls=false)
        {
            var session=localSession as ILocalAntigravitySwitchSession;
            if(!localMode||busy||!ManagedDns.Connected||ManagedDns.Stopping||session==null||!UiProviders.Contains(provider)||!LocalDnsRouting.Supports(provider))return false;
            ResetAgHealthWatch();
            error=null;
            try{var protocolSession=session as ILocalAntigravityTransportSession;if(tls&&(protocolSession==null||!DotDns.Available(provider)))return false;if(protocolSession!=null)protocolSession.SwitchAntigravity(provider,tls);else session.SwitchAntigravity(provider);pendingAntigravityProvider=provider;pendingAntigravityTls=tls;busy=true;}
            catch(Exception problem){error=TunnelDiagnostics.Error(problem);Invalidate();return false;}
            DiagnosticReport.Record("Смена Antigravity","Requested="+Dns.Name[provider]+"; previous="+Dns.Name[localAntigravityProvider]+"; common DNS unchanged; awaiting worker acknowledgement");Invalidate();return true;
        }
        void SaveLocalAntigravity(bool enabled,int provider,bool tls=false)
        {
            localAntigravity=enabled;localAntigravityProvider=provider;localAntigravityTls=tls;
            if(profilePreferences)try{using(var key=Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KodVsego\SmartDns")){key.SetValue("LocalAntigravityTls",tls?1:0,RegistryValueKind.DWord);key.SetValue("LocalAntigravityEnabled",enabled?1:0,RegistryValueKind.DWord);key.SetValue("LocalAntigravityProvider",provider,RegistryValueKind.DWord);}}catch(Exception problem){DiagnosticReport.Record("Настройки Antigravity",problem.ToString());}
            DiagnosticReport.Record("Локальный Antigravity","enabled="+enabled+"; provider="+Dns.Name[provider]+"; protocol="+(tls?"DoT":"DoH")+"; worker connected="+ManagedDns.Connected+"; model NOT verified");Invalidate();
        }
        internal void ApplyAntigravitySettings(bool enabled,int provider)
        {
            if(enabled==localAntigravity&&provider==localAntigravityProvider)return;
            if(!LocalDnsRouting.Supports(provider))throw new IOException("Выбранный DNS не поддерживает отдельный маршрут Antigravity.");
            if(ManagedDns.Running){
                if(!enabled||!localAntigravity||!localMode||!SwitchLocalAntigravity(provider))throw new IOException("Сначала выключите DNS для изменения этой настройки Antigravity.");
            }else{if(busy)throw new IOException("Дождитесь завершения изменения DNS.");SaveLocalAntigravity(enabled,provider);}
        }
        internal bool ShouldOfferLocalAntigravityRestart(bool antigravityRunning,bool automatic=false)
        {
            return !automatic&&!hideAntigravityRestart&&localMode&&localAntigravity&&antigravityRunning;
        }
        void OfferLocalAntigravityRestart(bool automatic=false)
        {
            if(automatic){DiagnosticReport.Record("Перезапуск Antigravity","Авто: DNS применён без диалога и без перезапуска клиента. При необходимости перезапустите Antigravity вручную.");return;}
            if(!profilePreferences||!ShouldOfferLocalAntigravityRestart(AntigravityRestart.IsRunning()))return;
            string providerName=LocalDnsRouting.Supports(localAntigravityProvider)?Dns.Name[localAntigravityProvider]:"DNS";
            using(var dialog=CreateDialog("Локальный DNS и маршрут Antigravity ("+providerName+") включены.\n\nПерезапустить запущенный Antigravity сейчас, чтобы он сразу переключился на новый DNS? Сохраните изменения перед продолжением.","Перезапустить Antigravity?",MessageBoxButtons.YesNo,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1))
            {
                dialog.SetPrimaryText("Перезапустить");
                if(dialog.ShowDialog(this)!=DialogResult.Yes)return;
            }
            ThreadPool.QueueUserWorkItem(_=>{
                string restartError=null;
                try{AntigravityRestart.Run();}catch(Exception ex){restartError=ex.Message;}
                if(restartError==null||IsDisposed||Disposing)return;
                try{BeginInvoke((Action)(()=>ShowMessage(restartError,"Перезапуск Antigravity",MessageBoxButtons.OK,MessageBoxIcon.Warning)));}catch(InvalidOperationException){}
            });
        }
        void GoogleTunnelChanged()
        {
            if(IsDisposed||Disposing||!IsHandleCreated)return;
            try{BeginInvoke((Action)(()=>{
                if(IsDisposed)return;bool wasConnected=active,wasBusy=busy;int previousProvider=dnsSet;var changing=localMode?localSession as ILocalDnsSwitchSession:null;
                operationRevision++;busy=ManagedDns.Running&&(!ManagedDns.Connected||ManagedDns.Stopping||changing!=null&&changing.Switching);active=ManagedDns.Connected&&!ManagedDns.Stopping;dnsConfirmed=active;error=ManagedDns.LastError;
                bool justConnected=active&&!wasConnected;
                if(active&&changing!=null&&!changing.Switching&&Dns.IsAvailable(changing.CurrentProvider)&&(dnsSet!=changing.CurrentProvider||tlsDns!=changing.CurrentTls)){dnsSet=changing.CurrentProvider;tlsDns=changing.CurrentTls;SaveGooglePreference();EnsureProviderVisible();}
                CompleteLocalAutoSwitch(changing);
                CompleteAgCommon(changing);
                if(pendingAntigravityProvider>=0&&(!busy||!active)){
                    var special=localSession as ILocalAntigravitySwitchSession;int requested=pendingAntigravityProvider;pendingAntigravityProvider=-1;
                    bool automatic=agAutomaticPending==requested;
                    bool success=active&&special!=null&&special.CurrentAntigravity==requested&&(!(special is ILocalAntigravityTransportSession)||((ILocalAntigravityTransportSession)special).CurrentAntigravityTls==pendingAntigravityTls)&&error==null;CompleteAgFailover(requested,success);
                    if(success){SaveLocalAntigravity(true,requested,pendingAntigravityTls);OfferLocalAntigravityRestart(automatic);}
                }
                if(active&&(!wasConnected||previousProvider!=dnsSet||wasBusy&&!busy))StartResolverCheck();else if(!active){CancelResolverCheck();CancelConnectionLatency();}
                if(!ManagedDns.Running&&googleCheck!=null)googleCheck.Cancel();Invalidate();
                if(justConnected&&localMode&&profilePreferences){OfferLocalAntigravityRestart(autoGoal>=0);ShowDnsReminder();}
                if(googleClosing&&!ManagedDns.Running){googleClosing=false;Close();}
            }));}catch(InvalidOperationException){}
        }
        void ToggleGoogleTunnel()
        {
            ResetAgHealthWatch();
            if(ManagedDns.Running){CancelResolverCheck();CancelConnectionLatency();ManagedDns.Stop();return;}
            if(busy)return;if(!localMode&&profilePreferences&&!DnsTunnelNative.IsAvailable()){ShowMissingWintunHelp();return;}operationRevision++;error=null;busy=true;Invalidate();
            try{if(localMode){var config=localSession as ILocalDnsConfiguration;if(config is ILocalAntigravityTransportSession)((ILocalAntigravityTransportSession)config).SetAntigravity(localAntigravity?localAntigravityProvider:-1,localAntigravityTls&&DotDns.Available(localAntigravityProvider));else if(config!=null)config.SetAntigravity(localAntigravity?localAntigravityProvider:-1);}ManagedDns.Start(dnsSet,tlsDns);}catch(Exception problem){busy=false;error=TunnelDiagnostics.Error(problem);DiagnosticReport.Record(localMode?"Локальный DNS":"Gemini / NotebookLM",error);Invalidate();}
        }
        sealed class PendingLocalAuto
        {
            internal int Goal,Provider,PreviousGoal;internal bool Tls,PreviousManual,PreviousRetry,Antigravity;
        }
        PendingLocalAuto pendingLocalAuto;
        internal bool CanOpenAutoDns {get{return !busy&&!initializing&&!googleMode&&(!localMode||!ManagedDns.Running||active&&ManagedDns.Connected&&!ManagedDns.Stopping&&localSession is ILocalDnsSwitchSession);}}
        internal bool LiveAutoContextCurrent(int revision,int provider,DnsTransport protocol)
        {return CanOpenAutoDns&&localMode&&active&&ManagedDns.Running&&ManagedDns.Connected&&revision==resolverRevision&&provider==dnsSet&&protocol==Transport;}
        internal bool SwitchLocalAuto(AutoDnsResult result,int goal,int revision,int provider,DnsTransport protocol)
        {
            bool special=goal==AutoDns.GoalIndex("Antigravity");
            if(!LiveAutoContextCurrent(revision,provider,protocol)||result==null||!result.Ok||!result.Encrypted||result.Tls!=(special?false:protocol==DnsTransport.Tls)||goal<0||goal>=AutoDns.Goals.Length||special&&(!LocalDnsRouting.Supports(result.Provider)||!(localSession is ILocalAntigravitySwitchSession))||!AutoDns.Candidates(goal,true,result.Tls,UiProviders).Contains(result.Provider))return false;
            var pending=new PendingLocalAuto{Goal=goal,Provider=result.Provider,Tls=result.Tls,Antigravity=special,PreviousGoal=autoGoal,PreviousManual=manualSelection,PreviousRetry=autoNeedsRetry};
            CancelResolverCheck();CancelConnectionLatency();error=null;
            try{if(special)((ILocalAntigravitySwitchSession)localSession).SwitchAntigravity(result.Provider);else ((ILocalDnsSwitchSession)localSession).SwitchProvider(result.Provider,result.Tls);busy=true;pendingLocalAuto=pending;}
            catch(Exception problem){error=TunnelDiagnostics.Error(problem);busy=false;Invalidate();return false;}
            Invalidate();
            DiagnosticReport.Record("Авто · смена локального DNS","Goal="+AutoDns.Goals[goal].Name+"; requested="+Dns.Name[result.Provider]+"; current provider kept until worker acknowledgement");return true;
        }
        void CompleteLocalAutoSwitch(ILocalDnsSwitchSession session)
        {
            var pending=pendingLocalAuto;if(pending==null||session==null||session.Switching&&active&&!ManagedDns.Stopping)return;
            pendingLocalAuto=null;
            var special=localSession as ILocalAntigravitySwitchSession;
            bool success=active&&ManagedDns.Connected&&!ManagedDns.Stopping&&string.IsNullOrEmpty(ManagedDns.LastError)&&(pending.Antigravity?special!=null&&special.CurrentAntigravity==pending.Provider:session.CurrentProvider==pending.Provider&&session.CurrentTls==pending.Tls);
            autoGoal=success?pending.Goal:pending.PreviousGoal;manualSelection=success?false:pending.PreviousManual;autoNeedsRetry=success?false:pending.PreviousRetry;
            if(success)lastAutoCheck=frameClock.ElapsedMilliseconds;
            if(success&&pending.Antigravity){SaveLocalAntigravity(true,pending.Provider);OfferLocalAntigravityRestart(true);}
            EnsureProviderVisible();DiagnosticReport.Record("Авто · смена локального DNS",success?"Worker confirmed the Auto provider":"Auto selection reverted; previous mode and provider retained");
        }
        internal bool StartLocalAuto(AutoDnsResult result,int goal)
        {
            bool special=goal==AutoDns.GoalIndex("Antigravity");
            if(!localMode||initializing||busy||active||ManagedDns.Running||result==null||!result.Ok||!result.Encrypted||goal<0||goal>=AutoDns.Goals.Length||special&&(!LocalDnsRouting.Supports(result.Provider)||result.Tls)||!AutoDns.Candidates(goal,true,result.Tls,UiProviders).Contains(result.Provider))return false;
            if(special)SaveLocalAntigravity(true,result.Provider);
            CancelResolverCheck();CancelConnectionLatency();dnsSet=result.Provider;secureDns=true;tlsDns=result.Tls;autoGoal=goal;manualSelection=false;autoNeedsRetry=false;
            lastAutoCheck=frameClock.ElapsedMilliseconds;SaveGooglePreference();layoutReady=false;UpdateCanvasBounds();EnsureProviderVisible();
            DiagnosticReport.Record("Авто · локальный DNS","Goal="+AutoDns.Goals[goal].Name+"; provider="+Dns.Name[dnsSet]+"; protocol="+(tlsDns?"TLS":"HTTPS")+"; activation=local worker; separate Antigravity="+localAntigravity);
            ToggleGoogleTunnel();return true;
        }
        internal bool StartAntigravityAuto(AutoDnsResult result)
        {
            int goal=AutoDns.GoalIndex("Antigravity");
            if(initializing||busy||ManagedDns.Running||result==null||!result.Ok||!result.Encrypted||result.Tls||!LocalDnsRouting.Supports(result.Provider)||!AutoDns.Candidates(goal,true,false,UiProviders).Contains(result.Provider))return false;
            if(active){Apply(false,afterRestore:()=>StartAntigravityAuto(result));return true;}
            if(!EnableAntigravityAppMode(result.Provider,true))return false;
            return StartLocalAuto(result,goal);
        }
        void CheckGoogleServices()
        {
            if(googleChecking)return;
            if(!ManagedDns.Connected){probeLabels[dnsSet]="Сначала включите Gemini / NotebookLM";Invalidate();return;}
            googleChecking=true;int selected=dnsSet;bool tls=tlsDns;var cancel=new CancellationTokenSource();googleCheck=cancel;cancel.CancelAfter(90000);probeLabels[selected]="Проверяем Gemini / NotebookLM…";Invalidate();
            ThreadPool.QueueUserWorkItem(_=>{string result="Проверка завершена · журнал в настройках";
                try{var upstream=new TunnelUpstream(selected,tls);var probe=new ServiceAvailabilityProbe(tls);TunnelDiagnostics.Check(upstream.Query,probe.Request,line=>DiagnosticReport.Record("Проверка Google",line),cancel.Token,true);}
                catch(OperationCanceledException){result="Проверка отменена · журнал в настройках";}
                catch(Exception problem){DiagnosticReport.Record("Проверка Google",problem.ToString());result="Ошибка проверки · журнал в настройках";}
                if(!IsDisposed)try{BeginInvoke((Action)(()=>{googleChecking=false;if(googleCheck==cancel)googleCheck=null;cancel.Dispose();probeLabels[selected]=result;Invalidate();}));}catch(InvalidOperationException){}
            });
        }
        void ShowSettings()
        {
            using(var dialog=CreateSettingsDialog()) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                int selected=dialog.SettingsGoal;
                try {
                    using(var key=Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KodVsego\SmartDns")){
                        if(dialog.CustomProvidersChanged){
                            var draft=dialog.CustomProviders;var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
                            foreach(var previous in RuntimeProviders.Customs)if(dialog.CustomDnsLocked(previous.id)&&serializer.Serialize(previous)!=serializer.Serialize(draft[previous.id-CustomDns.First]))throw new IOException("Сначала отключите DNS: изменяемая запись сейчас используется.");
                            CustomDns.Save(key,draft);RefreshCustomProviders(draft);}
                        ProviderPreferences.Save(key,dialog.HiddenProviders);key.SetValue("DefaultService",selected<0?"":AutoDns.Goals[selected].Name,RegistryValueKind.String);key.SetValue("AutoResolverCheck",dialog.AutoResolverCheck?1:0,RegistryValueKind.DWord);RestartPrompts.Save(key,dialog.ShowBrowserRestartPrompt,dialog.ShowAntigravityRestartPrompt);}
                    using(var key=Registry.CurrentUser.CreateSubKey(CustomDns.RegistryPath))key.SetValue(AntigravityStartupRecovery.Preference,dialog.StartupRecoveryEnabled?1:0,RegistryValueKind.DWord);
                    startupRecoveryEnabled=dialog.StartupRecoveryEnabled;startupRecovery.Tick(startupRecoveryEnabled);
                    SetProviderVisibility(dialog.HiddenProviders);
                    hideDnsReminder=!dialog.ShowBrowserRestartPrompt;hideAntigravityRestart=!dialog.ShowAntigravityRestartPrompt;
                    autoResolverCheck=dialog.AutoResolverCheck;if(!autoResolverCheck)CancelResolverCheck();else if(active&&!busy)StartResolverCheck();
                    defaultGoal=selected;manualSelection=false;if(selected<0)autoGoal=-1;EnsureProviderVisible();Invalidate();
                    ApplyAntigravitySettings(dialog.AntigravityRouteEnabled,dialog.AntigravityRouteProvider);
                    SaveAgAutoPreference(dialog.AntigravityAutoDnsEnabled);
                }catch(Exception error){ShowMessage("Не удалось сохранить настройки.\n\n"+error.Message,"Настройки",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }
        }
        internal void RefreshCustomProviders(ProviderEntry[] entries)
        {
            CancelResolverCheck();CancelConnectionLatency();RuntimeProviders.Replace(entries);
            rSeg=new Rectangle[Dns.Name.Length+1];probeResults=new DnsProbeResult[Dns.Name.Length][];probeLabels=Enumerable.Repeat("Ответы DNS ещё не проверены",Dns.Name.Length).ToArray();
            if(!Dns.IsAvailable(dnsSet)&&!active)dnsSet=Dns.AvailableProviders.First();
            if(!LocalDnsRouting.Supports(localAntigravityProvider)&&!ManagedDns.Running)localAntigravity=false;
            layoutReady=false;EnsureProviderVisible();UpdateCanvasBounds();Invalidate();
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
                        catch(Exception error){dialog.ShowUpdateStatus("Не удалось открыть страницу релиза в браузере. Повторите попытку.","Не удалось открыть GitHub","Открыть релиз",true);dialog.SetDiagnosticDetails("Operation=OpenRelease\n"+error.ToString(),false);}
                        return;
                    }
                    working=true;
                    bool download=release!=null;
                    dialog.SetDiagnosticDetails(null,false);
                    dialog.ShowUpdateStatus(download?"Скачиваем обновление.\nНастройки DNS и тема сохранятся.":"Проверяем новые версии на GitHub…",download?"Загрузка":"Проверка обновлений",download?"Скачиваем…":"Проверяем…",false);
                    ThreadPool.QueueUserWorkItem(_=> {
                        UpdateTicket ticket=null;Exception failure=null;
                        try {
                            if(download) {
                                if(DnsElevation.IsAdministrator())throw new UpdateElevationException();
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
                        catch(Exception ex){failure=ex;}
                        if(IsDisposed||dialog.IsDisposed||token.IsCancellationRequested){if(ticket!=null)ticket.Cancel();return;}
                        try{BeginInvoke((Action)(()=> {
                            if(dialog.IsDisposed||token.IsCancellationRequested){if(ticket!=null)ticket.Cancel();return;}
                            working=false;
                            if(failure!=null){
                                dialog.ShowUpdateFailure(failure,download,NetworkCheck.HasConnection());return;
                            }
                            if(ticket!=null) {
                                try {ticket.Commit();dialog.DialogResult=DialogResult.OK;dialog.Close();Close();}
                                catch(Exception ex){ticket.Cancel();dialog.ShowUpdateStatus("Не удалось подготовить перезапуск. Повторите попытку. Текущая программа не изменена.","Не удалось перезапустить","Повторить",true);dialog.SetDiagnosticDetails("Operation=CommitUpdate\n"+ex.ToString(),false);}
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
            bool encrypted = secureDns; bool tls = tlsDns;
            var token = autoLifetime.Token;
            ThreadPool.QueueUserWorkItem(_ => {
                bool available = false;
                try { available = AutoDns.Best(AutoDns.Search(goal, encrypted, new AutoDnsProbe(tls), token, null, provider, tls)) != null; }
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
            if(googleTunnel!=null&&ManagedDns.Running){e.Cancel=true;googleClosing=true;ManagedDns.Stop();base.OnFormClosing(e);return;}
            if(e.CloseReason==CloseReason.UserClosing&&(busy||agLoading))e.Cancel=true;
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed) {
                resourcesDisposed = true;
                ResetAgHealthWatch();agLogs.Dispose();
                startupRecovery.Dispose();
                if(connectionMenu!=null)connectionMenu.Dispose();if(connectionProtocolMenu!=null)connectionProtocolMenu.Dispose();
                CancelResolverCheck();CancelConnectionLatency();googleTunnel.Changed-=GoogleTunnelChanged;localSession.Changed-=GoogleTunnelChanged;googleTunnel.Dispose();localSession.Dispose();if(googleCheck!=null)googleCheck.Cancel();
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

        static bool ReadHiddenRestartPrompt(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KodVsego\SmartDns"))
                    return RestartPrompts.Hidden(key,name);
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
                    key.SetValue(RestartPrompts.BrowserValue, 1, RegistryValueKind.DWord);
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

        void DrawConnectionChoice(Graphics g,Rectangle bounds,string text,Color color)
        {
            using(var path=RoundPath(bounds,8))using(var fill=new SolidBrush(Bg))using(var line=new Pen(CardLine)){g.FillPath(fill,path);g.DrawPath(line,path);}
            float size;using(var measure=UiFont(fBold,9.5f))size=Math.Min(9.5f,9.5f*(bounds.Width-34)/g.MeasureString(text,measure).Width);
            using(var font=UiFont(fBold,Math.Max(8f,size)))using(var ink=new SolidBrush(ConnectionChangeLocked?Muted:color))using(var format=new StringFormat{FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter,LineAlignment=StringAlignment.Center})
                g.DrawString(text,font,ink,new RectangleF(bounds.Left+10,bounds.Top,bounds.Width-34,bounds.Height),format);
            using(var pen=new Pen(ConnectionChangeLocked?Muted:Accent,1.4f)){float x=bounds.Right-14,y=bounds.Top+bounds.Height/2f;g.DrawLines(pen,new[]{new PointF(x-3,y-2),new PointF(x,y+1),new PointF(x+3,y-2)});}
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
            return ConnectionLabel + "|" + dnsSet + "|" + secureDns + "/" + tlsDns + "|" + hasBackup + "|" + dnsConfirmed + "|" +
                agLoading + "|" + agStatus.Checked + "|" + agStatus.Complete + "|" + agStatus.External + "|" +
                agStatus.Error + "|" + agStatus.Provider + "|" + agStatus.RecoveryNeeded + "|" + agStatus.WindowsUpdateException;
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
                rSecureOption=new Rectangle(28,LowerY(672),width-56,72);rAgOption=new Rectangle(28,LowerY(754),width-56,58);rAgCleanup=new Rectangle(width-160,LowerY(775),114,26);
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
            string status = ConnectionStatusCaption;
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
            if(googleMode)sub=error??(ManagedDns.Stopping?"Ожидаем восстановление прежних DNS":ManagedDns.Connected?(tlsDns?"DoT":"DoH")+" · "+Dns.Name[dnsSet]+" · DNS всего ПК":busy?"Проверяем сервер и создаём DNS-туннель":"Быстрый профиль · DNS всего ПК · beta");
            if(localMode)sub=error??(ManagedDns.Stopping?"Восстанавливаем прежние DNS":ManagedDns.Connected?"127.0.0.1 → "+Dns.Name[dnsSet]+" · "+(tlsDns?"TLS":"HTTPS")+(localAntigravity&&LocalDnsRouting.Supports(localAntigravityProvider)?" + Antigravity ("+Dns.Name[localAntigravityProvider]+")":""):autoGoal>=0||UseDefaultGoal?"Подберём DNS для "+AutoDns.Goals[autoGoal>=0?autoGoal:defaultGoal].Name:localAntigravity&&LocalDnsRouting.Supports(localAntigravityProvider)?"Готово: "+Dns.Name[dnsSet]+" + Antigravity ("+Dns.Name[localAntigravityProvider]+")":"Совместимость · шифрование внутри программы");
            if(ManagedMode&&error==null&&!busy&&!ManagedDns.Stopping){if(!networkAvailable)sub="Подключитесь к Wi-Fi или Ethernet";else if(liveVpn.NeedsAttention)sub=liveVpn.Failed?"Не удалось проверить сетевые адаптеры":"Включён VPN"+(liveVpn.Connections.Length>0?" ("+string.Join(", ",liveVpn.Connections)+")":"")+" · он может перехватывать DNS";}
            using (var f = UiFont(fHead,ConnectionStatusFontSize(g)))
            using (var b = new SolidBrush(!busy && ConnectionWarning ? WarningColor : (active || ((agStatus.Checked || agStatus.External) && agStatus.Error == null)) && !busy ? Accent : Ink))
                Centered(g, status, f, b, LowerY(420),W-60);
            using (var f = UiFont(fMed, 10.5f))
            using (var b = new SolidBrush(error != null ? (darkTheme ? Color.FromArgb(255, 151, 151) : Color.FromArgb(181, 59, 65)) : (!busy && (liveVpn.NeedsAttention || !networkAvailable) ? WarningColor : Muted)))
                Centered(g, sub, f, b, LowerY(454), W - 60);
            DrawCheckerStatus(g);

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
                var addresses=localMode&&active?new[]{"127.0.0.1","::1"}:secureDns&&EncryptedDns.Templates[dnsSet]!=null?(tlsDns?DotDns.Servers(dnsSet):EncryptedDns.ServersFor(dnsSet)):Dns.V4[dnsSet];
                g.DrawString(string.Join("  /  ",addresses.Take(2))+(addresses.Length>2?"  +"+(addresses.Length-2):""),fv,bv,card.X+76,card.Y+8);
            }
            if(googleMode){using(var font=UiFont(fBold,10f))using(var brush=new SolidBrush(Ink))g.DrawString("Gemini app · NotebookLM",font,brush,card.X+20,card.Y+33);}
            else DrawServiceIcons(g, new RectangleF(card.X + 20, card.Y + 33, card.Width - 40, 23));
            using (var f = UiFont(fMed, 8f))
            using (var b = new SolidBrush(Accent)) g.DrawString(probing ? "Проверяем ответы DNS…" : probeLabels[dnsSet], f, b, 48, LowerY(636));
            using (var path = RoundPath(DnsTestButton, 8))
            using (var b = new SolidBrush(Selection)) g.FillPath(b, path);
            using (var f = UiFont(fBold, 8f))
            using (var b = new SolidBrush(ContrastInk))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(googleMode?"Проверить":"Тест DNS", f, b, DnsTestButton, sf);

            using (var path = RoundPath(rSecureOption, 16))
            using (var fill = new SolidBrush(CardBg))
            using (var line = new Pen(CardLine)) { g.FillPath(fill, path); g.DrawPath(line, path); }
            using(var font=UiFont(fMed,8f))using(var ink=new SolidBrush(Muted)){
                g.DrawString("Способ подключения",font,ink,ConnectionModeButton.Left,rSecureOption.Top+5);
                g.DrawString("Шифрование",font,ink,ConnectionProtocolButton.Left,rSecureOption.Top+5);
            }
            DrawConnectionChoice(g,ConnectionModeButton,ConnectionUi.CompactName(ConnectionMethod),Ink);
            DrawConnectionChoice(g,ConnectionProtocolButton,ConnectionUi.ProtocolName(Transport),Transport==DnsTransport.Plain?(darkTheme?Color.FromArgb(236,176,64):Color.FromArgb(155,92,0)):Accent);
            using(var font=UiFont(fMed,8f))using(var ink=new SolidBrush(Muted))using(var format=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
                g.DrawString(ConnectionCardHint,font,ink,new RectangleF(rSecureOption.X+12,rSecureOption.Top+55,rSecureOption.Width-24,16),format);
            if(HasLegacyAntigravityState){
            using (var path = RoundPath(rAgOption, 16))
            using (var fill = new SolidBrush(CardBg))
            using (var line = new Pen(CardLine))
            { g.FillPath(fill, path); g.DrawPath(line, path); }
            bool sharedAntigravity = AntigravityCardInfoOnly;
            if (!sharedAntigravity) {
                var check = new Rectangle(46, LowerY(771), 20, 20);
                using (var path = RoundPath(check, 5))
                using (var fill = new SolidBrush(agStatus.Checked ? ActiveBg : Bg))
                using (var line = new Pen(Accent, 1.5f))
                { g.FillPath(fill, path); g.DrawPath(line, path); }
            }
            if (agStatus.Checked)
                using (var pen = new Pen(ContrastInk, 2f))
                    g.DrawLines(pen, new[] { new Point(50, LowerY(781)), new Point(55, LowerY(786)), new Point(63, LowerY(776)) });
            using (var title = UiFont(fBold, 10f))
            using (var body = UiFont(fMed, 8.5f))
            using (var ink = new SolidBrush(Ink))
            using (var muted = new SolidBrush(Muted))
            {
                int textLeft = sharedAntigravity ? 46 : 76;
                g.DrawString(sharedAntigravity ? SharedAntigravityTitle : "Доп. DNS Antigravity", title, ink, textLeft, LowerY(764));
                string note = sharedAntigravity ? SharedAntigravityNote : agLoading ? "Проверяем настройки…" : agStatus.Error != null ? "Не удалось проверить · нажмите для повтора" :
                    agStatus.RecoveryNeeded ? "Снимите галочку: завершить восстановление" : agStatus.External ? "Есть правила другой программы" : agStatus.Checked ?
                    (agStatus.Complete ? (agStatus.Provider == 1 ? "Comss: смените DNS" : "Включено · " + (agStatus.Provider >= 0 ? AntigravityDns.ProviderNames[agStatus.Provider] : "DNS")) +
                        (agStatus.Provider != AntigravityDns.ProviderForMainSet(dnsSet) ? " · смена после отключения" : "") : "Включено частично · снимите для отмены") :
                    (cleanupDone ? "Старые правила удалены" : secureDns ? (tlsDns ? "С шифрованием — через общий TLS" : "С шифрованием — через общий HTTPS") : PlainAntigravityNote);
                bool showCleanup = agStatus.External;
                using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString(string.IsNullOrEmpty(note)?"Может помочь при ошибке 400 в Antigravity":note, body, muted, new RectangleF(textLeft, LowerY(788), showCleanup ? W-242 : W-textLeft-44, 22), format);
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

            }else if(!googleMode){
                using(var path=RoundPath(rAgOption,16))using(var fill=new SolidBrush(CardBg))using(var line=new Pen(CardLine)){g.FillPath(fill,path);g.DrawPath(line,path);}
                var check=new Rectangle(46,LowerY(770),20,20);
                bool agChecked=localMode&&localAntigravity;
                using(var path=RoundPath(check,5))using(var fill=new SolidBrush(agChecked?Accent:CardBg))using(var line=new Pen((ManagedDns.Running||!localMode&&active)?Muted:Accent,1.4f)){g.FillPath(fill,path);g.DrawPath(line,path);}
                if(agChecked)using(var line=new Pen(ContrastInk,2f))g.DrawLines(line,new[]{new Point(50,LowerY(780)),new Point(55,LowerY(785)),new Point(63,LowerY(775))});
                using(var font=UiFont(fBold,9.5f))using(var ink=new SolidBrush(Ink))g.DrawString("Antigravity · отдельно",font,ink,76,LowerY(764));
                using(var path=RoundPath(LocalAntigravityProviderButton,7))using(var fill=new SolidBrush(CardLine))g.FillPath(fill,path);
                using(var font=UiFont(fMed,8.5f))using(var ink=new SolidBrush(busy?Muted:Soft))using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})g.DrawString((LocalDnsRouting.Supports(localAntigravityProvider)?Dns.Name[localAntigravityProvider]:"Недоступен")+" ›",font,ink,LocalAntigravityProviderButton,format);
                string agProviderName=LocalDnsRouting.Supports(localAntigravityProvider)?Dns.Name[localAntigravityProvider]:"DNS-AI";
                string agNote=agAutoDns&&localMode&&localAntigravity?agAutoStatus:pendingAntigravityProvider>=0?"Проверяем "+Dns.Name[pendingAntigravityProvider]+" · прежний DNS продолжает работать":!localMode?(active?"Сначала выключите DNS, чтобы включить маршрут "+agProviderName:"Включит способ 2 (DoH) · маршрут через "+agProviderName):(localAntigravity?(active?"Работает через "+agProviderName+" ("+(localAntigravityTls?"DoT":"DoH")+") · основной: "+Dns.Name[dnsSet]:"Готово ("+agProviderName+") · нажмите большую кнопку ВКЛ"):"Выключено · нажмите галочку для маршрута "+agProviderName);
                using(var font=UiFont(fMed,8f))using(var ink=new SolidBrush(Muted))using(var format=new StringFormat{FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter})g.DrawString(agNote,font,ink,new RectangleF(76,LowerY(789),W-104,20),format);
            }else{
                using(var path=RoundPath(rAgOption,16))using(var fill=new SolidBrush(CardBg))using(var border=new Pen(CardLine)){g.FillPath(fill,path);g.DrawPath(border,path);}
                using(var title=UiFont(fBold,10f))using(var body=UiFont(fMed,8.5f))using(var ink=new SolidBrush(Ink))using(var muted=new SolidBrush(Muted)){
                    g.DrawString(localMode?"Локальный DNS · совместимость":"DNS-туннель · beta",title,ink,46,LowerY(764));
                    g.DrawString(localMode?"При выходе прежние DNS восстанавливаются":"Не VPN · внешний IP не меняется · DNS всего ПК",body,muted,46,LowerY(788));
                }
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
            Color toggleAccent = ConnectionWarning ? Amber : ActiveBg;
            using (var b = new SolidBrush(Mix(CardBg, toggleAccent, powerMix))) g.FillEllipse(b, r);
            using (var pen = new Pen(Mix(CardLine, toggleAccent, Math.Max(powerMix, hoverMix)), 2f)) g.DrawEllipse(pen, r);
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

        void DrawCheckerStatus(Graphics g)
        {
            var bounds=CheckerStatusBounds;Color color=CheckerConfirmed?(darkTheme?Color.FromArgb(73,207,148):Color.FromArgb(20,128,80)):CheckerWarning?WarningColor:Muted;
            using(var font=UiFont(fMed,9f))using(var brush=new SolidBrush(color))using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter})
                g.DrawString(CheckerCaption,font,brush,bounds,format);
        }
        internal float ConnectionStatusFontSize(Graphics graphics)
        {
            float size=busy?12f:15f;
            while(size>10f){using(var font=UiFont(fHead,size))if(graphics.MeasureString(ConnectionStatusCaption,font).Width<=layoutWidth-60)break;size-=0.5f;}
            return size;
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
            if(active&&!busy&&LatencyStatusBounds.Contains(e.Location)){StartConnectionLatency();return;}
            if(ConnectionProtocolButton.Contains(e.Location)){ShowConnectionProtocolMenu();return;}
            if(ConnectionModeButton.Contains(e.Location)){ShowConnectionMenu();return;}
            if (rAgCleanup.Contains(e.Location) && agStatus.External&&!ManagedMode) { if (busy || agLoading) return; RemoveOldAntigravity(); return; }
            if(!googleMode&&!HasLegacyAntigravityState&&rAgOption.Contains(e.Location)){
                if(LocalAntigravityProviderButton.Contains(e.Location)){var choices=LocalDnsRouting.Providers.Where(UiProviders.Contains).ToArray();if(choices.Length>0){int next=choices[(Array.IndexOf(choices,localAntigravityProvider)+1)%choices.Length];if(!localMode)EnableAntigravityAppMode(next,true);else SelectLocalAntigravity(localAntigravity,next);}}
                else if(!localMode)EnableAntigravityAppMode(localAntigravityProvider,true);
                else SelectLocalAntigravity(!localAntigravity,localAntigravityProvider);return;
            }
            if (providerPrevious.Contains(e.Location)) { ScrollProviderPage(-1); return; }
            if (providerNext.Contains(e.Location)) { ScrollProviderPage(1); return; }
            if (DnsTestButton.Contains(e.Location)) { if(googleMode&&resolverResult==null)CheckGoogleServices();else ShowDnsDiagnostics(); return; }
            if (rAgCleanup.Contains(e.Location) && agStatus.External&&!ManagedMode) {
                if (busy || agLoading) return;
                RemoveOldAntigravity();
                return;
            }
            if (rAgOption.Contains(e.Location)) { if(!ManagedMode&&!ManagedDns.Running)ToggleAntigravity(); return; }
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
            bool hand = active&&LatencyStatusBounds.Contains(e.Location) || rSettings.Contains(e.Location) || rVersion.Contains(e.Location) || (ConnectionModeButton.Contains(e.Location)||ConnectionProtocolButton.Contains(e.Location)) || (rAgOption.Contains(e.Location) && (!googleMode&&!HasLegacyAntigravityState||!AntigravityCardInfoOnly)) || rTheme.Contains(e.Location) || rClose.Contains(e.Location) || rGroup.Contains(e.Location) || rSite.Contains(e.Location) || rBrand.Contains(e.Location) || ProviderBar.Contains(e.Location) || DnsTestButton.Contains(e.Location)
                        || dx * dx + dy * dy <= rToggle.Width * rToggle.Width / 4.0;
            Cursor = hand && !busy ? Cursors.Hand : Cursors.Default;
            string tip = LatencyStatusBounds.Contains(e.Location)&&active?ConnectionLatencyTip:rSite.Contains(e.Location)?"Информация и настройка "+Dns.Name[dnsSet]+"\n"+Dns.Url[dnsSet]:CheckerStatusBounds.Contains(e.Location)?CheckerTip:!googleMode&&!HasLegacyAntigravityState&&rAgOption.Contains(e.Location)?(!localMode?"Нажмите галочку: программа автоматически переключится в способ «2 · Через приложение (DoH)» с маршрутом Antigravity через "+Dns.Name[localAntigravityProvider]+".":ManagedDns.Running?"Нажмите справа для смены DNS Antigravity без отключения основного DNS. Новый сервер сначала проверяется.":"Галочка включает отдельный DoH для API Antigravity ("+Dns.Name[localAntigravityProvider]+"); справа можно сменить провайдера."):ConnectionProtocolButton.Contains(e.Location)?(ConnectionChangeLocked?"Сначала выключите DNS для смены шифрования":"Шифрование: "+ConnectionUi.ProtocolName(Transport)+". Нажмите для выбора HTTPS, TLS или обычного DNS."):ConnectionModeButton.Contains(e.Location) ? (ConnectionChangeLocked?"Для смены способа сначала выключите DNS":ConnectionHint+". Нажмите для выбора способа подключения.") : rSettings.Contains(e.Location)?"Настройки":providerPrevious.Contains(e.Location)?"Предыдущие 3 варианта":providerNext.Contains(e.Location)?"Следующие 3 варианта":rVersion.Contains(e.Location)?(availableUpdate==null?"Проверить обновления":"Доступна новая версия "+availableUpdate.Version):rTheme.Contains(e.Location) ? (darkTheme ? "Светлая тема" : "Тёмная тема") : null;
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
                    "Выбранный DNS: " + Dns.Name[dnsSet] + "\nПротокол: " + Transport + "; encrypted-only: " + Dns.EncryptedOnly(dnsSet) +
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
            if (secureDns || Dns.EncryptedOnly(dnsSet)) { probeLabels = Enumerable.Repeat((tlsDns ? "Режим TLS · тест через TLS" : "Режим HTTPS · тест через HTTPS"), Dns.Name.Length).ToArray(); Invalidate(); return; }
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
            using (var cancellation=new CancellationTokenSource())
            using (var dialog = CreateDialog("Выберите DNS и тип проверки. Проверяется доступность сервера; вход в аккаунт и ответ модели не проверяются.",
                "DNS и доступность", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                Math.Min(uiScale,(Screen.FromControl(this).WorkingArea.Height-32f)/520f)))
            {
                var choices=UiProviders;int selected=Array.IndexOf(choices,dnsSet);
                dialog.AddDnsTestControls(choices.Select(p=>Dns.Name[p]).ToArray(),Math.Max(0,selected), index => RunDnsTest(dialog, choices[index],cancellation.Token));
                dialog.AddServiceTestControls(defaultGoal>=0&&defaultGoal<4?defaultGoal:0);
                if(resolverResult!=null&&ResolverResultCurrent){dialog.SelectResolverTest();ShowResolverResult(dialog,resolverResult);}
                dialog.SetPrimaryText("Закрыть");
                dialog.FormClosed+=(s,e)=>cancellation.Cancel();
                dialog.ShowDialog(this);
            }
        }
        void ShowAutoDns(bool startImmediately=false)
        {
            if (!CanOpenAutoDns) return;
            bool localSearch=localMode,liveLocal=localMode&&active&&ManagedDns.Connected;var allowedProviders=UiProviders;
            int liveRevision=resolverRevision,liveProvider=dnsSet;var liveProtocol=Transport;
            string connectionDiagnostic=localSearch?"Connection=LocalResolver; encrypted queries only; activation=local worker":"Connection=Windows";
            string localNote=liveLocal?"\n\nТекущий DNS продолжает работать во время подбора. Выбранный вариант будет включён через тот же обработчик после проверки; при ошибке останется прежний DNS.":localSearch?"\n\nСпособ подключения 2: DNS будет запущен через приложение с шифрованием. Программа должна оставаться открытой.":"";
            if(localSearch&&localAntigravity)localNote+="\nОтдельный Antigravity сохраняет провайдера "+Dns.Name[localAntigravityProvider]+"; Авто подбирает основной DNS.";
            AutoDnsResult chosen = null; int chosenGoal = -1; bool running = false;
            using (var cancellation = new CancellationTokenSource())
            using (var dialog = CreateDialog("Выберите сервис и нажмите «Подобрать DNS».",
                "Авто-подключение · beta", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                Math.Min(uiScale,(Screen.FromControl(this).WorkingArea.Height-32f)/640f)))
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
                    bool antigravityTarget=goal==AutoDns.GoalIndex("Antigravity");
                    bool encrypted = antigravityTarget||localSearch||secureDns; bool tls = !antigravityTarget&&tlsDns;
                    dialog.ShowAutoDnsStatus("Проверяем сеть и подходящие DNS…", "Подбираем для " + AutoDns.Goals[goal].Name, true, false);
                    ThreadPool.QueueUserWorkItem(_ => {
                        AutoDnsResult[] results = null; string failure = null, failureDetail=null;
                        try {
                            if (!NetworkCheck.HasConnection()) throw new IOException(NetworkCheck.OfflineMessage);
                            if (VpnCheck.Scan().NeedsAttention) throw new IOException("Отключите VPN и повторите подбор.");
                            if (AutoDns.Candidates(goal, encrypted, tls,allowedProviders).Length == 0) throw new IOException("Среди видимых провайдеров нет подходящего DNS. Измените список в настройках, цель или шифрование.");
                            int completed=0,total=AutoDns.Candidates(goal,encrypted,tls,allowedProviders).Length;
                            results = AutoDns.Search(goal, encrypted, new AutoDnsProbe(tls), token, result => {
                                int done=Interlocked.Increment(ref completed);
                                if(token.IsCancellationRequested||dialog.IsDisposed||IsDisposed)return;
                                try {BeginInvoke((Action)(()=> {
                                    if(!token.IsCancellationRequested&&!dialog.IsDisposed&&running)
                                        dialog.ShowAutoDnsStatus("Проверено "+done+" из "+total+" вариантов.\n"+(liveLocal?"Текущий DNS продолжает работать.":"Настройки пока не меняем."),"Подбираем DNS",true,false);
                                }));}catch(InvalidOperationException){}
                            }, null, tls,allowedProviders);
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
                            if(liveLocal&&!LiveAutoContextCurrent(liveRevision,liveProvider,liveProtocol)){
                                chosen=null;chosenGoal=-1;dialog.ShowAutoDnsResult("Подключение изменилось","Закройте это окно и повторите Авто после включения DNS.","Результат подбора не применялся к изменившемуся подключению.");return;
                            }
                            if (failure != null) {
                                string hint=failure.Contains("VPN")?"Отключите VPN и повторите подбор.":failure==NetworkCheck.OfflineMessage?"Подключитесь к Wi-Fi или Ethernet.":"Повторите подбор или выберите DNS вручную.";
                                dialog.ShowAutoDnsResult("Не удалось завершить подбор",hint,failure);
                                dialog.SetDiagnosticDetails(connectionDiagnostic+"\nGoal="+AutoDns.Goals[goal].Name+"; DoH="+encrypted+"\n"+failureDetail+(results==null?"":"\n"+AutoDns.Diagnostic(goal,results)),DiagnosticReport.ShouldOffer("Авто",failure));return;
                            }
                            var best = AutoDns.Best(results);
                            string report = AutoDns.Report(goal, results)+(antigravityTarget?"\n\nБудет включена галочка «Antigravity · отдельно» (DoH). В способе 2 основной DNS сохранится. При переходе со способа 1 прежние настройки сначала восстановятся, затем запустится способ 2. После применения можно перезапустить Antigravity.":localNote);
                            if (best == null) { dialog.ShowAutoDnsResult("Нет подходящего варианта","Попробуйте позже или выберите DNS вручную.\nНастройки не изменены.",report); dialog.SetDiagnosticDetails(connectionDiagnostic+"\n"+AutoDns.Diagnostic(goal,results),true); return; }
                            dialog.ShowAutoDnsResult(Dns.Name[best.Provider]+" для "+AutoDns.Goals[goal].Name,
                                best.Availability!=null?best.Summary:best.NeedsBrowser?"Требуется проверка в браузере.":"Сервер доступен.\nМожно подключить выбранный DNS.",
                                report,(liveLocal?"Переключить на ":"Подключить ")+Dns.Name[best.Provider]);
                            dialog.SetDiagnosticDetails(connectionDiagnostic+"\n"+AutoDns.Diagnostic(goal,results),false);
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
            if(chosenGoal==AutoDns.GoalIndex("Antigravity")&&!liveLocal){if(!StartAntigravityAuto(chosen)){error=error??"Не удалось включить отдельный маршрут Antigravity. Повторите Авто.";Invalidate();}return;}
            if(localSearch){if(liveLocal){if(!SwitchLocalAuto(chosen,chosenGoal,liveRevision,liveProvider,liveProtocol)){error=error??"Подключение изменилось. Повторите Авто.";Invalidate();}}else StartLocalAuto(chosen,chosenGoal);return;}
            Apply(true, chosen.Provider, false, chosen.Encrypted, chosenGoal,chosen.Encrypted?(chosen.Tls?DnsTransport.Tls:DnsTransport.Https):DnsTransport.Plain);
        }

        void CancelConnectionLatency()
        {
            latencyRevision++;var previous=latencyCancel;latencyCancel=null;
            if(previous!=null)try{previous.Cancel();}catch(ObjectDisposedException){}
            latencyChecking=false;connectionLatency=null;latencyProvider=-1;latencyStamp=null;
        }
        void RefreshConnectionLatency()
        {
            if(!profilePreferences||!Visible||WindowState==FormWindowState.Minimized||!active||busy||initializing||latencyChecking||frameClock.ElapsedMilliseconds<nextLatencyAt)return;
            StartConnectionLatency();
        }
        internal bool AcceptConnectionLatencyResult(ProviderLatency result,int revision)
        {
            if(result==null||revision!=latencyRevision||!LatencyResultCurrent||result.Provider!=dnsSet||result.Protocol!=Transport)return false;
            latencyChecking=false;latencyCancel=null;connectionLatency=result;Invalidate();return true;
        }
        void StartConnectionLatency()
        {
            if(IsDisposed||Disposing||!profilePreferences||!active||busy||initializing||!IsHandleCreated||latencyChecking&&LatencyResultCurrent)return;
            CancelConnectionLatency();int revision=latencyRevision,provider=dnsSet;var protocol=Transport;bool local=localMode,google=googleMode;
            latencyProvider=provider;latencyTransport=protocol;latencyLocal=local;latencyGoogle=google;latencyChecking=true;nextLatencyAt=frameClock.ElapsedMilliseconds+30000;
            string stamp=null;try{stamp=new MangoResolverCheckIo().Stamp();}catch(Exception){}latencyStamp=stamp;
            var lifetime=CancellationTokenSource.CreateLinkedTokenSource(autoLifetime.Token);latencyCancel=lifetime;lifetime.CancelAfter(15000);var token=lifetime.Token;Invalidate();
            ThreadPool.QueueUserWorkItem(_=>{
                ProviderLatency result;var vpn=VpnCheck.Scan();
                try{result=ProviderLatency.MeasureActive(provider,protocol,local,google,token);if(stamp!=null&&stamp!=new MangoResolverCheckIo().Stamp())result.Error="NetworkChanged";}
                catch(OperationCanceledException){result=new ProviderLatency{Provider=provider,Protocol=protocol,Error="DeadlineOrCancelled",MeasuredUtc=DateTime.UtcNow};}
                catch(Exception error){result=new ProviderLatency{Provider=provider,Protocol=protocol,Error=error.GetType().Name,MeasuredUtc=DateTime.UtcNow};}
                finally{lifetime.Dispose();}
                if(IsDisposed||Disposing)return;
                try{BeginInvoke((Action)(()=>{UpdateVpnStatus(vpn);if(AcceptConnectionLatencyResult(result,revision))DiagnosticReport.Record("Пинг DNS","Provider="+Dns.Name[provider]+"; protocol="+protocol+"; local="+local+"; google="+google+"; ms="+result.Milliseconds+"; error="+result.Error+"; query=example.com; network settings unchanged");else if(revision==latencyRevision){latencyChecking=false;latencyCancel=null;}}));}catch(InvalidOperationException){}
            });
        }
        void CancelResolverCheck()
        {
            resolverRevision++;var previous=resolverCancel;resolverCancel=null;
            if(previous!=null)try{previous.Cancel();}catch(ObjectDisposedException){}
            resolverChecking=false;resolverResult=null;resolverProvider=-1;resolverStamp=null;
        }
        void StartResolverCheck()
        {
            CancelResolverCheck();
            StartConnectionLatency();
            // The alternate constructor is for deterministic UI tests: no external requests.
            if(!profilePreferences||!autoResolverCheck||!active||busy||!IsHandleCreated)return;
            int revision=resolverRevision,provider=dnsSet;var protocol=Transport;
            resolverProvider=provider;resolverTransport=protocol;resolverGoogle=googleMode;resolverLocal=localMode;resolverChecking=true;
            try{resolverStamp=new MangoResolverCheckIo().Stamp();}catch(Exception){}
            var lifetime=CancellationTokenSource.CreateLinkedTokenSource(autoLifetime.Token);resolverCancel=lifetime;lifetime.CancelAfter(35000);Invalidate();
            ThreadPool.QueueUserWorkItem(_=>{
                ResolverCheckResult result;
                try{result=ObserveResolvers(provider,protocol,lifetime.Token);}
                catch(OperationCanceledException){result=new ResolverCheckResult{Provider=Dns.Name[provider],Protocol=protocol.ToString(),Detail="Observer deadline reached or operation cancelled"};}
                catch(Exception problem){result=new ResolverCheckResult{Provider=Dns.Name[provider],Protocol=protocol.ToString(),Detail="Observer failure: "+problem.GetType().Name};}
                finally{lifetime.Dispose();}
                if(IsDisposed||Disposing)return;
                try{BeginInvoke((Action)(()=>{
                    if(revision!=resolverRevision||!ResolverResultCurrent)return;
                    resolverCancel=null;resolverChecking=false;resolverResult=result;
                    DiagnosticReport.Record("Фактический DNS",result.Report+"\n"+result.Detail);Invalidate();
                }));}catch(InvalidOperationException){}
            });
        }
        static ResolverCheckResult ObserveResolvers(int provider,DnsTransport protocol,CancellationToken cancel)
        {
            var result=ProviderCheck.Available(provider)?new ResolverCheckResult{Provider=Dns.Name[provider],Protocol=protocol.ToString(),Official=ProviderCheck.Run(provider,new ProviderCheckIo(),cancel)}:ResolverCheck.Run(provider,protocol,new MangoResolverCheckIo(),cancel);
            if(result.Official!=null)result.Detail=result.Official.Detail;
            var vpn=VpnCheck.Scan();result.Detail+="\n"+vpn.Label+"\n"+vpn.Details;return result;
        }
        static void ShowResolverResult(StyledDialog dialog,ResolverCheckResult result)
        {
            bool warning=result.Official==null?result.Comparison==ResolverComparison.NetworkChanged:result.Official.State==ProviderCheckState.NotConfirmed||result.Official.State==ProviderCheckState.NetworkChanged;
            bool confirmed=result.Official!=null&&result.Official.State==ProviderCheckState.Confirmed;
            dialog.ShowDnsTestStatus(result.Report,result.Official==null?"Наблюдение DNS · без подтверждения провайдера":"Официальный чекер · "+result.Provider,false,warning,result.Summary+(result.Official==null?"\nСистемных узлов: "+result.SystemNodes.Length+" · контрольных: "+result.ProviderNodes.Length:"\n"+result.Official.Source)+"\nDoH браузера проверяется отдельно.",true,!confirmed);
            dialog.SetDiagnosticDetails(result.Detail,false);
        }
        internal bool AcceptManualResolverResult(int provider,DnsTransport protocol,int revision,ResolverCheckResult result)
        {
            if(result==null||revision!=resolverRevision||!active||busy||provider!=dnsSet||protocol!=Transport)return false;
            resolverProvider=provider;resolverTransport=protocol;resolverGoogle=googleMode;resolverLocal=localMode;resolverChecking=false;resolverResult=result;
            try{resolverStamp=new MangoResolverCheckIo().Stamp();}catch(Exception){resolverStamp=null;}
            Invalidate();return true;
        }
        void RunResolverTest(StyledDialog dialog,int provider,CancellationToken cancel)
        {
            if(resolverChecking)CancelResolverCheck();
            var protocol=(secureDns||Dns.EncryptedOnly(provider))?(tlsDns?DnsTransport.Tls:DnsTransport.Https):DnsTransport.Plain;
            dialog.ShowDnsTestStatus(ProviderCheck.Available(provider)?"Официальный чекер провайдера проверяет текущий системный путь Windows. Настройки не меняются.":"Наблюдатель MangoProxy проверяет уникальные тестовые имена. Сравниваем системный путь и выбранный DNS. Настройки не меняются.","Проверяем применение DNS…",true,false);
            int revision=resolverRevision;
            ThreadPool.QueueUserWorkItem(_=>{
                ResolverCheckResult result;
                using(var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancel,autoLifetime.Token)){
                    lifetime.CancelAfter(35000);
                    try{result=ObserveResolvers(provider,protocol,lifetime.Token);}
                    catch(OperationCanceledException){if(cancel.IsCancellationRequested||autoLifetime.IsCancellationRequested)return;result=new ResolverCheckResult{Provider=Dns.Name[provider],Protocol=protocol.ToString(),Detail="Observer deadline reached"};}
                    catch(Exception problem){result=new ResolverCheckResult{Provider=Dns.Name[provider],Protocol=protocol.ToString(),Detail="Observer failure: "+problem.GetType().Name};}
                }
                if(cancel.IsCancellationRequested||dialog.IsDisposed||IsDisposed)return;
                try{BeginInvoke((Action)(()=>{
                    if(cancel.IsCancellationRequested||dialog.IsDisposed||IsDisposed)return;
                    if(revision!=resolverRevision){dialog.ShowDnsTestStatus("Настройки подключения изменились. Повторите проверку.","Результат устарел",false,true);return;}
                    AcceptManualResolverResult(provider,protocol,revision,result);
                    DiagnosticReport.Record("Фактический DNS · вручную",result.Report+"\n"+result.Detail);ShowResolverResult(dialog,result);
                }));}catch(InvalidOperationException){}
            });
        }
        void RunDnsTest(StyledDialog dialog, int provider, CancellationToken cancel)
        {
            if(dialog.TestService==ResolverCheck.ServiceIndex){RunResolverTest(dialog,provider,cancel);return;}
            bool encrypted = secureDns || Dns.EncryptedOnly(provider); bool tls = encrypted && tlsDns;
            int service=dialog.TestService;
            if (encrypted && (tls ? !DotDns.Available(provider) : EncryptedDns.Templates[provider] == null)) { dialog.ShowDnsTestStatus("Для этого провайдера HTTPS-тест пока недоступен. Выберите Comss.one, Bezmezhau, DNS-AI или MagicDNS. Обычные DNS-запросы не отправлялись.", "Нет параметров DoH", false, true); return; }
            if (!NetworkCheck.HasConnection()) { dialog.ShowDnsTestStatus(NetworkCheck.OfflineMessage, "Нет подключения", false, true); return; }
            var vpn = VpnCheck.Scan();
            if (vpn.NeedsAttention)
            {
                dialog.ShowDnsTestStatus("Отключите VPN и нажмите «Проверить» ещё раз. VPN может перехватывать запросы к любому серверу.\n\n" + vpn.Details,
                    "Сначала отключите VPN", false, true); return;
            }
            dialog.ShowDnsTestStatus(service>=0?"Проверяем DNS, TLS и ответы "+ServiceAvailability.Targets[service].Name+"…":"Проверяем серверы " + Dns.Name[provider] + " и домены сервисов…", "Проверяем " + Dns.Name[provider], true, false);
            DohHttp2.ResetAddressHealth();
            ThreadPool.QueueUserWorkItem(_ => {
                DnsProbeResult[] results = null; ServiceAvailabilityResult availability=null; string failure = null;
                try {
                    if(service>=0)availability=ServiceAvailability.Check(service,provider,encrypted,new ServiceAvailabilityProbe(tls),cancel);
                    else results = encrypted ? DnsProbe.HostsForProvider(provider).Select(host => {cancel.ThrowIfCancellationRequested();return tls ? DotDns.Check(provider,host) : DnsProbe.CheckHttps(EncryptedDns.Templates[provider], host);}).ToArray() : DnsProbe.CheckProvider(provider);
                }
                catch(OperationCanceledException){return;}
                catch (Exception ex) { failure = ex.Message; }
                if (cancel.IsCancellationRequested || IsDisposed || Disposing) return;
                try { BeginInvoke((Action)(() => {
                    if (cancel.IsCancellationRequested || dialog.IsDisposed || dialog.Disposing) return;
                    if (failure != null) { dialog.ShowDnsTestStatus(failure, "Не удалось проверить", false, true); return; }
                    if (VpnCheck.Scan().NeedsAttention) { dialog.ShowDnsTestStatus("Во время теста обнаружен VPN. Отключите его и повторите проверку.", "Результат недостоверен", false, true); return; }
                    if(availability!=null) {
                        dialog.ShowDnsTestStatus(availability.Report,availability.Name+" · без ключа",false,availability.Attention,availability.Summary);
                        dialog.SetDiagnosticDetails("Transport="+(encrypted?(tls?"TLS":"HTTPS"):"DNS")+"\n"+availability.Diagnostic,availability.OfferReport);
                        return;
                    }
                    probeResults[provider] = results;
                    int count = results.Count(r => r.Host == "example.com" && r.Ok);
                    probeLabels[provider] = encrypted ? (tls ? "DoT: " : "DoH: ") + results.Count(r => r.Ok) + "/" + results.Length + " ответов" : "Ответы DNS: " + count + "/"+Dns.V4[provider].Length;
                    string report = encrypted ? (tls ? "TLS: " + DotDns.Host(provider) + ":853" : "HTTPS: " + EncryptedDns.Templates[provider]) + "\n\n" + string.Join("\n", results.Select(r => r.Host + " — " + (r.Ok ? r.Milliseconds + " мс" : r.Error))) + "\n\nЭто проверка сервера по выбранному протоколу, не DNS браузера. Системные настройки не изменялись." : DnsProbe.Report(provider, results);
                    dialog.ShowDnsTestStatus(report, (encrypted ? (tls ? "TLS-ответы: " : "HTTPS-ответы: ") : "Прямые ответы: ") + results.Count(r => r.Ok) + "/" + results.Length, false, results.Any(r => !r.Ok), (results.Any(r=>!r.Ok)?"Часть запросов завершилась с ошибкой.":"Ответы получены на все запросы.")+"\nПрименение DNS на ПК и доступ к сервису не проверялись."+(!encrypted?"\nОбычные запросы могут перенаправляться по пути.":""),true,true);
                    dialog.SetDiagnosticDetails(string.Join("\n\n",results.Select(r=>r.Server+"; host="+r.Host+"; elapsed="+r.Milliseconds+" ms; error="+r.Error+"\n"+r.Detail)),results.Any(r=>!r.Ok));
                    Invalidate();
                })); } catch (InvalidOperationException) { }
            });
        }
        void PickSet(int k)
        {
            if(!UiProviders.Contains(k))return;
            if(localMode&&ManagedDns.Connected&&!ManagedDns.Stopping&&!busy){SwitchLocalProvider(k);return;}
            if(ManagedMode){if(busy||ManagedDns.Running||!(localMode?TunnelSession.EncryptedProvider(k):TunnelSession.GoogleProvider(k)))return;manualSelection=true;autoGoal=-1;dnsSet=k;secureDns=true;tlsDns=!localMode&&k!=6&&DotDns.Available(k);SaveGooglePreference();EnsureProviderVisible();Invalidate();return;}
            if (busy || (k == dnsSet && autoGoal < 0&&!UseDefaultGoal)) return;
            manualSelection=true;
            if (active) Apply(true, k, encrypted: EncryptedDns.Templates[k] != null && secureDns || Dns.EncryptedOnly(k), transport: tlsDns && DotDns.Available(k) ? DnsTransport.Tls : secureDns || Dns.EncryptedOnly(k) ? DnsTransport.Https : DnsTransport.Plain);
            else { autoGoal = -1; dnsSet = k; if (!DotDns.Available(k)) tlsDns = false; if (Dns.EncryptedOnly(k)) secureDns = true; else if (EncryptedDns.Templates[k] == null) secureDns = false; EnsureProviderVisible(); Invalidate(); }
        }
        internal bool SwitchLocalProvider(int provider)
        {
            var session=localSession as ILocalDnsSwitchSession;
            if(!localMode||busy||!ManagedDns.Connected||ManagedDns.Stopping||!UiProviders.Contains(provider)||!TunnelSession.EncryptedProvider(provider)||session==null)return false;
            if(provider==dnsSet){manualSelection=true;autoGoal=-1;Invalidate();return true;}
            ResetAgHealthWatch();
            CancelResolverCheck();CancelConnectionLatency();error=null;manualSelection=true;autoGoal=-1;
            try{session.SwitchProvider(provider,tlsDns&&DotDns.Available(provider));busy=true;DiagnosticReport.Record("Смена локального DNS","Requested="+Dns.Name[provider]+"; previous="+Dns.Name[dnsSet]+"; backup unchanged; separate Antigravity unchanged");}
            catch(Exception problem){error=TunnelDiagnostics.Error(problem);busy=false;Invalidate();return false;}
            Invalidate();return true;
        }

        void Toggle()
        {
            if(localMode&&!active&&!ManagedDns.Running&&!busy&&(autoGoal>=0||UseDefaultGoal)){ShowAutoDns(true);return;}
            if(ManagedMode){ToggleGoogleTunnel();return;}
            if (busy) return;
            if (!active && (autoGoal >= 0||UseDefaultGoal)) { ShowAutoDns(true); return; }
            bool resetKnown;
            try { resetKnown = active && Dns.NeedsKnownReset(); }
            catch (Exception ex) { ShowMessage("Не удалось проверить резервные копии: " + ex.Message, "Ошибка отключения DNS", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (resetKnown && ShowMessage(
                "Для текущего подключения нет копии прежних DNS. Копии других адаптеров не восстанавливают его настройки.\n\nПерейти на автоматическое получение DNS? " +
                "Изменятся только активные адаптеры, в которых все ручные DNS-адреса соответствующего семейства входят в список программы. " +
                "Смешанные и сторонние адреса останутся без изменений. Копии недоступных адаптеров сохранятся.\n\nЭто не восстановит неизвестные прежние ручные настройки.",
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
            if (AntigravityCardInfoOnly) return;
            bool enable = !agStatus.Checked;
            if (enable && secureDns) {
                if (agStatus.External) RemoveOldAntigravity();
                else ShowMessage(active ? "Antigravity использует общий зашифрованный DNS. Отдельная галочка DNS не нужна." : "Для шифрованного подключения Antigravity включите DNS большой кнопкой. Будет использован выбранный зашифрованный DNS без отдельных DNS-правил.", "Зашифрованный DNS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int provider = AntigravityDns.ProviderForMainSet(dnsSet);
            if (enable && agStatus.Error == null && provider < 0)
            {
                ShowMessage(Dns.EncryptedOnly(dnsSet) ? Dns.Name[dnsSet]+" используется через общий зашифрованный DNS. Включите его большой кнопкой. Отдельные DNS-правила Antigravity для него не создаются." : dnsSet == 0 ? "Comss для доп. DNS Antigravity пока отключён. Выберите другой DNS выше. Общий DNS Comss остаётся доступен." : "У выбранного DNS не подтверждена поддержка отдельного маршрута Antigravity. Выберите MagicDNS, Bezmezhau или Malw DNS.",
                    "Выберите DNS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string providerName = provider >= 0 ? AntigravityDns.ProviderNames[provider] : "DNS";
            bool force = false;
            if (enable && agStatus.External)
            {
                using (var dialog = CreateDialog("Antigravity уже настроен другой программой.\n\nЗаменить её DNS на " + providerName + "? Прежние настройки сохранятся.\n\n"+(!hideAntigravityRestart?"Запущенный Antigravity будет перезапущен. Сохраните изменения перед продолжением.":"После изменения DNS перезапустите Antigravity вручную."),
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
                if (!force && !hideAntigravityRestart && ShowMessage("Включить " + providerName + " для Antigravity?\n\nЗапущенный Antigravity будет перезапущен. Сохраните изменения перед продолжением.\n\nСнимите галочку, чтобы отменить DNS-настройку.",
                    "Доп. DNS Antigravity", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            bool readOnly = agStatus.Error != null;
            bool restartAllowed=!hideAntigravityRestart;
            if (!readOnly) DiagnosticReport.Record("Antigravity", "enable=" + enable + "; provider=" + providerName + "; force=" + force);
            operationRevision++; busy = true; agLoading = true; Invalidate();
            ThreadPool.QueueUserWorkItem(_ => {
                string err = null;
                try { if (!readOnly) err = DnsElevation.Run(enable, enable ? (force ? 101 : 100) + provider * 2 : 2); }
                catch (Exception e) { err = e.Message; }
                var status = AntigravityDns.GetStatus();
                if (!readOnly && enable && err == null && status.Error == null && (!status.Complete || status.Provider != provider))
                    err = "Windows не подтвердила выбранные DNS-настройки Antigravity. Перезапуск не выполнялся.";
                if (AntigravityRestart.Ready(enable, readOnly, err, status, provider,restartAllowed))
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

        void Apply(bool turnOn, int? selectedSet = null, bool resetKnown = false, bool? encrypted = null, int? autoTarget = null, DnsTransport? transport = null,Action afterRestore=null)
        {
            if(ManagedMode||ManagedDns.Running)return;
            int requestedSet = selectedSet ?? dnsSet;
            if(turnOn&&!Dns.IsAvailable(requestedSet)){ShowMessage("Этот провайдер временно скрыт. Выберите другой DNS.","Провайдер недоступен",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            bool useTls = (transport ?? Transport) == DnsTransport.Tls;
            bool useEncrypted = (encrypted ?? ((transport ?? Transport) != DnsTransport.Plain)) || Dns.EncryptedOnly(requestedSet);
            useTls = useTls && useEncrypted;
            if (turnOn && useEncrypted && (useTls ? !DotDns.Available(requestedSet) : EncryptedDns.Templates[requestedSet] == null)) { ShowMessage("У этого провайдера нет настроек выбранного протокола. Выберите другой способ подключения.", "Протокол недоступен", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
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
                        string conflict = EncryptedDns.PolicyConflict(policies);
                        if (EncryptedDns.CanKeepAntigravity(NrptRegistryReader.Read(false), policies))
                        {
                            string preserved = policies.Any(EncryptedDns.IsWindowsUpdateException) ? "Отдельные правила Antigravity и Windows Update сохранят прежние DNS." : "Два домена Antigravity продолжат использовать прежние DNS.";
                            using (var dialog = CreateDialog("Подключить зашифрованный DNS, сохранив настройки Antigravity?\n\n" + preserved + " Остальные запросы пойдут через выбранный зашифрованный DNS. IPv6 временно приостановится.", "DoH и Antigravity", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))
                            {
                                dialog.SetPrimaryText("Подключить");
                                if (dialog.ShowDialog(this) != DialogResult.Yes) return;
                            }
                            keepAntigravity = true;
                        }
                        else if (conflict != null) { ShowMessage(conflict, "Отдельные DNS-правила", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    }
                    catch (Exception ex) { ShowMessage("Не удалось проверить DNS-правила: " + ex.Message, "Ошибка DNS", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                }
            }
            CancelResolverCheck();CancelConnectionLatency();operationRevision++; busy = true; error = null; Invalidate();
            DiagnosticReport.Record("Общий DNS", "enable=" + turnOn + "; provider=" + Dns.Name[requestedSet] + "; protocol=" + (useTls ? "TLS" : useEncrypted ? "HTTPS" : "DNS"));
            int set = selectedSet ?? dnsSet;
            dnsSet = set;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;string networkDiagnostic=null;
                try {
                    DnsProbeResult encryptedProbe=null;
                    if(turnOn&&useEncrypted&&NetworkCheck.HasConnection())encryptedProbe=useTls ? DotDns.Check(set,"example.com") : DnsProbe.CheckHttps(EncryptedDns.Templates[set],"example.com");
                    if (turnOn && !NetworkCheck.HasConnection()) err = NetworkCheck.OfflineMessage;
                    else if (turnOn && useEncrypted && encryptedProbe!=null && !encryptedProbe.Ok) {
                        err = "Сервер не ответил через " + (useTls ? "TLS" : "HTTPS") + ". Обычный DNS не использовался, настройки не изменены.";
                        networkDiagnostic=(useTls?"DoT":"DoH")+" preflight; provider="+Dns.Name[set]+"; endpoint="+encryptedProbe.Server+"; host="+encryptedProbe.Host+"; elapsed="+encryptedProbe.Milliseconds+" ms\n"+encryptedProbe.Detail;
                    }
                    else if (turnOn && !useEncrypted && !DnsProbe.CheckAvailability(set).Any(r => r.Host == "example.com" && r.Ok))
                        err = NetworkCheck.HasConnection() ? "Сетевое подключение есть, но оба DNS-сервера не ответили. Возможны отсутствие интернета или блокировка запросов. Настройки не изменены. Подробнее — «Тест DNS»." : NetworkCheck.OfflineMessage;
                    else err = turnOn ? (useEncrypted ? DnsElevation.Run(true, (useTls ? (keepAntigravity ? 500 : 400) : (keepAntigravity ? 300 : 200)) + set) : Dns.Enable(set)) : resetKnown ? Dns.ResetKnown() : Dns.Disable();
                }
                catch (Exception ex) { err = ex.Message; }
                Thread.Sleep(600);
                int now = -1;
                bool pending = false;
                bool availableBackup = false, connected = NetworkCheck.HasConnection();
                try { now = Dns.ActiveSetFor(turnOn?set:dnsSet); pending = Dns.HasBackup(); availableBackup = pending && Dns.HasAvailableBackup(); }
                catch (Exception ex) { err = err ?? ex.Message; }
                if (IsDisposed || Disposing) return;
                BeginInvoke((Action)(() =>
                {
                    busy = false;
                    hasBackup = pending;
                    if (turnOn && err == null) { secureDns = useEncrypted; tlsDns = useTls; }
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
                        error = now < 0 && !availableBackup ? null : "Копия недоступного адаптера сохранена";
                        DiagnosticReport.Record("Отключение DNS", err);
                        if (now >= 0 || availableBackup) ShowMessage(err.Substring("[DNS-DEFERRED]".Length), "Восстановление отложено", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    else if (turnOn){StartResolverCheck();ShowDnsReminder();}
                    else if(afterRestore!=null){if(!active)afterRestore();else error="Не завершено восстановление DNS. Подключение Antigravity не запускалось.";}
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
            DiagnosticReport.Record("Запуск","version="+MainForm.VersionLabel+"; mode="+(args.Length==0?"main":args[0].StartsWith("--dns-tunnel",StringComparison.Ordinal)?"tunnel-worker":"maintenance"));
            AppDomain.CurrentDomain.UnhandledException+=(s,e)=>DiagnosticReport.Record("Необработанная ошибка",e.ExceptionObject.ToString());
            try { ProcessSecurity.Initialize(); }
            catch (Exception error)
            {
                if (args.Length == 0)
                    MessageBox.Show(error.Message, "Ошибка безопасного запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 3;
            }
            if (args.Length == 1 && args[0] == "--install-update") return AppUpdate.Install();
            if (args.Length == 1 && args[0] == "--dns-tunnel-ui") return DnsTunnelWindow.Run();
            if (args.Length > 0 && args[0] == "--dns-local-worker") return LocalDnsWorker.Run(args);
            if (args.Length > 0 && args[0] == "--dns-local-guard") return LocalDnsGuardian.Run(args);
            if (args.Length > 0 && args[0] == "--dns-tunnel-worker") return DnsTunnel.Worker(args);
            if (args.Length > 0 && args[0] == "--prepare-uninstall") return UninstallCleanup.Handle(args);
            if (args.Length != 0) return DnsElevation.Handle(args, Dns.ExecuteDirect);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return SingleInstance.Run(()=>new MainForm());
        }
    }
}
