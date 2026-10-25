using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using HbAi.XboxDns;

static class SessionPipeTests
{
    static int passed;
    const string Cause="Не удалось проверить MagicDNS: DNS-код 5 (REFUSED).";
    static void Check(bool ok,string text){if(!ok)throw new Exception(text);passed++;Console.WriteLine("PASS "+text);}
    static int FakeWorker(string[] args)
    {
        string mode=args[1],token=args[4];int parent=int.Parse(args[3]);
        try {
            using(var ready=EventWaitHandle.OpenExisting("Local\\SmartDNS-test-ready-"+token))
            using(var exit=EventWaitHandle.OpenExisting("Local\\SmartDNS-test-exit-"+token)){
                using(var pipe=new NamedPipeClientStream(".",DnsTunnel.PipeName(Guid.ParseExact(token,"N")),PipeDirection.InOut,PipeOptions.Asynchronous)){
                    pipe.Connect(2000);if(!DnsTunnel.ServerMatches(pipe,parent))return 5;
                    using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true})
                    using(var reader=new StreamReader(pipe,Encoding.UTF8,false,1024,true)){
                        writer.WriteLine("STATS ready");if(!ready.WaitOne(5000))return 9;
                        if(mode=="stop"){
                            writer.WriteLine("CONNECTED fake local DNS");
                            var command=reader.ReadLineAsync();if(!command.Wait(5000)||command.Result!="STOP")return 8;
                            writer.WriteLine("STOPPED");
                        }else if(mode=="error"||mode=="delayed-error"){
                            writer.WriteLine("TRACE LOCAL STAGE=encrypted-dns-preflight");writer.WriteLine("ERROR "+Cause);
                        }else if(mode=="stopped"||mode=="pending")writer.WriteLine("STOPPED");
                    }
                }
                // The peer has closed, but its process remains alive until the
                // controller reaches cleanup. Authentication cannot race process exit.
                if(!exit.WaitOne(6000))return 7;
                return mode=="error"||mode=="delayed-error"?1:mode=="abrupt"?42:0;
            }
        }catch(IOException){return 1;}
    }
    static void Integration(string mode)
    {
        EventWaitHandle ready=null,exit=null;Process child=null;TunnelSession session=null;int launches=0;bool everConnected=false;
        using(var done=new ManualResetEventSlim())using(var connected=new ManualResetEventSlim())using(var cleanup=new ManualResetEventSlim()){
            try {
                session=new TunnelSession(info=>{
                    launches++;string token=info.Arguments.Split(' ')[2];
                    ready=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\SmartDNS-test-ready-"+token);
                    exit=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\SmartDNS-test-exit-"+token);
                    child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--fake-session "+mode+" "+info.Arguments){UseShellExecute=false,CreateNoWindow=true});return child;
                },true,()=>mode=="pending");
                session.Changed+=()=>{
                    if(session.Statistics=="ready"&&ready!=null)ready.Set();
                    if(session.Connected){everConnected=true;connected.Set();}
                    if(session.Running&&session.Stopping&&!session.Connected){cleanup.Set();if(mode!="delayed-error"&&exit!=null)exit.Set();}
                    if(!session.Running)done.Set();
                };
                session.Start(6,false);
                if(mode=="stop"){Check(connected.Wait(5000),"Authenticated fake worker can report connected");session.Stop();}
                if(mode=="delayed-error"){
                    Check(cleanup.Wait(5000)&&session.Running&&!session.Connected&&session.Stopping,"Lost connection clears connected state while worker cleanup is outstanding");
                    session.Start(9,false);Check(launches==1,"A second worker cannot start during pipe-failure cleanup");exit.Set();
                }
                Check(done.Wait(10000),"Controller completes after real pipe close: "+mode);
                Check(!session.Running&&!session.Connected&&!session.Stopping,"Controller releases its state after confirmed process exit: "+mode);
                if(mode=="error"||mode=="delayed-error")Check(session.LastError==Cause&&!everConnected,"Original startup DNS failure survives stream disposal: "+mode);
                else if(mode=="pending")Check(session.LastError!=null&&session.LastError.Contains("не подтверждено"),"A STOPPED message never hides pending DNS recovery");
                else if(mode=="abrupt"||mode=="eof-zero")Check(session.LastError!=null&&!session.LastError.Contains("восстановлены"),"An unexplained channel close is not a successful restoration: "+mode);
                else Check(session.LastError==null,"Normal terminal message and stop do not become a broken-pipe error: "+mode+"; error="+session.LastError+(session.LastError==null?"":"; log="+DiagnosticReport.Recent(5000)));
                Check(DiagnosticReport.Recent(20000).Contains("CONTROL END; exit="),"Completion records exit code, terminal state and recovery state: "+mode);
            }finally{
                if(ready!=null)ready.Set();if(exit!=null)exit.Set();if(session!=null)session.Stop();done.Wait(5000);
                if(child!=null)try{if(!child.HasExited){child.Kill();child.WaitForExit(3000);}}catch(InvalidOperationException){}
                if(session!=null)session.Dispose();if(ready!=null)ready.Dispose();if(exit!=null)exit.Dispose();
            }
        }
    }
    static int Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--fake-session")return FakeWorker(args);
        var broken=new IOException("simulated broken pipe",unchecked((int)0x8007006d));
        var outcome=new TunnelSessionOutcome();outcome.Accept("ERROR "+Cause);outcome.Fault(broken,false);
        Check(outcome.Complete(1,false,false)==Cause,"A final worker error outranks broken pipe on writer flush");
        Check(outcome.Complete(1,true,true).Contains(Cause)&&outcome.Complete(1,true,true).Contains("не подтверждено"),"Worker error and failed recovery both survive a user stop");
        outcome=new TunnelSessionOutcome();outcome.Accept("STOPPED");outcome.Fault(broken,false);
        Check(outcome.Complete(0,false,false)==null,"Normal disconnect after STOPPED is not an error");
        outcome.Fault(new IOException("managed PipeStream.Flush has no native HRESULT"),false,true);
        Check(outcome.Complete(0,false,false)==null,"Managed pipe disposal after a terminal message is also harmless without a Win32 error code");
        outcome.Fault(new IOException("unexpected non-pipe failure"),false);
        Check(outcome.Complete(0,false,false).Contains("unexpected non-pipe failure"),"Unrelated I/O failures are not silenced after a terminal message");
        outcome=new TunnelSessionOutcome();outcome.Fault(broken,false);
        Check(outcome.Complete(42,false,false).Contains("Потеряна связь"),"Unexpected pipe failure has a readable summary instead of localized exception text");
        Check(new TunnelSessionOutcome().Complete(0,false,false)!=null,"Exit zero without a terminal message does not claim recovery");
        Check(new TunnelSessionOutcome().Complete(1,false,true)==null,"Explicit cancellation before activation is accepted when recovery is clear");
        foreach(string mode in new[]{"error","stopped","stop","abrupt","eof-zero","pending","delayed-error"})Integration(mode);
        Console.WriteLine("All "+passed+" worker-pipe checks passed; fake child processes and private pipes only, no elevation, DNS writes or driver loading.");return 0;
    }
}
