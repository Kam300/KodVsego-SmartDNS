using System;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
using HbAi.XboxDns;
static class ServiceBoardTests {
    static int count;static void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    static ServiceAvailabilityResult Result(params ServiceReply[] replies){return new ServiceAvailabilityResult{Name="Gemini",Endpoints=replies.Select(r=>new ServiceEndpointResult{Endpoint=ServiceAvailability.Targets[0].Endpoints[0],Reply=r}).ToArray()};}
    [STAThread]static void Main(){
        Check(ServiceBoard.Initial(1,0,DnsTransport.Https).State==BoardState.Disabled,"Retired DNS cannot be probed or advertised as working");
        Check(ServiceBoard.Initial(12,0,DnsTransport.Https).State==BoardState.Unsupported,"Filtering AdGuard is not advertised as Gemini unlocker");
        Check(ServiceBoard.Initial(5,0,DnsTransport.Plain).State==BoardState.NoTransport,"Plain mode never silently upgrades encrypted-only provider");
        Check(ServiceBoard.Initial(6,0,DnsTransport.Https).State==BoardState.Untested,"Declared service starts untested, not green");
        Check(BoardCell.From(Result(ServiceReply.Response,ServiceReply.Login)).State==BoardState.Login,"Missing login remains distinct from full responses");
        Check(BoardCell.From(Result(ServiceReply.Response,ServiceReply.Restricted)).State==BoardState.Failed,"Website success cannot hide regional API failure");
        Check(BoardCell.From(Result(ServiceReply.Response,ServiceReply.Unknown)).State==BoardState.Unknown,"Unknown API response cannot become green");
        Check(BoardCell.From(Result(ServiceReply.BrowserCheck,ServiceReply.Login)).State==BoardState.Browser,"Browser challenge stays distinct from login");
        Check(BoardCell.From(Result(ServiceReply.Response,ServiceReply.Response)).State==BoardState.Response,"Verified endpoint responses get response caption only");
        Check(BoardCell.From(Result(ServiceReply.Response)).CheckedUtc>DateTime.UtcNow.AddSeconds(-2),"Every finished cell carries freshness timestamp");
        var reply=new ServiceHttpReply{Status=403,Body="{\"error\":{\"message\":\"Method does not allow unregistered callers\"}}"};reply.Headers["Content-Type"]="application/json";
        Check(ServiceAvailability.Classify(ServiceAvailability.Targets[0].Endpoints[1],reply)==ServiceReply.Login,"Google unregistered callers without API-key words means login, not failed DNS");
        Check(ServiceBoard.Targets.Any(t=>t.Name=="Spotify")&&ServiceBoard.Targets.Any(t=>t.Name=="Antigravity")&&ServiceBoard.Targets.Any(t=>t.Name=="Xbox"),"Map covers every declared desktop service");
        foreach(bool dark in new[]{true,false})using(var window=new ServiceBoardWindow(dark,DnsTransport.Https,()=>{})){
            window.CreateControl();window.PerformLayout();var grid=(DataGridView)typeof(ServiceBoardWindow).GetField("grid",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
            Check(grid.Rows.Count==Dns.Name.Length&&!grid.Rows[1].Visible&&!grid.Rows[2].Visible&&!grid.Rows[3].Visible,"Retired providers do not displace working candidates in "+(dark?"dark":"light")+" map");
            Check(!window.Running&&window.SnapshotCurrent,"Opening map never starts network calls automatically");
            Check(grid.Rows[6].Cells[1].Value.ToString()=="Не проверено","Visible MagicDNS starts with honest untested status");
            Check(grid.Columns[0].Frozen&&!grid.Columns[7].Visible,"Names stay pinned; unavailable Xbox service column stays hidden");
            Check(grid.Columns.Cast<DataGridViewColumn>().All(c=>c.SortMode==DataGridViewColumnSortMode.NotSortable),"Sorting cannot attach another provider's result to a DNS row");
        }
        Console.WriteLine("All "+count+" service map checks passed; fake replies, no network changes.");
    }
}
