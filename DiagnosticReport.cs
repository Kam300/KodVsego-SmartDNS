using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HbAi.XboxDns
{
    static class DiagnosticReport
    {
        internal const string Contact = "https://t.me/TotalCodeAdmin";
        internal static bool ShouldOffer(string title, string message)
        {
            string text = (message ?? "").ToLowerInvariant();
            string[] routine = { "запрос прав администратора отменён", "отключите vpn", "нет сетевого подключения", "нет подключения к сети", "сохраните работу", "сохранения файлов", "ждёт сохранения данных", "браузер работает в фоне", "яндекс остался в фоновом режиме", "ещё работает в фоне", "повторный запуск отменён", "оба dns-сервера не ответили", "сервер не ответил через https", "отключите dns-настройку прежнего", "сначала отключите режим doh" };
            return !routine.Any(text.Contains);
        }
        static readonly object gate = new object();
        static readonly Queue<string> events = new Queue<string>();
        static readonly Queue<string> journal = new Queue<string>();
        static int journalBytes;static long removed;
        internal static string Scrub(string value)
        {
            string text = value ?? "";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) text = Regex.Replace(text, Regex.Escape(home), "%USERPROFILE%", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"(?i)(bearer\s+)[^\s,;""']+", "$1<hidden>");
            text = Regex.Replace(text, @"(?i)((?:access[_-]?token|refresh[_-]?token|api[_-]?key|password|secret|authorization|token)[""']?\s*[:=]\s*[""']?)[^\s,;""'}]+", "$1<hidden>");
            text = Regex.Replace(text,@"(?im)^(\s*(?:set-cookie|cookie)\s*:)[^\r\n]*","$1 <hidden>");
            return text;
        }
        internal static string TemplateLabel(string template)
        {
            if (template == null) return "(automatic)";
            if (EncryptedDns.Templates.Contains(template) || RuntimeProviders.All.Any(p => p.dot == template)) return template;
            Uri uri;
            return Uri.TryCreate(template,UriKind.Absolute,out uri) ? uri.Scheme+"://"+uri.Host+(uri.IsDefaultPort ? "" : ":"+uri.Port)+"/<custom-template>" : "<custom-template>";
        }
        internal static void Record(string operation, string detail)
        {
            string text = Scrub(operation + ": " + detail);
            string full=text.Length>32768?text.Substring(0,32768)+" [entry shortened]":text;
            string stamp=DateTime.UtcNow.ToString("O")+" ";
            if (text.Length > 1400) text = text.Substring(0, 1400);
            lock (gate) {
                events.Enqueue(stamp+text);
                while (events.Count > 24) events.Dequeue();
                journal.Enqueue(stamp+full);journalBytes+=Encoding.UTF8.GetByteCount(stamp+full)+2;
                while(journalBytes>2*1024*1024||journal.Count>4096){journalBytes-=Encoding.UTF8.GetByteCount(journal.Dequeue())+2;removed++;}
            }
        }
        internal static string Recent(int maximum=10000)
        {lock(gate){string text=string.Join("\r\n",journal);return text.Length<=maximum?text:"[Older entries omitted]\r\n"+text.Substring(text.Length-maximum);}}
        internal static string Export(string context,Func<string> snapshot=null)
        {
            string header=Create("Общий журнал программы","Текущий запуск; сеть не изменяется при сохранении журнала.",context,snapshot);
            string all;long dropped;lock(gate){all=string.Join("\r\n",journal);dropped=removed;}
            return Scrub(header+"\r\n\r\nПодробный журнал всех операций текущего запуска (UTC):\r\n"+(dropped>0?"[Удалено старых записей: "+dropped+"; предел 2 МиБ / 4096 записей]\r\n":"")+all+"\r\n");
        }
        internal static string Create(string title, string error, string context, Func<string> snapshot = null)
        {
            var report = new StringBuilder();
            report.AppendLine("Код Всего · Smart DNS · диагностический отчёт");
            report.AppendLine("Версия: " + MainForm.VersionLabel);
            report.AppendLine("Время UTC: " + DateTime.UtcNow.ToString("O"));
            report.AppendLine("Разработчик: " + Contact);
            report.AppendLine("Отправляется пользователем вручную. Автоматической отправки нет.");
            report.AppendLine(); report.AppendLine("Событие: " + title); report.AppendLine(error);
            report.AppendLine(); report.AppendLine("Состояние приложения:"); report.AppendLine(context);
            report.AppendLine(); report.AppendLine("Чтение системных настроек:");
            try { report.AppendLine((snapshot ?? Snapshot)()); }
            catch (Exception ex) { report.AppendLine("Не удалось собрать часть данных: " + ex.GetType().Name + ": " + ex.Message); }
            report.AppendLine(); report.AppendLine("События текущего запуска:");
            lock (gate) foreach (string entry in events) report.AppendLine(entry);
            report.AppendLine(); report.AppendLine("Не собираются: аккаунты, пароли, история браузера, содержимое файлов и чатов, имя ПК, серийные номера и ключи доступа.");
            string result = Scrub(report.ToString());
            return result.Length > 65536 ? result.Substring(0, 65536) + "\r\n[Отчёт сокращён]" : result;
        }
        static string Snapshot()
        {
            var text = new StringBuilder();
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                if (key != null) text.AppendLine("Windows build: " + key.GetValue("CurrentBuildNumber") + "." + key.GetValue("UBR") + "; release: " + key.GetValue("DisplayVersion"));
            text.AppendLine("OS x64: " + Environment.Is64BitOperatingSystem + "; process x64: " + Environment.Is64BitProcess + "; .NET: " + Environment.Version);
            text.AppendLine("Сетевое подключение: " + NetworkCheck.HasConnection());
            var vpn = VpnCheck.Scan(); text.AppendLine("Other VPN detected: " + (vpn.Failed||vpn.Connections.Any(n=>n!=DnsTunnelNative.Name)) + "; scan failed: " + vpn.Failed);
            var system = new NativeDnsSystem();
            int index = 0;
            foreach (var adapter in system.Adapters(true).Take(16))
            {
                index++;
                try {
                    var nic=System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => {Guid id;return Guid.TryParse(n.Id,out id)&&id==adapter.Id;});
                    if(nic!=null) text.AppendLine("Адаптер "+index+" DNS, сообщённые Windows: "+string.Join(",",nic.GetIPProperties().DnsAddresses.Select(a=>a.ToString())));
                } catch(Exception ex) {text.AppendLine("DNS Windows: "+ex.GetType().Name);}
                foreach (string family in new[] {"IPv4", "IPv6"})
                {
                    if (family == "IPv4" ? !adapter.V4 : !adapter.V6) continue;
                    try {
                        var settings = system.Read(adapter.Id, family);
                        text.AppendLine("Адаптер " + index + " " + family + ": manual=" + settings.Manual + "; DNS=" + string.Join(",", settings.Servers));
                        foreach (var entry in settings.Doh ?? new DohConfig[0])
                            text.AppendLine("  " + (entry.Kind == 2 ? "DoT" : "DoH") + ": index=" + entry.Index + "; flags=" + entry.Flags + "; port=" + entry.Port + "; template=" + TemplateLabel(entry.Template));
                    }
                    catch (Exception ex) { text.AppendLine("Адаптер " + index + " " + family + ": " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            try {
                var rules = NrptRegistryReader.Read(false);
                text.AppendLine("Локальных правил NRPT: " + rules.Length);
                foreach (var rule in rules.Where(EncryptedDns.IsWindowsUpdateException))
                    text.AppendLine("NRPT Windows Update exception: " + rule.Names[0] + "; DNS=" + string.Join(",", rule.Servers) + "; preserved outside adapter DoH");
                foreach (var rule in rules.Where(r => r.Names.Any(n => AntigravityDns.Domains.Any(d => AntigravityDns.Matches(n,d)))).Take(8))
                    text.AppendLine("NRPT Antigravity: targets=" + string.Join(",", rule.Names.Where(n => AntigravityDns.Domains.Contains(n))) + "; DNS=" + string.Join(",", rule.Servers) + "; owned=" + AntigravityDns.Owned(rule));
                text.AppendLine("Правил организации: " + NrptRegistryReader.ReadOrganization().Length);
            }
            catch (Exception ex) { text.AppendLine("NRPT: " + ex.GetType().Name + ": " + ex.Message); }
            return text.ToString();
        }
    }
}
