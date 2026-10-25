using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    enum DnsTransport { Plain, Https, Tls }

    static class DotDns
    {
        internal static string Host(int provider) { return ProviderCatalog.All[provider].dot; }
        internal static string[] Servers(int provider) { return ProviderCatalog.All[provider].windowsDotServers; }
        internal static bool Available(int provider) { return !string.IsNullOrEmpty(Host(provider)); }
        internal static bool InUse()
        {
            try { var system=new NativeDnsSystem(); return system.Adapters(false).Where(a=>a.V4).Any(a=> {var state=system.Read(a.Id,"IPv4"); return state.Doh!=null && state.Doh.Any(d=>d.Kind==2 && (d.Flags&1)!=0); }); }
            catch { return true; } // Never disable a policy when the current state cannot be read.
        }
        internal static byte[] Query(string address, string hostname, byte[] request, int port = 853)
        {var response=Exchange(address,hostname,request,port);DnsProbe.Validate(request,response);return response;}
        internal static byte[] Exchange(string address, string hostname, byte[] request, int port = 853)
        {
            // Connect to a catalog IP, authenticate the catalog hostname. Never trust a
            // plaintext bootstrap response, skip certificate validation or fall back to UDP.
            using (var tcp = new TcpClient(IPAddress.Parse(address).AddressFamily))
            {
                var connecting = tcp.BeginConnect(IPAddress.Parse(address), port, null, null);
                using (connecting.AsyncWaitHandle)
                { if (!connecting.AsyncWaitHandle.WaitOne(3000)) throw new IOException("DoT: сервер не ответил за 3 с."); tcp.EndConnect(connecting); }
                tcp.ReceiveTimeout = 3000; tcp.SendTimeout = 3000;
                using (var tls = new SslStream(tcp.GetStream(), false))
                {
                    var authentication = tls.BeginAuthenticateAsClient(hostname, null, SslProtocols.Tls12, true, null, null);
                    using (authentication.AsyncWaitHandle)
                    { if (!authentication.AsyncWaitHandle.WaitOne(3000)) throw new IOException("DoT: время ожидания TLS истекло."); tls.EndAuthenticateAsClient(authentication); }
                    tls.ReadTimeout = 3000; tls.WriteTimeout = 3000;
                    var frame = new byte[request.Length + 2];
                    frame[0] = (byte)(request.Length >> 8); frame[1] = (byte)request.Length;
                    Buffer.BlockCopy(request, 0, frame, 2, request.Length);
                    tls.Write(frame, 0, frame.Length); tls.Flush();
                    int hi = tls.ReadByte(), lo = tls.ReadByte();
                    if (hi < 0 || lo < 0) throw new IOException("DoT: неполный размер ответа.");
                    var response = new byte[hi * 256 + lo];
                    if (response.Length < 12) throw new IOException("DoT: неполный DNS-ответ.");
                    int offset = 0;
                    while (offset < response.Length)
                    { int read = tls.Read(response, offset, response.Length - offset); if (read == 0) throw new IOException("DoT: соединение закрыто до конца ответа."); offset += read; }
                    return response;
                }
            }
        }
        internal static DnsProbeResult Check(int provider, string host)
        {
            var results = Servers(provider).Select(address => Task.Factory.StartNew(() => {
                var result = new DnsProbeResult { Server = address + ":853 / " + Host(provider), Host = host };
                var watch = Stopwatch.StartNew();
                try { Query(address, Host(provider), DnsProbe.Query(host)); }
                catch (Exception error) { result.Error = error.Message; result.Detail = DiagnosticReport.Scrub(error.ToString()); }
                result.Milliseconds = (int)watch.ElapsedMilliseconds; return result;
            })).ToArray();
            Task.WaitAll(results);
            return results.Select(t => t.Result).OrderBy(r => !r.Ok).ThenBy(r => r.Milliseconds).First();
        }
        internal static string[] Resolve(int provider, string host)
        {
            Exception last = null; var request = DnsProbe.Query(host);
            foreach (string address in Servers(provider))
            { try { return DnsProbe.Addresses(request, Query(address, Host(provider), request)); } catch (Exception e) { last = e; } }
            throw new IOException("DoT не ответил: " + (last == null ? "нет сервера" : last.Message), last);
        }
    }

    interface IDotPolicy
    {
        bool Enabled();
        bool? Backup();
        void Save(bool? value);
        void Set(bool value);
    }
    sealed class DotPolicyTransaction
    {
        readonly IDotPolicy policy;
        internal DotPolicyTransaction(IDotPolicy policy) { this.policy = policy; }
        internal void Enable()
        {
            bool enabled = policy.Enabled();
            if (!enabled)
            {
                if (!policy.Backup().HasValue) policy.Save(false); // Durable before any policy write.
                policy.Set(true);
            }
        }
        internal void Restore()
        {
            bool? before = policy.Backup(); if (!before.HasValue) return;
            // We only ever enabled this bit. If someone disabled it meanwhile, leave it alone.
            if (policy.Enabled()) policy.Set(before.Value);
            policy.Save(null);
        }
    }
    sealed class WindowsDotPolicy : IDotPolicy
    {
        [StructLayout(LayoutKind.Sequential)]
        struct GlobalSettings { public uint Version; public ulong Flags; public IntPtr Hostname, Domain, SearchList; public ulong SettingFlags; }
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern uint GetDnsSettings(ref GlobalSettings value);
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern void FreeDnsSettings(ref GlobalSettings value);
        public bool Enabled()
        {
            var value = new GlobalSettings { Version = 2 };
            uint error = GetDnsSettings(ref value);
            if (error != 0) throw new NotSupportedException("Эта версия Windows не поддерживает системный DoT. Выберите HTTPS.");
            try {
                if ((value.SettingFlags & 0x100UL) != 0) throw new IOException("DoT запрещён политикой Windows. Настройки не изменены.");
                return (value.SettingFlags & 0x80UL) != 0;
            } finally { FreeDnsSettings(ref value); }
        }
        public bool? Backup()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(RegistryDnsBackupStore.Key)) {
                object value = key == null ? null : key.GetValue("DotPolicyBackup");
                if (value == null) return null;
                if (!(value is int) || ((int)value != 0 && (int)value != 1)) throw new IOException("Повреждена копия настройки DoT.");
                return (int)value == 1;
            }
        }
        public void Save(bool? value)
        {
            using (var key = Registry.LocalMachine.CreateSubKey(RegistryDnsBackupStore.Key)) {
                if (value.HasValue) key.SetValue("DotPolicyBackup", value.Value ? 1 : 0, RegistryValueKind.DWord);
                else key.DeleteValue("DotPolicyBackup", false);
                key.Flush();
            }
        }
        public void Set(bool value)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"),
                "dnsclient set global dot=" + (value ? "yes" : "no")) { UseShellExecute = false, CreateNoWindow = true };
            using (var process = Process.Start(info)) {
                if (!process.WaitForExit(10000)) { try { process.Kill(); } catch { } throw new IOException("Windows не завершила настройку DoT."); }
                if (process.ExitCode != 0) throw new IOException("Windows отклонила настройку DoT.");
            }
            if (Enabled() != value) throw new IOException("Windows не подтвердила состояние DoT.");
        }
    }
    static class SystemDnsVerification
    {
        internal static void Verify()
        {
            // Explicit fresh wire query: cached results must not conceal a broken configuration.
            var task = Task.Factory.StartNew(() => {
                foreach (string host in new[] { "example.com", "www.microsoft.com" }) {
                    IntPtr records; int error = DnsQuery(host, 1, 0x00000100 | 0x00000040 | 0x00000008, IntPtr.Zero, out records, IntPtr.Zero);
                    if (records != IntPtr.Zero) DnsRecordListFree(records, 1);
                    if (error == 0) return;
                }
                throw new IOException("Системный DNS после подключения не ответил.");
            });
            try { if (!task.Wait(12000)) throw new IOException("Проверка системного DNS превысила 12 с."); }
            catch (AggregateException e) { throw e.GetBaseException(); }
        }
        [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr records, IntPtr reserved);
        [DllImport("dnsapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern void DnsRecordListFree(IntPtr records, int kind);
    }
}
