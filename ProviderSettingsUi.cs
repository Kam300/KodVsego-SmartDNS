using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HbAi.XboxDns
{
    sealed partial class StyledDialog
    {
        Panel providerPanel,providerRows;Label providerHint,providerGeneralHeading;Button providerRefresh,settingsGeneralTab,settingsProvidersTab;
        Control[] settingsGeneralControls;bool providersTab;
        readonly Dictionary<int,CheckBox> providerChecks=new Dictionary<int,CheckBox>();
        readonly Dictionary<int,Label> providerPings=new Dictionary<int,Label>();
        CancellationTokenSource providerMeasurements;
        internal int[] HiddenProviders {get{return providerChecks.Where(pair=>!pair.Value.Checked).Select(pair=>pair.Key).OrderBy(p=>p).ToArray();}}
        internal void AddProviderSettingsControls(int[] hidden,DnsTransport protocol)
        {
            providerGeneralHeading=Controls.OfType<Label>().Single(label=>label.Text=="Выбор DNS");
            settingsGeneralControls=new Control[]{providerGeneralHeading,settingsConnectionPanel,settingsBackupPanel,settingsBackupHeading,settingsSupportHeading,settingsGithub,settingsTelegram,settingsSource,settingsTunnel,settingsResolverCheck,settingsRestartHeading,settingsRestartHint,settingsBrowserRestart,settingsAntigravityRestart}.Where(c=>c!=null).ToArray();
            float scale=actionHeight/42f;Func<int,int> px=n=>(int)Math.Round(n*scale);
            settingsGeneralTab=new DialogButton("Общие",bodyFont,border,ForeColor,border,px(8));
            settingsProvidersTab=new DialogButton("DNS-провайдеры",bodyFont,border,ForeColor,border,px(8));
            settingsGeneralTab.Click+=(s,e)=>SelectSettingsTab(false);settingsProvidersTab.Click+=(s,e)=>SelectSettingsTab(true);
            Controls.Add(settingsGeneralTab);Controls.Add(settingsProvidersTab);
            providerPanel=new Panel{BackColor=surface};Controls.Add(providerPanel);
            providerHint=new Label{Text="Галочка — показывать в списке и использовать в Авто.\nСкрытие не выключает текущее подключение.",Font=bodyFont,ForeColor=messageText.ForeColor,UseMnemonic=false};providerPanel.Controls.Add(providerHint);
            providerRows=new Panel{AutoScroll=true,BackColor=surface};providerPanel.Controls.Add(providerRows);
            foreach(int provider in Dns.AvailableProviders){int id=provider;
                var check=new CheckBox{Text=Dns.Name[id],Checked=!hidden.Contains(id),Font=bodyFont,ForeColor=ForeColor,BackColor=surface,AutoEllipsis=true,AccessibleName="Показывать DNS-провайдера "+Dns.Name[id]};
                check.CheckedChanged+=(s,e)=>{if(providerChecks.Count>0&&!providerChecks.Values.Any(c=>c.Checked))check.Checked=true;};
                var ping=new Label{Text="Не измерено",Font=bodyFont,ForeColor=messageText.ForeColor,TextAlign=ContentAlignment.MiddleRight,AutoEllipsis=true,AccessibleName="Задержка DNS "+Dns.Name[id]};
                providerChecks.Add(id,check);providerPings.Add(id,ping);providerRows.Controls.Add(check);providerRows.Controls.Add(ping);
            }
            providerRefresh=new DialogButton("Измерить задержку DNS",bodyFont,border,ForeColor,border,px(8));providerPanel.Controls.Add(providerRefresh);
            providerRefresh.Click+=(s,e)=>MeasureProviders(protocol);
            FormClosed+=(s,e)=>{if(providerMeasurements!=null)providerMeasurements.Cancel();};
            SelectSettingsTab(false);
        }
        internal void SelectSettingsTab(bool providers)
        {
            providersTab=providers;foreach(var control in settingsGeneralControls)control.Visible=!providers;
            providerPanel.Visible=providers;settingsGeneralTab.Enabled=providers;settingsProvidersTab.Enabled=!providers;UpdateDialogLayout();
        }
        void LayoutProviderSettings(Func<int,int> px)
        {
            if(providerPanel==null)return;
            int width=ClientSize.Width-px(56),half=(width-px(8))/2;
            settingsGeneralTab.Bounds=new Rectangle(px(28),px(64),half,px(32));settingsProvidersTab.Bounds=new Rectangle(px(36)+half,px(64),half,px(32));
            providerGeneralHeading.Bounds=new Rectangle(px(28),px(108),width,px(24));
            providerPanel.Bounds=new Rectangle(px(28),px(108),width,Math.Max(px(130),primaryAction.Top-px(124)));
            providerHint.Bounds=new Rectangle(0,0,width,px(52));
            providerRefresh.Bounds=new Rectangle(0,providerPanel.Height-px(38),width,px(36));
            providerRows.Bounds=new Rectangle(0,px(60),width,Math.Max(px(30),providerRefresh.Top-px(68)));
            int row=0,rowWidth=width-SystemInformation.VerticalScrollBarWidth;
            foreach(int id in Dns.AvailableProviders){providerChecks[id].Bounds=new Rectangle(0,px(38)*row,rowWidth-px(112),px(34));providerPings[id].Bounds=new Rectangle(rowWidth-px(112),px(38)*row,px(110),px(34));row++;}
            providerRows.AutoScrollMinSize=new Size(0,px(38)*row);
            if(providersTab)foreach(var control in settingsGeneralControls)control.Visible=false;
        }
        internal void MeasureProviders(DnsTransport protocol,Func<int,DnsTransport,CancellationToken,ProviderLatency> measure=null)
        {
            if(providerMeasurements!=null)return;
            var cancel=new CancellationTokenSource();providerMeasurements=cancel;providerRefresh.Enabled=false;
            providerRefresh.Text="Измеряем…";foreach(var label in providerPings.Values)label.Text="…";
            var ids=Dns.AvailableProviders;
            ThreadPool.QueueUserWorkItem(_=>{
                try{Parallel.ForEach(ids,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=cancel.Token},id=>{
                    var result=measure==null?ProviderLatency.Measure(id,protocol,cancel.Token):measure(id,protocol,cancel.Token);
                    DiagnosticReport.Record("Задержка DNS","Provider="+Dns.Name[id]+"; protocol="+result.Protocol+"; ms="+result.Milliseconds+"; error="+result.Error+"; query=example.com; network settings unchanged");
                    if(IsDisposed||cancel.IsCancellationRequested)return;
                    try{BeginInvoke((Action)(()=>{if(IsDisposed||cancel.IsCancellationRequested)return;
                        providerPings[id].Text=result.Error!=null?result.Label:(result.Protocol==DnsTransport.Https?"DoH":result.Protocol==DnsTransport.Tls?"DoT":"DNS")+" · "+result.Label;providerPings[id].AccessibleDescription=ConnectionUi.ProtocolName(result.Protocol)+"; "+result.MeasuredUtc.ToLocalTime().ToString("HH:mm:ss");
                    }));}catch(InvalidOperationException){}
                });}catch(OperationCanceledException){}
                finally{if(!IsDisposed)try{BeginInvoke((Action)(()=>{providerMeasurements=null;providerRefresh.Enabled=true;providerRefresh.Text="Повторить замер · DNS, не ICMP";cancel.Dispose();}));}catch(InvalidOperationException){cancel.Dispose();}else cancel.Dispose();}
            });
        }
    }
}
