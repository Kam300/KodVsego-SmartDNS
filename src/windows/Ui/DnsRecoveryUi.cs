using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace HbAi.XboxDns {
    sealed partial class StyledDialog {
        LinkLabel settingsDnsRecovery;Action<StyledDialog> recoveryAction;bool recoveryBusy,recoveryReady;
        internal void AddDnsRecoveryLink(Action<StyledDialog> open){
            settingsDnsRecovery=new LinkLabel{Text="Очистить DNS…",Font=bodyFont,LinkColor=statusAccent,ActiveLinkColor=statusAccent,UseMnemonic=false,TextAlign=ContentAlignment.MiddleRight,AccessibleName="Восстановить DNS на всех адаптерах, включая отключённый Wi-Fi"};
            settingsDnsRecovery.LinkClicked+=(s,e)=>open(this);Controls.Add(settingsDnsRecovery);UpdateDialogLayout();
        }
        internal void AddDnsRecoveryAction(Action<StyledDialog> action){
            recoveryAction=action;primaryAction.DialogResult=DialogResult.None;primaryAction.Text="Проверяем…";primaryAction.Enabled=false;
            primaryAction.Click+=(s,e)=>{if(recoveryReady&&!recoveryBusy)recoveryAction(this);};
            FormClosing+=(s,e)=>{if(recoveryBusy)e.Cancel=true;};
        }
        internal void RecoveryStatus(string text,bool canRun,bool running=false,bool close=false){
            messageText.Text=text;recoveryReady=canRun;recoveryBusy=running;
            primaryAction.Text=running?"Восстанавливаем…":close?"Закрыть":"Очистить DNS";primaryAction.Enabled=!running&&(canRun||close);primaryAction.DialogResult=close?DialogResult.OK:DialogResult.None;UpdateDialogLayout();
        }
    }
    partial class MainForm {
        void ShowDnsRecovery(StyledDialog parent){
            if(busy||initializing||ManagedDns.Running||ManagedDns.Stopping){ShowMessage("Сначала выключите DNS приложения и дождитесь восстановления настроек. После этого можно очистить оставшиеся DNS адаптеров.","Восстановление DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            using(var dialog=CreateDialog("Проверяем сохранённые настройки и все сетевые адаптеры, включая отключённый Wi-Fi…","Восстановление DNS",MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1)){
                Action scan=()=>ThreadPool.QueueUserWorkItem(_=>{
                    DnsRecoveryItem[] plan=null;string failure=null;try{plan=new DnsSettingsEngine(new NativeDnsSystem(),new RegistryDnsBackupStore()).RecoveryPlan(Dns.KnownServers);}catch(Exception problem){failure=problem.Message;DiagnosticReport.Record("DNS recovery scan",problem.ToString());}
                    PostRecovery(dialog,()=>{if(failure!=null){dialog.RecoveryStatus("Не удалось проверить настройки:\n"+failure,false,false,true);return;}
                        if(plan.Length==0&&DnsTunnel.Pending){dialog.RecoveryStatus("Осталось правило или состояние прежнего локального DNS. Удалим только правило Smart DNS и восстановим сохранённые настройки.",true);return;}
                        if(plan.Length==0){dialog.RecoveryStatus("Сохранённых копий и оставшихся распознанных DNS не найдено. Проверены активные и отключённые адаптеры.",false,false,true);return;}
                        string summary="Будут обработаны настройки:\n\n"+string.Join("\n",plan.Select(item=>item.Summary));
                        summary+="\n\nПо копии восстановим прежние DNS и настройки шифрования. Без копии сбросим только распознанные DNS программы. Настройки отсутствующих адаптеров останутся в копии до восстановления.";
                        dialog.RecoveryStatus(summary,plan.Any(item=>item.Present),false,!plan.Any(item=>item.Present));
                    });
                });
                dialog.AddDnsRecoveryAction(target=>{
                    if(busy||ManagedDns.Running||ManagedDns.Stopping){target.RecoveryStatus("Другая операция DNS ещё выполняется. Закройте окно и повторите очистку после её завершения.",false,false,true);return;}
                    CancelResolverCheck();CancelConnectionLatency();operationRevision++;busy=true;error=null;target.RecoveryStatus("Восстанавливаем DNS на всех доступных адаптерах, включая отключённый Wi-Fi. Подтвердите запрос Windows, если он появится.",false,true);Invalidate();
                    ThreadPool.QueueUserWorkItem(_=>{
                        string failure=null;DnsRecoveryItem[] remaining=null;try{failure=Dns.ResetKnown();remaining=new DnsSettingsEngine(new NativeDnsSystem(),new RegistryDnsBackupStore()).RecoveryPlan(Dns.KnownServers);}catch(Exception problem){failure=problem.Message;DiagnosticReport.Record("DNS recovery",problem.ToString());}
                        PostRecovery(target,()=>{busy=false;RefreshNetworkStatus();Invalidate();string text;
                            if(failure==null&&remaining!=null&&remaining.Length==0)text="DNS восстановлены. Оставшихся копий и распознанных DNS на проверенных адаптерах нет.";
                            else {text=failure==null?"Восстановление завершено не полностью.":failure.StartsWith("[DNS-DEFERRED]",StringComparison.Ordinal)?failure.Substring(14):"Не удалось полностью восстановить DNS:\n"+failure;if(remaining!=null&&remaining.Length>0)text+="\n\nОстались:\n"+string.Join("\n",remaining.Select(item=>item.Summary))+"\n\nКопии сохранены для повторной попытки.";}
                            DiagnosticReport.Record("DNS recovery result",text);target.RecoveryStatus(text,false,false,true);
                        });
                    });
                });
                dialog.Shown+=(s,e)=>scan();dialog.ShowDialog(parent);
            }
        }
        static void PostRecovery(StyledDialog dialog,Action update){if(dialog.IsDisposed||!dialog.IsHandleCreated)return;try{dialog.BeginInvoke((Action)(()=>{if(!dialog.IsDisposed)update();}));}catch(InvalidOperationException){}}
    }
}
