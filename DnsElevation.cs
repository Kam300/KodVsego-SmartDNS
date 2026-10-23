using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace HbAi.XboxDns
{
    public sealed class DnsReply
    {
        public int Code { get; set; }
        public string Error { get; set; }
    }
    sealed class DnsRequest
    {
        internal bool Enable;
        internal int Set;
        internal Guid Token;
        internal int Parent;
        internal string PipeName { get { return "KodVsego.SmartDns." + Token.ToString("N"); } }
        internal string Arguments
        {
            get { return "--dns-operation " + (Enable ? (Set >= 300 ? "secure-keep-ag " : Set >= 200 ? "secure-enable " : Set >= 100 ? (Set % 2 == 1 ? "ag-force " : "ag-enable ") : "enable ") : Set == 4 ? "uninstall-restore " : Set == 3 ? "ag-cleanup " : Set == 2 ? "ag-disable " : Set == 1 ? "reset-known " : "restore ") +
                (Enable ? (Set >= 300 ? Set - 300 : Set >= 200 ? Set - 200 : Set >= 100 ? (Set - 100) / 2 : Set) : 0).ToString(CultureInfo.InvariantCulture) + " " + Token.ToString("N") + " " + Parent.ToString(CultureInfo.InvariantCulture); }
        }
        internal static bool TryParse(string[] args, out DnsRequest request)
        {
            request = null;
            int set, parent;
            Guid token;
            if (args == null || args.Length != 5 || args[0] != "--dns-operation" ||
                (args[1] != "enable" && args[1] != "secure-enable" && args[1] != "secure-keep-ag" && args[1] != "restore" && args[1] != "reset-known" && args[1] != "ag-enable" && args[1] != "ag-disable" && args[1] != "ag-force" && args[1] != "ag-cleanup" && args[1] != "uninstall-restore") ||
                !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out set) ||
                !Guid.TryParseExact(args[3], "N", out token) || token == Guid.Empty ||
                !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out parent) || parent <= 0 ||
                (args[1] == "enable" || args[1] == "secure-enable" || args[1] == "secure-keep-ag" ? set < 0 || set >= Dns.V4.Length : args[1] == "ag-enable" || args[1] == "ag-force" ? set < 0 || set >= AntigravityDns.ServerSets.Length : set != 0)) return false;
            request = new DnsRequest { Enable = args[1] == "enable" || args[1] == "secure-enable" || args[1] == "secure-keep-ag" || args[1] == "ag-enable" || args[1] == "ag-force",
                Set = args[1] == "uninstall-restore" ? 4 : args[1] == "secure-keep-ag" ? 300 + set : args[1] == "secure-enable" ? 200 + set : args[1] == "ag-force" ? 101 + set * 2 : args[1] == "ag-enable" ? 100 + set * 2 : args[1] == "ag-cleanup" ? 3 : args[1] == "ag-disable" ? 2 : args[1] == "reset-known" ? 1 : set, Token = token, Parent = parent };
            return true;
        }
    }
    static class DnsElevation
    {
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);

        internal static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        internal static string Run(bool enable, int set)
        {
            // Explicit "Run as administrator" remains supported without a second prompt.
            if (IsAdministrator()) return Dns.ExecuteDirect(enable, set);
            if (enable ? set < 0 || (set >= Dns.V4.Length && (set < 100 || set >= 100 + AntigravityDns.ServerSets.Length * 2) && (set < 200 || set >= 200 + Dns.V4.Length) && (set < 300 || set >= 300 + Dns.V4.Length)) : set < 0 || set > 4) return "Неизвестная операция DNS.";
            var request = new DnsRequest { Enable = enable, Set = set,
                Token = Guid.NewGuid(), Parent = Process.GetCurrentProcess().Id };
            using (var pipe = CreateResultPipe(request))
            {
                var connection = pipe.BeginWaitForConnection(null, null);
                string exe = Assembly.GetExecutingAssembly().Location;
                var start = new ProcessStartInfo(exe, request.Arguments) {
                    Verb = "runas", UseShellExecute = true,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
                };
                try
                {
                    using (var child = Process.Start(start))
                    {
                        if (child == null) return "Не удалось запустить операцию DNS.";
                        var timer = Stopwatch.StartNew();
                        while (!connection.IsCompleted)
                        {
                            if (child.WaitForExit(100) && !connection.IsCompleted)
                                return "Операция DNS завершилась без ответа. Настройки проверьте в Windows.";
                            if (timer.ElapsedMilliseconds > 15000)
                                return "Не удалось связаться с процессом изменения DNS.";
                        }
                        pipe.EndWaitForConnection(connection);
                        uint client;
                        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out client) || client != (uint)child.Id)
                            return "Не удалось подтвердить процесс изменения DNS.";
                        using (var reader = new StreamReader(pipe, Encoding.UTF8))
                        {
                            string line = reader.ReadLine();
                            if (line == null || line.Length > 20000) return "Неполный ответ операции DNS.";
                            var reply = new JavaScriptSerializer().Deserialize<DnsReply>(line);
                            return reply != null && reply.Code == 0 ? null :
                                (reply == null || string.IsNullOrEmpty(reply.Error) ? "Ошибка операции DNS." : reply.Error);
                        }
                    }
                }
                catch (Win32Exception error)
                {
                    if (error.NativeErrorCode == 1223) return "Запрос прав администратора отменён. DNS не изменён.";
                    return "Не удалось запросить права администратора: " + error.Message;
                }
            }
        }

        internal static NamedPipeServerStream CreateResultPipe(DnsRequest request)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(request.PipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        }

        // This mode never opens a form or reads commands/scripts from the pipe.
        // Only built-in provider indices, restore or explicit known-server reset are accepted.
        internal static int Handle(string[] args, Func<bool, int, string> execute)
        {
            DnsRequest request;
            if (!DnsRequest.TryParse(args, out request)) return 2;
            if (!IsAdministrator()) return 5;
            return CompleteRequest(request, execute);
        }

        internal static int CompleteRequest(DnsRequest request, Func<bool, int, string> execute)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", request.PipeName, PipeDirection.Out,
                    PipeOptions.None, TokenImpersonationLevel.Identification))
                {
                    pipe.Connect(10000);
                    uint server;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out server) || server != (uint)request.Parent) return 5;
                    string error;
                    try { error = execute(request.Enable, request.Set); }
                    catch (Exception failure) { error = failure.Message; }
                    var reply = new DnsReply { Code = error == null ? 0 : 1,
                        Error = error == null ? null : error.Substring(0, Math.Min(error.Length, 4000)) };
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(new JavaScriptSerializer().Serialize(reply));
                        writer.Flush();
                    }
                    return reply.Code;
                }
            }
            catch (Exception) { return 1; }
        }
    }
}
