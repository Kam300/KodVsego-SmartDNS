using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace HbAi.XboxDns
{
    sealed class RunningInstance
    {
        internal int ProcessId;
        internal IntPtr Window;
        internal string Version;
    }
    sealed class SingleInstance : IDisposable
    {
        readonly Mutex mutex;
        internal bool IsOwner {get;private set;}
        internal static string MutexName {get{return @"Local\KodVsego.SmartDNS.UI."+WindowsIdentity.GetCurrent().User.Value;}}
        internal SingleInstance(string name)
        {
            var security=new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User,MutexRights.FullControl,AccessControlType.Allow));
            bool created;mutex=new Mutex(false,name,out created,security);
            try{IsOwner=mutex.WaitOne(0);}catch(AbandonedMutexException){IsOwner=true;}
        }
        internal bool AcquireAfterExit(int milliseconds)
        {
            if(IsOwner)return true;
            try{IsOwner=mutex.WaitOne(milliseconds);}catch(AbandonedMutexException){IsOwner=true;}
            return IsOwner;
        }
        internal static bool ShouldReplace(string current,string running)
        {
            try{return CompareVersions(current,running)>0;}catch(FormatException){return false;}
        }
        internal static bool CompleteHandoff(Func<bool> close,Func<bool> wait,Func<bool> acquire)
        {
            return close()&&wait()&&acquire();
        }
        [DllImport("user32.dll",ExactSpelling=true,SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool PostMessageW(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
        internal static bool ReplaceOlder(RunningInstance running,SingleInstance guard)
        {
            try {
                using(var process=Process.GetProcessById(running.ProcessId)) {
                    if(process.MainWindowHandle!=running.Window)return false;
                    return CompleteHandoff(()=>PostMessageW(running.Window,0x0010,IntPtr.Zero,IntPtr.Zero),
                        ()=>process.WaitForExit(15000),()=>guard.AcquireAfterExit(2000));
                }
            }catch(ArgumentException){return guard.AcquireAfterExit(2000);}
            catch(InvalidOperationException){return guard.AcquireAfterExit(2000);}
            catch(Win32Exception){return false;}
        }
        public void Dispose(){if(IsOwner){mutex.ReleaseMutex();IsOwner=false;}mutex.Dispose();}
        internal static int CompareVersions(string left,string right)
        {
            var a=(left??"").Trim().TrimStart('v').Split(new[]{'-'},2);
            var b=(right??"").Trim().TrimStart('v').Split(new[]{'-'},2);
            Version av,bv;
            if(!Version.TryParse(a[0],out av)||!Version.TryParse(b[0],out bv))throw new FormatException("Не удалось прочитать номер версии.");
            av=new Version(av.Major,av.Minor,Math.Max(0,av.Build),Math.Max(0,av.Revision));
            bv=new Version(bv.Major,bv.Minor,Math.Max(0,bv.Build),Math.Max(0,bv.Revision));
            int comparison=av.CompareTo(bv);if(comparison!=0)return comparison;
            if(a.Length==1||b.Length==1)return a.Length==b.Length?0:a.Length==1?1:-1;
            var ap=a[1].Split('.');var bp=b[1].Split('.');
            for(int i=0;i<Math.Min(ap.Length,bp.Length);i++) {
                ulong an,bn;bool numericA=ulong.TryParse(ap[i],out an),numericB=ulong.TryParse(bp[i],out bn);
                comparison=numericA&&numericB?an.CompareTo(bn):numericA!=numericB?(numericA?-1:1):string.CompareOrdinal(ap[i],bp[i]);
                if(comparison!=0)return comparison;
            }
            return ap.Length.CompareTo(bp.Length);
        }
        internal static RunningInstance FindExisting()
        {
            int self,session;using(var current=Process.GetCurrentProcess()){self=current.Id;session=current.SessionId;}
            RunningInstance best=null;
            foreach(var process in Process.GetProcesses())using(process) {
                try {
                    if(process.Id==self||process.SessionId!=session||process.MainWindowHandle==IntPtr.Zero)continue;
                    var info=FileVersionInfo.GetVersionInfo(process.MainModule.FileName);
                    if(info.ProductName!="Код Всего · Smart DNS")continue;
                    var found=new RunningInstance{ProcessId=process.Id,Window=process.MainWindowHandle,Version=info.ProductVersion};
                    if(best==null)best=found;
                    else {try{if(CompareVersions(found.Version,best.Version)>0)best=found;}catch(FormatException){}}
                }catch(Win32Exception){}catch(InvalidOperationException){}catch(System.IO.IOException){}catch(NotSupportedException){}
            }
            return best;
        }
        internal static string DuplicateMessage(string current,string running)
        {
            int comparison;try{comparison=CompareVersions(current,running);}catch(FormatException){return "Smart DNS уже запущена. Не удалось сравнить версии. Используйте открытое окно программы.";}
            if(comparison==0)return null;
            return comparison>0?"Уже работает Smart DNS "+running+".\nВы открыли более новую версию "+current+".\n\nЗакройте работающую копию и запустите новую ещё раз.":
                "Уже работает более новая Smart DNS "+running+".\nВы открыли версию "+current+".\n\nИспользуйте окно работающей программы.";
        }
        [DllImport("user32.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool ShowWindowAsync(IntPtr window,int command);
        [DllImport("user32.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll",ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool IsWindowVisible(IntPtr window);
        internal static void Activate(RunningInstance running)
        {
            AllowSetForegroundWindow(running.ProcessId);
            if(IsIconic(running.Window))ShowWindowAsync(running.Window,9);
            IntPtr popup=GetLastActivePopup(running.Window);
            SetForegroundWindow(popup!=IntPtr.Zero&&IsWindowVisible(popup)?popup:running.Window);
        }
        internal static int Run(Func<Form> makeForm)
        {
            try {
                using(var guard=new SingleInstance(MutexName)) {
                    RunningInstance running=FindExisting();
                    if(!guard.IsOwner||running!=null) {
                        for(int i=0;running==null&&i<15;i++){Thread.Sleep(100);running=FindExisting();}
                        if(running==null)MessageBox.Show("Smart DNS уже запускается. Подождите немного и повторите запуск.","Smart DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);
                        else {
                            if(ShouldReplace(MainForm.VersionLabel,running.Version)) {
                                if(ReplaceOlder(running,guard)) {
                                    using(var form=makeForm())Application.Run(form);
                                    return 0;
                                }
                                Activate(running);
                                MessageBox.Show("Не удалось переключиться на новую версию: работающая копия ещё не закрылась. Дождитесь завершения операции или закройте её окно и повторите запуск.","Smart DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);
                            } else Activate(running);
                        }
                        return 0;
                    }
                    using(var form=makeForm())Application.Run(form);return 0;
                }
            }catch(UnauthorizedAccessException){MessageBox.Show("Не удалось проверить работающую копию. Закройте другие окна Smart DNS и запустите программу обычным способом, без прав администратора.","Smart DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);return 4;}
        }
    }
}
