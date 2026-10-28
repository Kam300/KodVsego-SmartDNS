using System;
using System.Drawing;
using System.Windows.Forms;

namespace HbAi.XboxDns {
    sealed partial class StyledDialog {
        Panel settingsCatalog;
        CheckBox settingsCatalogAuto;
        Label settingsCatalogStatus;
        LinkLabel settingsCatalogRefresh;
        internal bool AutoCatalogUpdate {get{return settingsCatalogAuto==null||settingsCatalogAuto.Checked;}}
        internal void AddCatalogSettings(bool enabled,Action<StyledDialog> check) {
            settingsCatalog=new Panel{BackColor=surface};
            settingsCatalogAuto=new CheckBox{Text="Обновлять адреса DNS автоматически",Checked=enabled,Font=bodyFont,ForeColor=ForeColor,BackColor=surface,AccessibleDescription="Проверять каталог проекта раз в сутки. Активное подключение не меняется."};
            settingsCatalogStatus=new Label{Font=bodyFont,ForeColor=Color.Gray,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};
            settingsCatalogRefresh=new LinkLabel{Text="Проверить",AccessibleName="Проверить каталог сейчас",Font=bodyFont,LinkColor=statusAccent,ActiveLinkColor=statusAccent,UseMnemonic=false,TextAlign=ContentAlignment.MiddleRight};
            settingsCatalogRefresh.LinkClicked+=(s,e)=>check(this);
            settingsCatalog.Controls.AddRange(new Control[]{settingsCatalogAuto,settingsCatalogStatus,settingsCatalogRefresh});
            settingsCatalog.SizeChanged+=(s,e)=>{float scale=actionHeight/42f;Func<int,int> px=n=>(int)Math.Round(n*scale);int width=settingsCatalog.ClientSize.Width;
                settingsCatalogAuto.Bounds=new Rectangle(0,0,width,px(24));
                settingsCatalogStatus.Bounds=new Rectangle(px(18),px(27),Math.Max(0,width-px(134)),px(22));
                settingsCatalogRefresh.Bounds=new Rectangle(Math.Max(0,width-px(112)),px(27),px(112),px(22));};
            Controls.Add(settingsCatalog);ShowCatalogStatus(CatalogUpdate.Summary,false);UpdateDialogLayout();
        }
        internal void ShowCatalogStatus(string text,bool checking){if(IsDisposed||settingsCatalog==null)return;settingsCatalogStatus.Text=text;settingsCatalogStatus.AccessibleDescription=CatalogUpdate.Status;settingsCatalogRefresh.Enabled=!checking;settingsCatalogRefresh.Text=checking?"Проверяем…":"Проверить";}
    }
    partial class MainForm {
        void ApplyCatalogIfIdle(){
            if(!profilePreferences||initializing||active||busy||googleChecking||resolverChecking||latencyChecking||agLoading||agSearch!=null||serviceBoardWindow!=null&&serviceBoardWindow.Running||HasLegacyAntigravityState||hasBackup||ManagedDns.Running||ManagedDns.Stopping||Form.ActiveForm!=this)return;
            try{var customs=RuntimeProviders.Customs;if(CatalogUpdate.Promote(true)){serviceBoardSummary="каталог изменился · повторите";RefreshCustomProviders(customs);DiagnosticReport.Record("Каталог DNS","Применена версия "+CatalogUpdate.Revision);}}
            catch(Exception error){DiagnosticReport.Record("Каталог DNS",error.GetType().Name);}
        }
        void CheckCatalogInSettings(StyledDialog dialog){
            dialog.ShowCatalogStatus("Проверяем каталог…",true);
            CatalogUpdate.Check(true,()=>{if(!dialog.IsDisposed)try{dialog.BeginInvoke((Action)(()=>{if(!dialog.IsDisposed)dialog.ShowCatalogStatus(CatalogUpdate.Summary,false);}));}catch(InvalidOperationException){}});
        }
    }
}
