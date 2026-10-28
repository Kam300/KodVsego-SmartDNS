using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HbAi.XboxDns {
    sealed class ServiceBoardWindow : Form {
        readonly DataGridView grid=new DataGridView();readonly ComboBox protocol=new ComboBox(),service=new ComboBox();
        readonly Button run=new Button();readonly Label summary=new Label();readonly Color ink,muted,accent,surface;
        readonly long revision=CatalogUpdate.Revision;readonly int providerCount=Dns.Name.Length;
        BoardCell[,] cells;CancellationTokenSource cancel;int generation;bool stale;
        internal event Action<string> StatusChanged;
        internal bool Running {get{return cancel!=null;}}
        internal bool SnapshotCurrent {get{return revision==CatalogUpdate.Revision&&providerCount==Dns.Name.Length;}}
        internal bool Dark {get;private set;}
        internal ServiceBoardWindow(bool dark,DnsTransport current,Action diagnostics) {
            Dark=dark;
            Text="Карта сервисов · Smart DNS";StartPosition=FormStartPosition.CenterParent;MinimumSize=new Size(660,470);Size=new Size(1110,690);
            BackColor=dark?Color.FromArgb(12,15,24):Color.FromArgb(245,248,252);ink=dark?Color.White:Color.FromArgb(21,29,34);
            muted=dark?Color.FromArgb(169,180,198):Color.FromArgb(90,107,125);accent=Color.FromArgb(2,165,207);surface=dark?Color.FromArgb(24,30,43):Color.White;
            ForeColor=ink;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Dpi;
            var header=new FlowLayoutPanel{Dock=DockStyle.Top,Height=142,Padding=new Padding(20,10,20,5),FlowDirection=FlowDirection.TopDown,WrapContents=false};
            header.Controls.Add(new Label{Text="Карта сервисов",Font=new Font("Segoe UI",19,FontStyle.Bold),AutoSize=true});
            header.Controls.Add(new Label{Text="Сравните DNS в вашей сети. Нажмите на ячейку для подробностей.",AutoSize=true,ForeColor=muted,Margin=new Padding(0,4,0,8)});
            var actions=new FlowLayoutPanel{AutoSize=true,WrapContents=false,Margin=Padding.Empty};
            foreach(var combo in new[]{service,protocol}){combo.DropDownStyle=ComboBoxStyle.DropDownList;combo.FlatStyle=FlatStyle.Flat;combo.BackColor=surface;combo.ForeColor=ink;combo.Width=155;actions.Controls.Add(combo);}
            service.Items.Add("Все сервисы");service.Items.AddRange(ServiceBoard.Targets.Select(t=>t.Name).ToArray());service.SelectedIndex=1;
            protocol.Items.AddRange(new object[]{"DoH · HTTPS","DoT · TLS","Обычный DNS"});protocol.SelectedIndex=current==DnsTransport.Tls?1:current==DnsTransport.Plain?2:0;
            run.Text="Проверить все DNS";run.Width=190;run.Height=32;run.FlatStyle=FlatStyle.Flat;run.BackColor=accent;run.ForeColor=Color.FromArgb(8,25,35);actions.Controls.Add(run);run.Click+=(s,e)=>{if(cancel!=null)Stop();else Start();};
            var old=new Button{Text="Подробный тест DNS",Width=175,Dock=DockStyle.Right,FlatStyle=FlatStyle.Flat,ForeColor=ink};old.Click+=(s,e)=>diagnostics();
            header.Controls.Add(actions);Controls.Add(header);
            var footer=new Panel{Dock=DockStyle.Bottom,Height=85,Padding=new Padding(20,9,20,8)};
            summary.Dock=DockStyle.Top;summary.Height=25;summary.ForeColor=accent;footer.Controls.Add(summary);footer.Controls.Add(old);
            footer.Controls.Add(new Label{Dock=DockStyle.Bottom,Height=40,Text="● Ответ есть   ● Нужен вход / браузер   ● Есть проблема   · Не проверено\nБез аккаунта и API-ключей. Ответ сервера не подтверждает генерацию или доступ после входа.",ForeColor=muted});Controls.Add(footer);
            grid.Dock=DockStyle.Fill;grid.BackgroundColor=BackColor;grid.BorderStyle=BorderStyle.None;grid.RowHeadersVisible=false;grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.AllowUserToResizeRows=false;grid.ReadOnly=true;grid.AutoGenerateColumns=false;
            grid.EnableHeadersVisualStyles=false;grid.ColumnHeadersBorderStyle=DataGridViewHeaderBorderStyle.None;grid.ColumnHeadersHeight=42;grid.ColumnHeadersDefaultCellStyle=new DataGridViewCellStyle{BackColor=surface,ForeColor=ink,Font=new Font(Font,FontStyle.Bold)};
            grid.DefaultCellStyle=new DataGridViewCellStyle{BackColor=surface,ForeColor=muted,SelectionBackColor=surface,SelectionForeColor=ink,Padding=new Padding(12,5,4,5),WrapMode=DataGridViewTriState.True};grid.GridColor=BackColor;grid.RowTemplate.Height=46;grid.CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal;grid.SelectionMode=DataGridViewSelectionMode.CellSelect;grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewTextBoxColumn{Name="provider",HeaderText="DNS-провайдер",Width=174,Frozen=true,AutoSizeMode=DataGridViewAutoSizeColumnMode.None,SortMode=DataGridViewColumnSortMode.NotSortable});
            foreach(var target in ServiceBoard.Targets)grid.Columns.Add(new DataGridViewTextBoxColumn{Name=target.Name,HeaderText=target.Name,MinimumWidth=118,FillWeight=125,SortMode=DataGridViewColumnSortMode.NotSortable,Visible=Enumerable.Range(0,providerCount).Any(p=>Dns.IsAvailable(p)&&RuntimeProviders.All[p].services.Contains(target.Name))});
            for(int p=0;p<providerCount;p++){grid.Rows.Add(new object[]{Dns.Name[p]});grid.Rows[p].Visible=Dns.IsAvailable(p);}
            Controls.Add(grid);grid.BringToFront();grid.CellClick+=(s,e)=>{if(e.RowIndex>=0&&e.ColumnIndex>0){var cell=cells[e.RowIndex,e.ColumnIndex-1];using(var dialog=new StyledDialog(this,Dark,Font.FontFamily,Font.FontFamily,cell.Caption+"\n\n"+cell.Detail+(cell.CheckedUtc==default(DateTime)?"":"\n\nПроверено: "+cell.CheckedUtc.ToLocalTime().ToString("HH:mm:ss")),Dns.Name[e.RowIndex]+" · "+ServiceBoard.Targets[e.ColumnIndex-1].Name,MessageBoxButtons.OK,MessageBoxIcon.Information,MessageBoxDefaultButton.Button1,1f))dialog.ShowDialog(this);}};
            protocol.SelectedIndexChanged+=(s,e)=>{Stop();Reset();};Reset();NetworkChange.NetworkAddressChanged+=NetworkChanged;
            FormClosing+=(s,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Stop();Hide();}};
            FormClosed+=(s,e)=>{NetworkChange.NetworkAddressChanged-=NetworkChanged;Stop();};
            Shown+=(s,e)=>{var area=Screen.FromControl(this).WorkingArea;Size=new Size(Math.Min(Width,area.Width-24),Math.Min(Height,area.Height-24));};
        }
        DnsTransport Protocol {get{return protocol.SelectedIndex==1?DnsTransport.Tls:protocol.SelectedIndex==2?DnsTransport.Plain:DnsTransport.Https;}}
        protected override void Dispose(bool disposing){if(disposing){generation++;NetworkChange.NetworkAddressChanged-=NetworkChanged;if(cancel!=null)cancel.Cancel();cancel=null;}base.Dispose(disposing);}
        void Reset(){stale=false;cells=new BoardCell[providerCount,ServiceBoard.Targets.Length];for(int p=0;p<providerCount;p++)for(int s=0;s<ServiceBoard.Targets.Length;s++)PaintCell(p,s,ServiceBoard.Initial(p,s,Protocol));summary.Text="Выберите сервис и нажмите «Проверить все DNS».";if(StatusChanged!=null)StatusChanged("не проверено");}
        void PaintCell(int p,int s,BoardCell value){cells[p,s]=value;var view=grid.Rows[p].Cells[s+1];view.Value=value.Caption+(value.CheckedUtc==default(DateTime)?"":"\n"+value.CheckedUtc.ToLocalTime().ToString("HH:mm"));
            Color color=value.State==BoardState.Response?Color.FromArgb(35,175,125):value.State==BoardState.Failed?Color.FromArgb(235,104,128):value.State==BoardState.Login||value.State==BoardState.Checking?accent:value.State==BoardState.Browser?Color.FromArgb(155,132,238):value.State==BoardState.Unknown?Color.FromArgb(205,142,175):muted;view.Style.ForeColor=color;view.Style.SelectionForeColor=color;view.ToolTipText=value.Detail;}
        void Stop(){generation++;var c=cancel;cancel=null;if(c!=null)c.Cancel();run.Text="Проверить все DNS";protocol.Enabled=true;service.Enabled=true;
            if(cells!=null)for(int p=0;p<providerCount;p++)for(int s=0;s<ServiceBoard.Targets.Length;s++)if(cells[p,s].State==BoardState.Checking)PaintCell(p,s,new BoardCell{State=BoardState.Cancelled,Detail="Проверка остановлена; предыдущие результаты не используются."});}
        void NetworkChanged(object sender,EventArgs args){if(!IsHandleCreated||IsDisposed)return;try{BeginInvoke((Action)(()=>{if(IsDisposed)return;Stop();Reset();stale=true;summary.Text="Сеть изменилась · результаты сброшены, повторите проверку.";if(StatusChanged!=null)StatusChanged("сеть изменилась · повторите");}));}catch(InvalidOperationException){}}
        async void Start(){
            if(CatalogUpdate.Revision!=revision||Dns.Name.Length!=providerCount){summary.Text="Каталог изменился · закройте и откройте карту снова.";return;}
            if(!NetworkCheck.HasConnection()||VpnCheck.Scan().NeedsAttention){Reset();summary.Text="Проверьте интернет и отключите другой VPN перед сравнением DNS.";return;}
            var c=new CancellationTokenSource();cancel=c;int current=++generation;var transport=Protocol;int chosen=service.SelectedIndex-1;int completed=0;
            var jobs=new List<int[]>();for(int p=0;p<providerCount;p++)for(int s=0;s<ServiceBoard.Targets.Length;s++)if((chosen<0||chosen==s)&&ServiceBoard.Initial(p,s,transport).State==BoardState.Untested){jobs.Add(new[]{p,s});PaintCell(p,s,new BoardCell{State=BoardState.Checking});}
            if(jobs.Count==0){Stop();summary.Text="Для выбранного сервиса и протокола нет заявленных провайдеров.";return;}
            c.CancelAfter(120000);run.Text="Остановить";protocol.Enabled=false;service.Enabled=false;summary.Text="Проверяем… 0 / "+jobs.Count;
            try{await Task.Run(()=>Parallel.ForEach(jobs,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=c.Token},job=>{
                BoardCell result;try{result=BoardCell.From(ServiceAvailability.Check(ServiceBoard.Targets[job[1]],job[0],transport!=DnsTransport.Plain,new ServiceAvailabilityProbe(transport==DnsTransport.Tls),c.Token,1));}
                catch(OperationCanceledException){throw;}catch(Exception){result=new BoardCell{State=BoardState.Failed,Detail="Не удалось выполнить проверку DNS/HTTPS. Повторите позже.",CheckedUtc=DateTime.UtcNow};}
                if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke((Action)(()=>{if(current!=generation||c.IsCancellationRequested||CatalogUpdate.Revision!=revision)return;PaintCell(job[0],job[1],result);summary.Text="Готово "+(++completed)+" / "+jobs.Count+" · "+DateTime.Now.ToString("HH:mm");if(StatusChanged!=null){int answers=0,problems=0;for(int p=0;p<providerCount;p++)for(int s=0;s<ServiceBoard.Targets.Length;s++)if(chosen<0||chosen==s){if(cells[p,s].State==BoardState.Response||cells[p,s].State==BoardState.Login)answers++;if(cells[p,s].State==BoardState.Failed)problems++;}StatusChanged((chosen<0?"все":ServiceBoard.Targets[chosen].Name)+": "+answers+" ответов · "+problems+" проблем");}}));}catch(InvalidOperationException){}
            }));}catch(OperationCanceledException){}finally{if(current==generation){bool timedOut=c.IsCancellationRequested;Stop();if(!stale)summary.Text=(timedOut?"Время проверки истекло · ":"Проверка завершена · ")+completed+" / "+jobs.Count+". Нажмите на статус для деталей.";}c.Dispose();}
        }
    }
    partial class MainForm {
        ServiceBoardWindow serviceBoardWindow;
        string serviceBoardSummary="не проверено";
        void ShowServiceBoard(){if(serviceBoardWindow!=null&&!serviceBoardWindow.IsDisposed&&(!serviceBoardWindow.SnapshotCurrent||serviceBoardWindow.Dark!=darkTheme))serviceBoardWindow.Dispose();if(serviceBoardWindow!=null&&!serviceBoardWindow.IsDisposed){serviceBoardWindow.Show(this);serviceBoardWindow.Activate();return;}serviceBoardSummary="не проверено";serviceBoardWindow=new ServiceBoardWindow(darkTheme,Transport,ShowDnsDiagnostics);serviceBoardWindow.StatusChanged+=text=>{serviceBoardSummary=text;Invalidate();};serviceBoardWindow.Show(this);}
    }
}
