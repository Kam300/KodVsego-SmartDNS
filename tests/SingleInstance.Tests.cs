using System;
using System.Diagnostics;
using HbAi.XboxDns;

class SingleInstanceTests
{
    static int count;
    static void Check(bool condition,string label){if(!condition)throw new Exception(label);count++;}
    static Process Child(string mode,string name)
    {
        return Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--"+mode+" "+name){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardInput=true});
    }
    static int Main(string[] args)
    {
        if(args.Length==2){using(var guard=new SingleInstance(args[1])){Console.WriteLine(guard.IsOwner?"owner":"duplicate");Console.Out.Flush();if(args[0]=="--hold")Console.ReadLine();return guard.IsOwner?0:2;}}
        Check(SingleInstance.CompareVersions("2.22.1-test.10","2.22.1-test.9")>0,"numeric test versions");
        Check(SingleInstance.CompareVersions("2.22.1","2.22.1-test.10")>0,"stable after test");
        Check(SingleInstance.CompareVersions("2.22.0","2.22.1-test.1")<0,"newer core first");
        Check(SingleInstance.CompareVersions("v2.22.1","2.22.1.0")==0,"normalized versions");
        Check(SingleInstance.DuplicateMessage("2.22.1-test.8","2.22.1-test.8")==null,"same version activates silently");
        Check(SingleInstance.DuplicateMessage("2.22.1-test.9","2.22.1-test.8").Contains("Закройте"),"newer launch explains manual replacement");
        Check(SingleInstance.DuplicateMessage("2.22.0","2.22.1").Contains("более новая"),"older launch preserves newer");
        Check(SingleInstance.DuplicateMessage("2.22.1",null).Contains("Не удалось сравнить"),"unknown version handled");
        Check(SingleInstance.ShouldReplace("2.22.1-test.11","2.22.1-test.10"),"newer replaces older");
        Check(!SingleInstance.ShouldReplace("2.22.1-test.10","2.22.1-test.11"),"older only activates");
        Check(!SingleInstance.ShouldReplace("2.22.1","2.22.1"),"equal only activates");
        Check(!SingleInstance.ShouldReplace("2.22.1",null),"unknown never closes");
        int step=0;
        Check(SingleInstance.CompleteHandoff(()=>++step==1,()=>++step==2,()=>++step==3)&&step==3,"close exit acquire ordered");
        step=0;
        Check(!SingleInstance.CompleteHandoff(()=>false,()=>{step++;return true;},()=>{step++;return true;})&&step==0,"failed close stops handoff");
        Check(!SingleInstance.CompleteHandoff(()=>true,()=>false,()=>{step++;return true;})&&step==0,"timeout never opens duplicate");
        Check(!SingleInstance.CompleteHandoff(()=>true,()=>true,()=>false),"lost ownership prevents launch");
        string name=@"Local\SmartDNS.IsolatedTests."+Guid.NewGuid().ToString("N");
        using(var owner=Child("hold",name)){
            try{
                Check(owner.StandardOutput.ReadLine()=="owner","first process owns mutex");
                using(var duplicate=Child("probe",name)){
                    Check(duplicate.StandardOutput.ReadLine()=="duplicate","second process refused");
                    Check(duplicate.WaitForExit(5000)&&duplicate.ExitCode==2,"duplicate exits");
                }
                Check(!owner.HasExited,"running owner stays alive");
            }finally{owner.StandardInput.WriteLine();owner.StandardInput.Flush();if(!owner.WaitForExit(5000))throw new Exception("isolated owner did not exit");}
        }
        using(var next=Child("probe",name)){
            Check(next.StandardOutput.ReadLine()=="owner","lock released after exit");
            Check(next.WaitForExit(5000)&&next.ExitCode==0,"next version can start");
        }
        Console.WriteLine("Single instance: "+count+" checks passed");return 0;
    }
}
