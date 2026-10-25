using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    interface IUninstallCleanup
    {
        bool NeedsNetworkCleanup();
        string RestoreNetwork(bool interactive);
        void ClearUserData();
    }
    static class UninstallCleanup
    {
        internal const int RestoreOperation=4;
        internal const string UserKey=@"SOFTWARE\KodVsego\SmartDns";
        internal static readonly string[] PreferenceValues={"Theme","DefaultService","HideDnsReminder","GeminiApiKeyProtected","AutoResolverCheck","GoogleTunnelMode","GoogleTunnelProvider","GoogleTunnelProtocol","LocalResolverMode","LocalResolverProvider","LocalResolverProtocol","LocalAntigravityEnabled","LocalAntigravityProvider"};

        internal static string Run(IUninstallCleanup cleanup,bool interactive)
        {
            // All recovery data is checked before deleting preferences or cache.
            if(cleanup.NeedsNetworkCleanup()) {
                string error=cleanup.RestoreNetwork(interactive);
                if(error!=null)return error;
            }
            if(cleanup.NeedsNetworkCleanup())return "Восстановление сети не завершено. Резервные копии сохранены, удаление остановлено.";
            cleanup.ClearUserData();return null;
        }
        internal static bool TryUiLevel(string[] args,out bool interactive)
        {
            interactive=false;int level;
            if(args==null||args.Length!=2||args[0]!="--prepare-uninstall"||
                !int.TryParse(args[1],NumberStyles.None,CultureInfo.InvariantCulture,out level)||level<2||level>5)return false;
            interactive=level>=4;return true;
        }
        internal static int Handle(string[] args)
        {
            bool interactive;if(!TryUiLevel(args,out interactive))return 2;
            string error=null;
            try {
                string exe=Assembly.GetExecutingAssembly().Location;
                if(!AppUpdate.IsMsiManaged(exe))throw new IOException("Режим удаления доступен только установленной MSI-копии.");
                using(var guard=new SingleInstance(SingleInstance.MutexName)) {
                    for(int count=0;;count++) {
                        var running=SingleInstance.FindExisting();if(running==null)break;
                        if(count>=10||!SingleInstance.ReplaceOlder(running,guard))
                            throw new IOException("Smart DNS ещё выполняет операцию. Дождитесь её завершения, закройте приложение и повторите удаление.");
                    }
                    if(!guard.IsOwner)throw new IOException("Другая копия Smart DNS ещё работает. Закройте её и повторите удаление.");
                    error=Run(new WindowsUninstallCleanup(),interactive);
                }
            }catch(Exception failure){error=failure.Message;}
            if(error==null)return 0;
            if(interactive)MessageBox.Show("Удаление остановлено.\n\n"+error+"\n\nФайлы программы и оставшиеся резервные копии сохранены. Повторите удаление после устранения причины.",
                "Не удалось подготовить удаление Smart DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return 1;
        }

        internal static bool NetworkCleanupRequired(IDnsBackupStore dns,IAgTakeoverStore ag,IAgRules rules)
        {
            var dnsBackup=dns.Load();var agBackup=ag.Load();var current=rules.Read();
            return dnsBackup.Count>0||agBackup.Length>0||current.Any(AntigravityDns.Owned);
        }

        internal static void RestoreNetwork(DnsSettingsEngine engine,IDnsBackupStore dns,IAgTakeoverStore ag,IAgRules rules)
        {
            NetworkCleanupRequired(dns,ag,rules); // Validate all recovery data first.
            if(ag.Load().Length>0||rules.Read().Any(AntigravityDns.Owned)) {
                AgTakeover.Restore(rules,ag);AntigravityDns.Change(rules,false);
            }
            if(dns.Load().Count>0)engine.Change(false,new string[0]);
            if(NetworkCleanupRequired(dns,ag,rules))
                throw new IOException("Остались невосстановленные настройки сети. Подключите недоступный адаптер и повторите удаление. Резервные копии сохранены.");
        }

        internal static string RestoreNetworkDirect()
        {
            using(var mutex=new Mutex(false,@"Global\HbAi.SmartDns.Settings")) {
                bool locked=false;
                try {
                    try{locked=mutex.WaitOne(30000);}catch(AbandonedMutexException){locked=true;}
                    if(!locked)return "Другая операция DNS ещё выполняется.";
                    var dns=new RegistryDnsBackupStore();var ag=new RegistryAgTakeoverStore();var rules=new WindowsAgRules();
                    using(var agMutex=new Mutex(false,@"Global\KodVsego.SmartDns.Antigravity")) {
                        bool agLocked=false;
                        try {
                            try{agLocked=agMutex.WaitOne(30000);}catch(AbandonedMutexException){agLocked=true;}
                            if(!agLocked)return "Другая операция Antigravity ещё выполняется.";
                            RestoreNetwork(new DnsSettingsEngine(new NativeDnsSystem(),dns),dns,ag,rules);
                        }finally{if(agLocked)agMutex.ReleaseMutex();}
                    }
                    new DotPolicyTransaction(new WindowsDotPolicy()).Restore();
                    using(var key=Registry.LocalMachine.OpenSubKey(RegistryDnsBackupStore.Key,true))if(key!=null){key.DeleteValue("TunnelPending",false);key.DeleteValue("TunnelAdapters",false);key.DeleteValue("LocalResolverOwner",false);}
                    DeleteEmptyKey(Registry.LocalMachine,RegistryDnsBackupStore.Key);
                    DeleteEmptyKey(Registry.LocalMachine,UserKey);
                    DeleteEmptyKey(Registry.LocalMachine,@"SOFTWARE\HbAi");
                    return null;
                }catch(Exception failure){return failure.Message;}
                finally{if(locked)mutex.ReleaseMutex();}
            }
        }
        internal static bool EmptyKeyExists(RegistryKey hive,string path)
        {using(var key=hive.OpenSubKey(path))return key!=null&&key.ValueCount==0&&key.SubKeyCount==0;}
        internal static void DeleteEmptyKey(RegistryKey hive,string path)
        {if(EmptyKeyExists(hive,path))hive.DeleteSubKey(path,false);}
        internal static void ClearPreferences(RegistryKey hive,string path)
        {
            using(var key=hive.OpenSubKey(path,true)) {
                if(key!=null)foreach(string name in PreferenceValues)key.DeleteValue(name,false);
            }
            DeleteEmptyKey(hive,path);
        }

        // Only the fixed application cache is removed. Refuse junctions/symlinks
        // rather than following them outside the intended AppData directory.
        internal static void CleanCache(string allowedParent,string directory)
        {
            string root=Path.GetFullPath(allowedParent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string target=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Каталог очистки находится вне папки приложения.");
            var directories=new List<string>();var files=new List<string>();
            string ancestor=target;
            while(!string.IsNullOrEmpty(ancestor)) {
                try{CheckCacheEntry(ancestor);}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
                ancestor=Path.GetDirectoryName(ancestor);
            }
            if(!Directory.Exists(target))return;
            CollectCache(target,directories,files);
            foreach(string file in files) {
                CheckCacheEntry(file);File.Delete(file);
            }
            foreach(string folder in directories.AsEnumerable().Reverse()) {
                CheckCacheEntry(folder);Directory.Delete(folder,false);
            }
        }
        static void CheckCacheEntry(string path)
        {if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("В папке кэша найдена ссылка. Автоматическая очистка отменена.");}
        static void CollectCache(string path,List<string> directories,List<string> files)
        {
            CheckCacheEntry(path);directories.Add(path);
            foreach(string entry in Directory.GetFileSystemEntries(path)) {
                CheckCacheEntry(entry);
                if(Directory.Exists(entry))CollectCache(entry,directories,files);else files.Add(entry);
            }
        }
    }
    sealed class WindowsUninstallCleanup : IUninstallCleanup
    {
        public bool NeedsNetworkCleanup()
        {
            return DnsTunnel.Pending||LocalDnsGuardian.MarkerPresent||UninstallCleanup.NetworkCleanupRequired(new RegistryDnsBackupStore(),new RegistryAgTakeoverStore(),new WindowsAgRules())||
                new WindowsDotPolicy().Backup().HasValue||
                UninstallCleanup.EmptyKeyExists(Registry.LocalMachine,RegistryDnsBackupStore.Key)||
                UninstallCleanup.EmptyKeyExists(Registry.LocalMachine,UninstallCleanup.UserKey);
        }
        public string RestoreNetwork(bool interactive)
        {
            if(!interactive&&!DnsElevation.IsAdministrator())return "Для восстановления сети нужны права администратора. Запустите обычное удаление с подтверждением Windows либо сначала выключите DNS в программе.";
            return DnsElevation.Run(false,UninstallCleanup.RestoreOperation);
        }
        public void ClearUserData()
        {
            string parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KodVsego","SmartDNS");
            UninstallCleanup.CleanCache(parent,AppUpdate.CacheRoot);
            UninstallCleanup.ClearPreferences(Registry.CurrentUser,UninstallCleanup.UserKey);
            if(Directory.Exists(parent)&&!Directory.EnumerateFileSystemEntries(parent).Any())Directory.Delete(parent,false);
            string publisher=Path.GetDirectoryName(parent);
            if(Directory.Exists(publisher)&&!Directory.EnumerateFileSystemEntries(publisher).Any())Directory.Delete(publisher,false);
        }
    }
}
