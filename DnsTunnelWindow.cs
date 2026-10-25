using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HbAi.XboxDns
{
    sealed class DnsTunnelWindow : Form
    {
        readonly ComboBox provider=new ComboBox(),transport=new ComboBox();
        readonly Label status=new Label(),note=new Label();
        readonly Button start=new Button(),stop=new Button(),recover=new Button(),copy=new Button(),save=new Button();
        readonly LinkLabel check=new LinkLabel();CancellationTokenSource checkStop;bool connected;
        readonly TextBox log=new TextBox();readonly int[] providers=Dns.AvailableProviders.Where(p=>EncryptedDns.Templates[p]!=null||DotDns.Available(p)).ToArray();
        Process worker;NamedPipeServerStream pipe;StreamWriter writer;bool running,closingRequested,canClose;
        volatile bool stopping;
        internal DnsTunnelWindow():this(0){}
        internal DnsTunnelWindow(float requestedScale)
        {
            Text="Smart DNS · DNS-туннель · beta";StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.None;
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;float requested=requestedScale;
            if(requested<=0)using(var graphics=CreateGraphics())requested=graphics.DpiX/96f;
            float scale=FitScale(requested,area.Size);Func<int,int> px=n=>(int)Math.Round(n*scale);
            Font=new Font("Segoe UI",15*scale,FontStyle.Regular,GraphicsUnit.Pixel);BackColor=Color.FromArgb(18,18,27);ForeColor=Color.White;
            ClientSize=new Size(px(480),px(550));MaximizeBox=false;FormBorderStyle=FormBorderStyle.FixedDialog;
            Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
            var title=new Label{Text="DNS-туннель · beta",Font=new Font(Font.FontFamily,24*scale,FontStyle.Bold,GraphicsUnit.Pixel),Bounds=new Rectangle(px(24),px(20),px(432),px(38)),UseMnemonic=false};
            note.Text="Шифрованный DNS; внешний IP не меняется.\nWindows «Без доступа» у DNS-адаптера — нормально.\nИнтернет идёт через Wi-Fi/Ethernet.\nСначала выключите обычный DNS и другой VPN.";
            note.Bounds=new Rectangle(px(24),px(70),px(432),px(90));note.ForeColor=Color.Silver;
            provider.DropDownStyle=transport.DropDownStyle=ComboBoxStyle.DropDownList;
            provider.Items.AddRange(providers.Select(p=>Dns.Name[p]).ToArray());provider.SelectedIndex=0;
            transport.Items.AddRange(new object[]{"DoT · TLS","DoH · HTTPS"});transport.SelectedIndex=0;
            provider.Bounds=new Rectangle(px(24),px(178),px(238),px(34));transport.Bounds=new Rectangle(px(274),px(178),px(182),px(34));
            status.Text="Выключен. Настройки сети не менялись.";status.Bounds=new Rectangle(px(24),px(222),px(432),px(52));status.AutoEllipsis=true;status.ForeColor=Color.FromArgb(2,165,207);
            check.Text="Проверить Gemini / ChatGPT / Codex";check.LinkColor=check.ActiveLinkColor=status.ForeColor;check.DisabledLinkColor=Color.Gray;check.Bounds=new Rectangle(px(24),px(278),px(432),px(22));check.Enabled=false;
            start.Text="Включить";stop.Text="Выключить";stop.Enabled=false;recover.Text="Восстановить DNS";copy.Text="Копировать";save.Text="Сохранить";
            start.Bounds=new Rectangle(px(24),px(304),px(205),px(42));stop.Bounds=new Rectangle(px(250),px(304),px(206),px(42));
            recover.Bounds=new Rectangle(px(24),px(358),px(205),px(42));copy.Bounds=new Rectangle(px(250),px(358),px(99),px(42));save.Bounds=new Rectangle(px(357),px(358),px(99),px(42));
            foreach(var button in new[]{start,stop,recover,copy,save}){button.FlatStyle=FlatStyle.Flat;button.BackColor=Color.FromArgb(43,40,59);button.ForeColor=Color.White;}
            copy.Font=save.Font=new Font(Font.FontFamily,13*scale,FontStyle.Regular,GraphicsUnit.Pixel);
            log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.BackColor=BackColor;log.ForeColor=Color.Silver;log.Bounds=new Rectangle(px(24),px(416),px(432),px(108));log.MaxLength=128000;
            Controls.AddRange(new Control[]{title,note,provider,transport,status,check,start,stop,recover,copy,save,log});
            start.Click+=(s,e)=>StartTunnel();stop.Click+=(s,e)=>StopTunnel();
            recover.Click+=(s,e)=>{if(running)return;if(!DnsTunnel.Pending){status.Text="Нет незавершённого туннеля. Копии других адаптеров сохранены.";Append(status.Text);return;}string error=DnsTunnel.Recover();status.Text=error??"DNS текущего туннеля восстановлены.";Append(status.Text);};
            copy.Click+=(s,e)=>{try{Clipboard.SetText(Report());copy.Text="Готово";}catch(System.Runtime.InteropServices.ExternalException){copy.Text="Буфер занят";}};
            save.Click+=(s,e)=>{using(var dialog=new SaveFileDialog{Filter="Журнал (*.txt)|*.txt",FileName="SmartDNS-"+MainForm.VersionLabel+"-tunnel-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt"})if(dialog.ShowDialog(this)==DialogResult.OK)try{File.WriteAllText(dialog.FileName,Report(),new UTF8Encoding(true));save.Text="Готово";}catch(Exception error){Append("SAVE ERROR "+TunnelDiagnostics.Error(error));}};
            check.LinkClicked+=(s,e)=>CheckServices();
            FormClosing+=(s,e)=>{if(running&&!canClose){e.Cancel=true;closingRequested=true;StopTunnel();}};
            if(DnsTunnel.Pending){status.Text="Осталась копия предыдущего запуска. Нажмите «Восстановить DNS».";Append(status.Text);}
        }
        internal static float FitScale(float requested,Size area){return Math.Max(0.5f,Math.Min(requested,Math.Min((area.Width-40f)/480f,(area.Height-70f)/550f)));}
        string Report(){return DiagnosticReport.Scrub("Код Всего · Smart DNS "+MainForm.VersionLabel+"\nDNS-туннель beta · UTC="+DateTime.UtcNow.ToString("O")+"\n"+TunnelDiagnostics.WindowsNote+"\nЗапросы без авторизации; работа модели не подтверждается.\n\n"+log.Text);}
        void Append(string text){log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff")+" "+TunnelDiagnostics.Line(text)+Environment.NewLine);if(log.TextLength>120000)log.Text="[Older log entries removed]\r\n"+log.Text.Substring(log.TextLength-100000);}
        void Ui(Action action){if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(action);}catch(InvalidOperationException){}}
        void ControlsRunning(bool enabled){running=enabled;provider.Enabled=transport.Enabled=start.Enabled=recover.Enabled=!enabled;stop.Enabled=enabled;if(!enabled){connected=false;check.Enabled=false;if(checkStop!=null)checkStop.Cancel();}}
        void CheckServices()
        {
            if(!running||!connected||checkStop!=null)return;
            int selected=providers[provider.SelectedIndex];bool tls=transport.SelectedIndex==0;var cancel=new CancellationTokenSource();cancel.CancelAfter(90000);checkStop=cancel;check.Enabled=false;check.Text="Проверяем… результаты в журнале";
            Append("CHECK provider="+Dns.Name[selected]+"; protocol="+(tls?"DoT":"DoH")+"; no credentials or generation");
            ThreadPool.QueueUserWorkItem(_=>{
                Action<string> trace=line=>Ui(()=>Append(line));
                try {
                    var vpn=VpnCheck.Scan();trace("CHECK OS route context: other VPN candidates="+string.Join(",",vpn.Connections.Where(n=>n!="Smart DNS Tunnel beta"))+"; scan-failed="+vpn.Failed);
                    foreach(var adapter in TunnelDnsSystem.Uplinks())trace("CHECK active uplink="+adapter.NetworkInterfaceType+"; DNS="+string.Join(",",adapter.GetIPProperties().DnsAddresses.Select(ip=>ip.ToString())));
                    var upstream=new TunnelUpstream(selected,tls);var probe=new ServiceAvailabilityProbe(tls);
                    TunnelDiagnostics.Check(upstream.Query,probe.Request,trace,cancel.Token);
                }catch(OperationCanceledException){trace("CHECK cancelled or 90-second deadline reached; access not confirmed");}
                catch(Exception error){trace("CHECK ERROR "+TunnelDiagnostics.Error(error));}
                finally {Ui(()=>{if(checkStop==cancel){checkStop=null;check.Text="Проверить Gemini / ChatGPT / Codex";check.Enabled=running&&connected&&!stopping;cancel.Dispose();}});}
            });
        }
        void StartTunnel()
        {
            if(running)return;int selected=providers[provider.SelectedIndex];bool tls=transport.SelectedIndex==0;
            if(tls?!DotDns.Available(selected):EncryptedDns.Templates[selected]==null){status.Text="У провайдера нет выбранного протокола.";return;}
            if(DnsTunnel.Pending){status.Text="Сначала восстановите DNS от предыдущего запуска.";return;}
            stopping=false;connected=false;check.Enabled=false;
            ControlsRunning(true);status.Text="Проверяем сервер и создаём туннель…";Append("Start provider="+Dns.Name[selected]+"; protocol="+(tls?"TLS":"HTTPS"));
            Guid token=Guid.NewGuid();IAsyncResult pending;
            try {
                pipe=DnsTunnel.Server(token);pending=pipe.BeginWaitForConnection(null,null);
                worker=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--dns-tunnel-worker "+Process.GetCurrentProcess().Id+" "+token.ToString("N")+" "+selected+" "+(tls?"tls":"https")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)});
                if(worker==null)throw new IOException("Не удалось запустить обработчик.");
            }catch(Exception error){if(pipe!=null)pipe.Dispose();pipe=null;ControlsRunning(false);status.Text=error.Message;Append(error.ToString());return;}
            var child=worker;var connection=pipe;
            ThreadPool.QueueUserWorkItem(_=>{
                string failure=null;
                try {
                    if(!DnsTunnel.WaitForConnection(connection,pending,12000))throw new IOException("Обработчик не подключился.");
                    if(!DnsTunnel.ClientMatches(connection,child.Id))throw new IOException("Процесс обработчика не подтверждён.");
                    using(var output=new StreamWriter(connection,new UTF8Encoding(false),4096,true){AutoFlush=true})
                    using(var input=new StreamReader(connection,Encoding.UTF8,false,4096,true)) {
                        Ui(()=>{writer=output;if(stopping||closingRequested)StopTunnel();});
                        string line;while((line=input.ReadLine())!=null){if(line.Length>8192)throw new IOException("Неверный ответ обработчика.");string message=line;
                            Ui(()=>{if(message.StartsWith("STATS ",StringComparison.Ordinal)){var counts=message.Split(' ');status.Text="Туннель включён · ответов: "+(counts.Length>1?counts[1]:"?")+" · ошибок: "+(counts.Length>2?counts[2]:"?");}
                                else{Append(message);if(message.StartsWith("CONNECTED ",StringComparison.Ordinal)){connected=true;check.Enabled=checkStop==null&&!stopping;status.Text="Туннель включён: "+message.Substring(10);}if(message.StartsWith("ERROR ",StringComparison.Ordinal))status.Text=message.Substring(6);if(message=="STOPPED")status.Text="Выключен. Прежние DNS восстановлены.";}});
                        }
                    }
                }catch(Exception error){if(!stopping)failure=DiagnosticReport.Scrub(error.ToString());}
                finally {
                    connection.Dispose();child.WaitForExit();int exit=child.ExitCode;
                    // Worker death closes its adapter; recover the durable snapshot on the UI's side.
                    if(exit!=0&&DnsTunnel.Pending){string recovery=DnsTunnel.Recover();failure=recovery??"Обработчик завершился. Прежние DNS восстановлены.";}
                    string final=failure;
                    Ui(()=>{writer=null;pipe=null;worker=null;child.Dispose();ControlsRunning(false);if(final!=null){status.Text=final;Append(final);}else if(stopping){status.Text="Выключен. Прежние DNS восстановлены.";Append(status.Text);}if(closingRequested){canClose=true;Close();}});
                }
            });
        }
        void StopTunnel()
        {
            if(!running)return;stopping=true;stop.Enabled=false;check.Enabled=false;if(checkStop!=null)checkStop.Cancel();status.Text="Останавливаем туннель и восстанавливаем DNS…";
            try{if(writer!=null)writer.WriteLine("STOP");else if(pipe!=null)pipe.Dispose();}catch(IOException){}catch(ObjectDisposedException){}
        }
        internal static int Run(){if(!DnsElevation.IsAdministrator())return 5;Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new DnsTunnelWindow());return 0;}
    }
}
