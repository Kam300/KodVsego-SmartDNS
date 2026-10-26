using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HbAi.XboxDns
{
    sealed partial class StyledDialog
    {
        List<ProviderEntry> customDns;
        Button providerAdd;
        readonly Dictionary<int,Button> providerEdits=new Dictionary<int,Button>();
        internal Func<int,bool> CustomDnsLocked=id=>false;
        internal ProviderEntry[] CustomProviders {get{return customDns.Select(CustomDns.Clone).ToArray();}}
        internal bool CustomProvidersChanged {get{return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(CustomProviders)!=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(RuntimeProviders.Customs);}}
        internal bool StageCustomProvider(ProviderEntry entry)
        {
            if(providerMeasurements!=null)return false;
            if(entry.id<CustomDns.First||entry.id>CustomDns.First+customDns.Count||CustomDnsLocked(entry.id))return false;
            var draft=customDns.Select(CustomDns.Clone).ToList();
            if(entry.id==CustomDns.First+draft.Count)draft.Add(entry);else {
                var previous=draft[entry.id-CustomDns.First];entry.legacyIpv4=previous.legacyIpv4;
                if(!previous.ipv4.SequenceEqual(entry.ipv4))entry.legacyIpv4=new[]{previous.ipv4}.Concat(previous.legacyIpv4).Take(16).ToArray();
                draft[entry.id-CustomDns.First]=entry;
            }
            var validated=CustomDns.Validate(draft);var hidden=HiddenProviders;
            customDns=validated.ToList();RebuildProviderRows(hidden);UpdateDialogLayout();return true;
        }
        void EditCustomProvider(int id)
        {
            if(providerMeasurements!=null)return;
            if(CustomDnsLocked(id)){MessageBox.Show(this,"Сначала отключите DNS: эта запись используется или подключение ещё меняется.","Свой DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            ProviderEntry previous=id<CustomDns.First+customDns.Count?customDns[id-CustomDns.First]:null;
            using(var editor=new Form{Text=previous==null?"Добавить свой DNS":"Изменить свой DNS",StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,BackColor=surface,ForeColor=ForeColor,Font=bodyFont,AutoScaleMode=AutoScaleMode.None}){
                float scale=Math.Min(1.5f,Math.Max(.85f,actionHeight/42f));Func<int,int> px=n=>(int)Math.Round(n*scale);
                editor.ClientSize=new Size(px(470),px(480));
                var fields=new List<TextBox>();string[] labels={"Название","IP-адреса · через пробел или запятую (IPv4 обязателен)","DoH · HTTPS-адрес (необязательно)","DoT · имя сервера, порт 853 (необязательно)"};
                string[] values={previous==null?"":previous.name,previous==null?"":string.Join(", ",previous.ipv4.Concat(previous.ipv6)),previous==null?"":previous.doh,previous==null?"":previous.dot};
                for(int n=0;n<labels.Length;n++){
                    editor.Controls.Add(new Label{Text=labels[n],Bounds=new Rectangle(px(20),px(16+n*69),px(430),px(24)),Font=bodyFont,ForeColor=ForeColor});
                    var box=new TextBox{Text=values[n]??"",Bounds=new Rectangle(px(20),px(41+n*69),px(430),px(30)),Font=bodyFont,BackColor=surface,ForeColor=ForeColor,MaxLength=n==0?48:1024,AccessibleName=labels[n]};fields.Add(box);editor.Controls.Add(box);
                }
                var auto=new CheckBox{Text="Участвует в Авто · доступ проверяется",Checked=previous==null||previous.autoGoals.Any(g=>g!="Antigravity"),Bounds=new Rectangle(px(20),px(297),px(430),px(29)),ForeColor=ForeColor};
                var ag=new CheckBox{Text="Поддерживает отдельный Antigravity (нужен DoH)",Checked=previous!=null&&previous.autoGoals.Contains("Antigravity"),Bounds=new Rectangle(px(20),px(329),px(430),px(29)),ForeColor=ForeColor};editor.Controls.Add(auto);editor.Controls.Add(ag);
                var failure=new Label{Bounds=new Rectangle(px(20),px(363),px(430),px(55)),Font=bodyFont,ForeColor=Color.Coral};editor.Controls.Add(failure);
                var save=new Button{Text="Готово",Bounds=new Rectangle(px(310),px(430),px(140),px(32))};var cancel=new Button{Text="Отмена",DialogResult=DialogResult.Cancel,Bounds=new Rectangle(px(155),px(430),px(140),px(32))};editor.Controls.Add(save);editor.Controls.Add(cancel);editor.AcceptButton=save;editor.CancelButton=cancel;
                save.Click+=(s,e)=>{try{if(StageCustomProvider(CustomDns.Create(id,fields[0].Text,fields[1].Text,fields[2].Text,fields[3].Text,auto.Checked,ag.Checked))){editor.DialogResult=DialogResult.OK;editor.Close();}}catch(Exception problem){failure.Text=problem.Message;}};
                if(previous!=null){var delete=new Button{Text="Удалить",Bounds=new Rectangle(px(20),px(430),px(120),px(32))};editor.Controls.Add(delete);delete.Click+=(s,e)=>{var removed=CustomDns.Clone(previous);removed.enabled=false;if(StageCustomProvider(removed)){editor.DialogResult=DialogResult.OK;editor.Close();}};}
                editor.ShowDialog(this);
            }
        }
        void RebuildProviderRows(int[] hidden)
        {
            foreach(Control control in providerRows.Controls.Cast<Control>().ToArray())control.Dispose();providerChecks.Clear();providerPings.Clear();providerEdits.Clear();providerAntigravityBadges.Clear();
            var entries=RuntimeProviders.All.Take(CustomDns.First).Where(p=>Dns.IsAvailable(p.id)).Concat(customDns.Where(p=>p.enabled));
            foreach(var provider in entries){int id=provider.id;
                var check=new CheckBox{Text=provider.name,Checked=!hidden.Contains(id),Font=bodyFont,ForeColor=ForeColor,BackColor=surface,AutoEllipsis=true,AccessibleName="Показывать DNS-провайдера "+provider.name};
                check.CheckedChanged+=(s,e)=>{if(providerChecks.Count>0&&!providerChecks.Values.Any(c=>c.Checked))check.Checked=true;RefreshAntigravitySettingsChoices();UpdateDialogLayout();};
                var ping=new Label{Text=CustomDns.IsCustom(id)?"Свой DNS":"Не измерено",Font=bodyFont,ForeColor=messageText.ForeColor,TextAlign=ContentAlignment.MiddleRight,AutoEllipsis=true,AccessibleName="Задержка DNS "+provider.name};
                providerChecks.Add(id,check);providerPings.Add(id,ping);providerRows.Controls.Add(check);providerRows.Controls.Add(ping);
                if(provider.doh!=null&&provider.autoGoals.Contains("Antigravity")){var badge=new Label{Text="AG",Font=bodyFont,ForeColor=statusAccent,BackColor=surface,TextAlign=ContentAlignment.MiddleCenter,AccessibleName="Antigravity · отдельный DNS · доступ не проверен"};providerAntigravityBadges.Add(id,badge);providerRows.Controls.Add(badge);}
                if(CustomDns.IsCustom(id)){var edit=new Button{Text="…",Font=bodyFont,AccessibleName="Изменить "+provider.name};edit.Click+=(s,e)=>EditCustomProvider(id);providerEdits.Add(id,edit);providerRows.Controls.Add(edit);}
            }
            RefreshAntigravitySettingsChoices();
        }
    }
}
