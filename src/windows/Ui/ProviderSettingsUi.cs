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
        Panel providerPanel,providerRows,antigravitySettingsPanel;Label providerHint,providerGeneralHeading,antigravitySettingsHint,antigravityProviderLabel;Button providerRefresh,settingsGeneralTab,settingsProvidersTab,settingsAntigravityTab;
        CheckBox antigravitySettingsEnabled,antigravityAutoDns;Label antigravitySettingsTitle;DialogServicePicker antigravitySettingsProvider;int[] antigravitySettingsIds=new int[0];int antigravityPreferred;
        Control[] settingsGeneralControls;bool providersTab,antigravityTab;
        readonly Dictionary<int,CheckBox> providerChecks=new Dictionary<int,CheckBox>();
        readonly Dictionary<int,Label> providerPings=new Dictionary<int,Label>();
        readonly Dictionary<int,Label> providerAntigravityBadges=new Dictionary<int,Label>();
        CancellationTokenSource providerMeasurements;
        internal int[] HiddenProviders {get{return providerChecks.Where(pair=>!pair.Value.Checked).Select(pair=>pair.Key).OrderBy(p=>p).ToArray();}}
        internal void AddProviderSettingsControls(int[] hidden,DnsTransport protocol)
        {
            providerGeneralHeading=Controls.OfType<Label>().Single(label=>label.Text=="Выбор DNS");
            settingsGeneralControls=new Control[]{providerGeneralHeading,settingsConnectionPanel,settingsBackupPanel,settingsBackupHeading,settingsSupportHeading,settingsGithub,settingsTelegram,settingsSource,settingsTunnel,settingsResolverCheck,settingsCatalog,settingsRestartHeading,settingsRestartHint,settingsBrowserRestart,settingsAntigravityRestart,settingsStartupRecovery}.Where(c=>c!=null).ToArray();
            float scale=actionHeight/42f;Func<int,int> px=n=>(int)Math.Round(n*scale);
            settingsGeneralTab=new DialogButton("Общие",bodyFont,border,ForeColor,border,px(8));
            settingsProvidersTab=new DialogButton("Провайдеры",bodyFont,border,ForeColor,border,px(8));
            settingsAntigravityTab=new DialogButton("Antigravity",bodyFont,border,ForeColor,border,px(8));
            settingsGeneralTab.Click+=(s,e)=>SelectSettingsTab(false);settingsProvidersTab.Click+=(s,e)=>SelectSettingsTab(true);
            settingsAntigravityTab.Click+=(s,e)=>SelectAntigravitySettingsTab();
            Controls.Add(settingsGeneralTab);Controls.Add(settingsProvidersTab);Controls.Add(settingsAntigravityTab);
            providerPanel=new Panel{BackColor=surface};Controls.Add(providerPanel);
            providerHint=new Label{Text="Галочка — показывать в списке и использовать в Авто.\nAG — есть отдельный маршрут Antigravity.\nСкрытие не выключает текущее подключение.",Font=bodyFont,ForeColor=messageText.ForeColor,UseMnemonic=false};providerPanel.Controls.Add(providerHint);
            providerRows=new Panel{AutoScroll=true,BackColor=surface};providerPanel.Controls.Add(providerRows);
            customDns=RuntimeProviders.Customs.ToList();RebuildProviderRows(hidden);
            providerRefresh=new DialogButton("Измерить задержку DNS",bodyFont,border,ForeColor,border,px(8));providerPanel.Controls.Add(providerRefresh);
            providerRefresh.Click+=(s,e)=>MeasureProviders(protocol);
            providerAdd=new DialogButton("+ Добавить DNS",bodyFont,border,ForeColor,border,px(8));providerPanel.Controls.Add(providerAdd);providerAdd.Click+=(s,e)=>EditCustomProvider(CustomDns.First+customDns.Count);
            FormClosed+=(s,e)=>{if(providerMeasurements!=null)providerMeasurements.Cancel();};
            SelectSettingsTab(false);
        }
        internal void SelectSettingsTab(bool providers)
        {
            providersTab=providers;antigravityTab=false;ApplySettingsTab();
        }
        internal void SelectAntigravitySettingsTab(){providersTab=false;antigravityTab=true;ApplySettingsTab();}
        void ApplySettingsTab()
        {
            foreach(var control in settingsGeneralControls)control.Visible=!providersTab&&!antigravityTab;
            providerPanel.Visible=providersTab;if(antigravitySettingsPanel!=null)antigravitySettingsPanel.Visible=antigravityTab;
            ((DialogButton)settingsGeneralTab).SelectionAccent=!providersTab&&!antigravityTab?statusAccent:(Color?)null;((DialogButton)settingsProvidersTab).SelectionAccent=providersTab?statusAccent:(Color?)null;((DialogButton)settingsAntigravityTab).SelectionAccent=antigravityTab?statusAccent:(Color?)null;
            settingsGeneralTab.Invalidate();settingsProvidersTab.Invalidate();settingsAntigravityTab.Invalidate();
            settingsGeneralTab.Enabled=providersTab||antigravityTab;settingsProvidersTab.Enabled=!providersTab;settingsAntigravityTab.Enabled=!antigravityTab;UpdateDialogLayout();
        }
        internal bool AntigravityRouteEnabled {get{return antigravitySettingsEnabled.Checked;}}
        internal bool AntigravityAutoDnsEnabled {get{return antigravityAutoDns!=null&&antigravityAutoDns.Checked;}}
        internal void SetAntigravityAutoDns(bool enabled){antigravityAutoDns.Checked=enabled;}
        internal int AntigravityRouteProvider {get{return antigravitySettingsProvider.SelectedIndex>=0&&antigravitySettingsProvider.SelectedIndex<antigravitySettingsIds.Length?antigravitySettingsIds[antigravitySettingsProvider.SelectedIndex]:antigravityPreferred;}}
        internal void AddAntigravitySettingsControls(bool enabled,int provider,bool running,bool canSwitch)
        {
            antigravityPreferred=provider;antigravitySettingsPanel=new Panel{BackColor=surface,AutoScroll=true};Controls.Add(antigravitySettingsPanel);
            antigravitySettingsEnabled=new CheckBox{Checked=enabled,Enabled=!running,Font=bodyFont,ForeColor=ForeColor,BackColor=surface,AccessibleName="Использовать отдельный DNS для Antigravity"};antigravitySettingsPanel.Controls.Add(antigravitySettingsEnabled);
            antigravitySettingsTitle=new Label{Text="Отдельный DNS для Antigravity",Font=bodyFont,ForeColor=ForeColor,TextAlign=ContentAlignment.MiddleLeft};antigravitySettingsPanel.Controls.Add(antigravitySettingsTitle);antigravitySettingsTitle.Click+=(s,e)=>{if(antigravitySettingsEnabled.Enabled)antigravitySettingsEnabled.Checked=!antigravitySettingsEnabled.Checked;};
            antigravityProviderLabel=new Label{Text="DNS-провайдер Antigravity",Font=bodyFont,ForeColor=ForeColor};antigravitySettingsPanel.Controls.Add(antigravityProviderLabel);
            antigravityAutoDns=new CheckBox{Text="Автоматически менять DNS при ошибках · beta",Font=bodyFont,ForeColor=ForeColor,BackColor=surface,AccessibleName="Автосмена DNS Antigravity и подбор основного DNS при повторных ошибках"};antigravitySettingsPanel.Controls.Add(antigravityAutoDns);
            antigravitySettingsHint=new Label{Font=bodyFont,ForeColor=messageText.ForeColor,UseMnemonic=false};antigravitySettingsPanel.Controls.Add(antigravitySettingsHint);
            RefreshAntigravitySettingsChoices();antigravitySettingsProvider.Enabled=!running||canSwitch;ApplySettingsTab();
            antigravitySettingsHint.Text="Отдельный DoH для двух API-доменов Antigravity. При ручной смене основной DNS не меняется.\n\nРаботает в способе «2 · Через приложение». "+(running&&canSwitch?"Провайдера можно сменить сейчас.":"Выбор применяется после «Сохранить».")+" Для включения или отключения маршрута сначала выключите DNS.\n\nАвтосмена реагирует на повторные обрывы и ошибку региона. Перегрузка модели, лимиты и ошибки входа DNS не переключают. Если отдельный маршрут не помогает, Авто пробует другой основной DNS, сохраняя маршрут Antigravity. Результат связки подтверждается только началом ответа клиента. Авто не показывает диалоги и не перезапускает клиент.\n\nОшибки клиента попадают в общий журнал и при выключенной автосмене.\n\nAG — есть отдельный маршрут; ответ модели не проверен.";
        }
        void RefreshAntigravitySettingsChoices()
        {
            if(antigravitySettingsPanel==null)return;
            int selected=antigravitySettingsProvider==null?antigravityPreferred:AntigravityRouteProvider;bool editable=antigravitySettingsProvider==null||antigravitySettingsProvider.Enabled;
            var entries=RuntimeProviders.All.Take(CustomDns.First).Where(p=>Dns.IsAvailable(p.id)).Concat(customDns.Where(p=>p.enabled)).Where(p=>p.doh!=null&&p.autoGoals.Contains("Antigravity")&&(p.id==selected||providerChecks.ContainsKey(p.id)&&providerChecks[p.id].Checked)).ToArray();
            if(antigravitySettingsProvider!=null)antigravitySettingsProvider.Dispose();
            antigravitySettingsIds=entries.Select(p=>p.id).ToArray();
            antigravitySettingsProvider=new DialogServicePicker(entries.Select(p=>p.name).ToArray(),bodyFont,settingsCardColor,ForeColor,statusAccent,border,8){Prefix="DNS: ",AccessibleName="DNS-провайдер для Antigravity",Enabled=editable&&entries.Length>0};
            int index=Array.IndexOf(antigravitySettingsIds,selected);if(entries.Length>0)antigravitySettingsProvider.SelectedIndex=Math.Max(0,index);else antigravitySettingsProvider.Text="Нет подходящих провайдеров";
            antigravitySettingsPanel.Controls.Add(antigravitySettingsProvider);
        }
        void LayoutProviderSettings(Func<int,int> px)
        {
            if(providerPanel==null)return;
            int width=ClientSize.Width-px(56),half=(width-px(8))/2,third=(width-px(16))/3;
            settingsGeneralTab.Bounds=new Rectangle(px(28),px(64),third,px(32));settingsProvidersTab.Bounds=new Rectangle(px(36)+third,px(64),third,px(32));settingsAntigravityTab.Bounds=new Rectangle(px(44)+2*third,px(64),third,px(32));
            providerGeneralHeading.Bounds=new Rectangle(px(28),px(108),width,px(24));
            providerPanel.Bounds=new Rectangle(px(28),px(108),width,Math.Max(px(130),primaryAction.Top-px(124)));
            providerHint.Bounds=new Rectangle(0,0,width,px(76));
            providerRefresh.Bounds=new Rectangle(0,providerPanel.Height-px(38),half,px(36));providerAdd.Bounds=new Rectangle(half+px(8),providerPanel.Height-px(38),half,px(36));
            providerRows.Bounds=new Rectangle(0,px(84),width,Math.Max(px(30),providerRefresh.Top-px(92)));
            int row=0,rowWidth=width-SystemInformation.VerticalScrollBarWidth;
            foreach(int id in providerChecks.Keys.OrderBy(n=>n)){int extra=providerEdits.ContainsKey(id)?px(28):0,badge=providerAntigravityBadges.ContainsKey(id)?px(34):0;providerChecks[id].Bounds=new Rectangle(0,px(38)*row,rowWidth-px(112)-extra-badge,px(34));if(badge>0)providerAntigravityBadges[id].Bounds=new Rectangle(providerChecks[id].Right,px(38)*row,px(30),px(34));providerPings[id].Bounds=new Rectangle(rowWidth-px(112)-extra,px(38)*row,px(110),px(34));if(extra>0)providerEdits[id].Bounds=new Rectangle(rowWidth-extra,px(38)*row,extra,px(30));row++;}
            providerRows.AutoScrollMinSize=new Size(0,px(38)*row);
            if(antigravitySettingsPanel!=null){int agWidth=width-SystemInformation.VerticalScrollBarWidth-px(4);antigravitySettingsPanel.Bounds=providerPanel.Bounds;antigravitySettingsEnabled.Bounds=new Rectangle(0,0,px(22),px(34));antigravitySettingsTitle.Bounds=new Rectangle(px(26),0,agWidth-px(26),px(34));antigravityProviderLabel.Bounds=new Rectangle(0,px(48),agWidth,px(26));antigravitySettingsProvider.Bounds=new Rectangle(0,px(82),agWidth,px(40));antigravityAutoDns.Bounds=new Rectangle(0,px(136),agWidth,px(48));antigravitySettingsHint.Bounds=new Rectangle(0,px(196),agWidth,px(400));}
            if(providersTab||antigravityTab)foreach(var control in settingsGeneralControls)control.Visible=false;
        }
        internal void MeasureProviders(DnsTransport protocol,Func<int,DnsTransport,CancellationToken,ProviderLatency> measure=null)
        {
            if(providerMeasurements!=null)return;
            var cancel=new CancellationTokenSource();providerMeasurements=cancel;providerRefresh.Enabled=false;
            providerRefresh.Text="Измеряем…";foreach(var label in providerPings.Values)label.Text="…";
            var ids=providerChecks.Keys.Where(id=>id<Dns.Name.Length&&Dns.IsAvailable(id)&&(!CustomDns.IsCustom(id)||new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(customDns[id-CustomDns.First])==new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(RuntimeProviders.All[id]))).ToArray();
            foreach(int id in providerChecks.Keys.Except(ids))providerPings[id].Text="Сначала сохраните";
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
