using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace HbAi.XboxDns
{
    sealed class DnsProbeResult
    {
        internal string Server, Host, Error, Detail;
        internal int Milliseconds;
        internal bool Ok { get { return Error == null; } }
        public override string ToString() { return Server + " · " + Host + ": " + (Ok ? Milliseconds + " мс" : Error); }
    }
    static class DnsProbe
    {
        static DnsProbe() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
        internal static byte[] Query(string host)
        {
            var id = new byte[2]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(id);
            using (var stream = new MemoryStream())
            {
                stream.Write(new byte[] { id[0], id[1], 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 12);
                foreach (string label in host.Split('.'))
                { var bytes = Encoding.ASCII.GetBytes(label); if (bytes.Length == 0 || bytes.Length > 63) throw new IOException("Некорректное имя"); stream.WriteByte((byte)bytes.Length); stream.Write(bytes, 0, bytes.Length); }
                stream.Write(new byte[] { 0, 0, 1, 0, 1 }, 0, 5); return stream.ToArray();
            }
        }
        static int Word(byte[] b, int offset) { if (offset + 2 > b.Length) throw new IOException("Неполный DNS-ответ"); return b[offset] * 256 + b[offset + 1]; }
        static void SkipName(byte[] b, ref int offset)
        {
            for (int count = 0; count < 128; count++)
            {
                if (offset >= b.Length) break;
                int length = b[offset++]; if (length == 0) return;
                if ((length & 192) == 192) { if (offset >= b.Length || (((length & 63) << 8) | b[offset++]) >= b.Length) break; return; }
                if (length > 63 || offset + length > b.Length) break; offset += length;
            }
            throw new IOException("Некорректное имя в DNS-ответе");
        }
        internal static void Validate(byte[] request, byte[] response)
        {
            if (response.Length < request.Length || response[0] != request[0] || response[1] != request[1] ||
                (response[2] & 248) != 128 || Word(response, 4) != 1 ||
                !request.Skip(12).SequenceEqual(response.Skip(12).Take(request.Length - 12))) throw new IOException("Некорректный DNS-ответ");
            if ((response[2] & 2) != 0) throw new IOException("Ответ обрезан (требуется TCP)");
            int code = response[3] & 15; if (code != 0) throw new IOException("DNS-код " + code);
            int offset = request.Length, answers = Word(response, 6); bool address = false;
            for (int i = 0; i < answers; i++)
            {
                SkipName(response, ref offset); int type = Word(response, offset), cls = Word(response, offset + 2), length = Word(response, offset + 8); offset += 10;
                if (offset + length > response.Length) throw new IOException("Неполный DNS-ответ");
                if (type == 1 && cls == 1 && length == 4 && response.Skip(offset).Take(4).Any(b => b != 0)) address = true;
                offset += length;
            }
            if (!address) throw new IOException("Нет IPv4-ответа");
        }
        internal static string[] Addresses(byte[] request, byte[] response)
        {
            Validate(request,response);
            int offset=request.Length, answers=Word(response,6);
            var result=new System.Collections.Generic.List<string>();
            for(int i=0;i<answers;i++) {
                SkipName(response,ref offset);int type=Word(response,offset),cls=Word(response,offset+2),length=Word(response,offset+8);offset+=10;
                if(type==1&&cls==1&&length==4)result.Add(new IPAddress(response.Skip(offset).Take(4).ToArray()).ToString());
                offset+=length;
            }
            return result.Distinct().ToArray();
        }
        internal static DnsProbeResult Check(string server, string host)
        {
            var result = new DnsProbeResult { Server = server, Host = host }; var timer = Stopwatch.StartNew();
            try
            {
                var request = Query(host);
                using (var udp = new UdpClient(AddressFamily.InterNetwork))
                {
                    udp.Client.ReceiveTimeout = 2000; udp.Client.SendTimeout = 2000;
                    udp.Connect(IPAddress.Parse(server), 53); udp.Send(request, request.Length);
                    IPEndPoint sender = null; Validate(request, udp.Receive(ref sender));
                }
            }
            catch (SocketException e) { result.Error = e.SocketErrorCode == SocketError.TimedOut ? "нет ответа (2 с)" : "ошибка сети: " + e.SocketErrorCode; }
            catch (IOException e) { result.Error = e.Message; }
            result.Milliseconds = (int)timer.ElapsedMilliseconds; return result;
        }
        internal static DnsProbeResult CheckHttps(string template, string host)
        {
            var result = new DnsProbeResult { Server = template, Host = host }; var timer = Stopwatch.StartNew();
            var trace=new StringBuilder("Transport=Comss HTTPS to configured IP / other providers WinHTTP with HTTP/2; GeoHide alternate=HTTPS to resolved addresses; original SNI and TLS certificate validation enabled; stage timeout="+DohHttp2.RequestTimeoutMilliseconds+" ms; maximum attempts=2\n");
            try
            {
                var query = Query(host);
                Validate(query,DohHttp2.QueryReliable(template,query,line=>trace.AppendLine(line)));
            }
            catch (Exception ex) { result.Error = ex.Message;trace.AppendLine(ex.ToString()); }
            result.Detail=DiagnosticReport.Scrub(trace.ToString());
            result.Milliseconds = (int)timer.ElapsedMilliseconds; return result;
        }
        internal static DnsProbeResult[] CheckProvider(int provider)
        {
            return CheckProvider(provider, (server, host) => Dns.EncryptedOnly(provider) ? CheckHttps(server, host) : Check(server, host));
        }
        internal static string[] Targets(int provider)
        { return Dns.EncryptedOnly(provider) ? new[] { EncryptedDns.Templates[provider] } : Dns.V4[provider]; }
        internal static DnsProbeResult[] CheckAvailability(int provider)
        {
            var tasks = Targets(provider).Select(server => Task.Factory.StartNew(() => Dns.EncryptedOnly(provider) ? CheckHttps(server, "example.com") : Check(server, "example.com"))).ToArray();
            Task.WaitAll(tasks); return tasks.Select(t => t.Result).ToArray();
        }
        internal static DnsProbeResult[] CheckProvider(int provider, Func<string, string, DnsProbeResult> check)
        {
            var hosts = HostsForProvider(provider);
            var tasks = Targets(provider).SelectMany(server => hosts.Select(name => Task.Factory.StartNew(() => check(server, name)))).ToArray();
            Task.WaitAll(tasks); return tasks.Select(t => t.Result).ToArray();
        }
        internal static string[] HostsForProvider(int provider)
        {
            if (provider < 0 || provider >= Dns.Name.Length) throw new ArgumentOutOfRangeException("provider");
            if (provider == 3) return new[] { "example.com", "xbox.com", "user.auth.xboxlive.com", "xsts.auth.xboxlive.com" };
            if (provider == 6) return new[] { "example.com", "chatgpt.com", "claude.ai", "gemini.google.com", "elevenlabs.io" };
            if (provider == 4) return new[] { "example.com", "chatgpt.com", "generativelanguage.googleapis.com", "open.spotify.com", "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" };
            if (provider == 5) return new[] { "example.com", "chatgpt.com", "claude.ai", "gemini.google.com", "generativelanguage.googleapis.com", "api.elevenlabs.io", "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" };
            return new[] { "example.com", "chatgpt.com", "claude.ai", "generativelanguage.googleapis.com", "api.elevenlabs.io", "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" };
        }
        internal static string Report(int provider, DnsProbeResult[] results)
        {
            string report = Dns.EncryptedOnly(provider) ? "Проверен DoH через HTTPS. Обычный DNS не использовался. Это не проверка DNS браузера.\n\nОтветы не подтверждают доступ к API или отсутствие ошибки 400.\n\n" : "Проверены прямые запросы к выбранным серверам. Это не проверка DNS браузера.\n\nУспешные ответы не подтверждают, что браузер использует этот DNS или сайт доступен. Браузер может использовать DoH, IPv6 DNS или VPN.\n\n";
            foreach (string server in Targets(provider))
            {
                report += server + "\n";
                foreach (var result in results.Where(r => r.Server == server))
                    report += (result.Ok ? "✓ " : "✕ ") + result.Host + " — " + (result.Ok ? result.Milliseconds + " мс" : result.Error) + "\n";
                report += "\n";
            }
            return report + "Настройки DNS не изменялись. Повторить тест можно кнопкой ниже.";
        }
    }
}
