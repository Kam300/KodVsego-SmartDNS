using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using HbAi.XboxDns;

static class ElevationTests
{
    static int passed;
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static DnsRequest Request(bool enable, int set)
    {
        return new DnsRequest { Enable = enable, Set = set, Token = Guid.NewGuid(), Parent = Process.GetCurrentProcess().Id };
    }
    static DnsReply Exchange(DnsRequest request, Func<bool, int, string> action, out int code)
    {
        using (var pipe = DnsElevation.CreateResultPipe(request))
        {
            var pending = pipe.BeginWaitForConnection(null, null);
            var task = Task.Factory.StartNew(() => DnsElevation.CompleteRequest(request, action));
            Assert(pending.AsyncWaitHandle.WaitOne(5000), "Pipe connection timed out");
            pipe.EndWaitForConnection(pending);
            DnsReply reply;
            using (var reader = new StreamReader(pipe, Encoding.UTF8))
            {
                var line = reader.ReadLine();
                reply = line == null ? null : new JavaScriptSerializer().Deserialize<DnsReply>(line);
            }
            Assert(task.Wait(5000), "Response did not complete");
            code = task.Result;
            return reply;
        }
    }
    static void Main(string[] args)
    {
        Test("All built-in provider arguments round-trip", () => {
            foreach (int set in Enumerable.Range(0, Dns.Name.Length))
            {
                var original = Request(true, set); DnsRequest parsed;
                Assert(DnsRequest.TryParse(original.Arguments.Split(' '), out parsed), "Valid request rejected");
                Assert(parsed.Enable && parsed.Set == set && parsed.Parent == original.Parent && parsed.Token == original.Token, "Request changed");
            }
        });
        Test("Restore arguments round-trip", () => {
            DnsRequest parsed; Assert(DnsRequest.TryParse(Request(false, 0).Arguments.Split(' '), out parsed), "Restore rejected");
            Assert(!parsed.Enable && parsed.Set == 0, "Restore changed");
        });
        Test("Returned adapters use a constrained restore action and authenticated IPC", () => {
            var request=Request(false,5); DnsRequest parsed;
            Assert(request.Arguments.StartsWith("--dns-operation restore-returned 0 "),"Returned restore action missing");
            Assert(DnsRequest.TryParse(request.Arguments.Split(' '),out parsed)&&!parsed.Enable&&parsed.Set==5,"Returned restore changed");
            DnsRequest rejected;
            foreach(string value in new[]{"1","5","https://example.com","-1"})
                Assert(!DnsRequest.TryParse(request.Arguments.Replace("restore-returned 0","restore-returned "+value).Split(' '),out rejected),"Arbitrary restore selector accepted");
            int code;var reply=Exchange(parsed,(on,set)=>{Assert(!on&&set==5,"Wrong returned restore operation");return null;},out code);
            Assert(code==0&&reply.Code==0,"Returned restore IPC failed");
        });
        Test("Encrypted DNS action round-trips without exposing arbitrary templates", () => {
            for (int provider=0;provider<Dns.V4.Length;provider++) {
                var request=Request(true,200+provider); DnsRequest parsed;
                Assert(request.Arguments.StartsWith("--dns-operation secure-enable "),"Encrypted action missing");
                Assert(DnsRequest.TryParse(request.Arguments.Split(' '),out parsed) && parsed.Set==200+provider,"Encrypted provider changed");
            }
            DnsRequest rejected; var good=Request(true,200).Arguments;
            Assert(!DnsRequest.TryParse(good.Replace("secure-enable 0","secure-enable 7").Split(' '),out rejected),"Unknown encrypted provider accepted");
            Assert(!DnsRequest.TryParse(good.Replace("secure-enable 0","secure-enable https://example.com").Split(' '),out rejected),"Arbitrary template accepted");
        });
        Test("Keeping Antigravity requires a separate constrained action", () => {
            foreach (int provider in Enumerable.Range(0, Dns.V4.Length))
            {
                var request = Request(true, 300 + provider); DnsRequest parsed;
                Assert(request.Arguments.StartsWith("--dns-operation secure-keep-ag "), "Consent action missing");
                Assert(DnsRequest.TryParse(request.Arguments.Split(' '), out parsed) && parsed.Enable && parsed.Set == 300 + provider, "Consent flag lost");
            }
            string good = Request(true, 300).Arguments; DnsRequest rejected;
            foreach (string value in new[] {"-1", "7", "https://example.com"})
                Assert(!DnsRequest.TryParse(good.Replace("secure-keep-ag 0", "secure-keep-ag " + value).Split(' '), out rejected), "Unbounded consent action");
            int code; var reply = Exchange(Request(true, 300), (on, set) => {Assert(on && set == 300, "Wrong consent operation"); return null;}, out code);
            Assert(code == 0 && reply.Code == 0, "Consent operation failed IPC");
        });
        Test("Legacy cleanup is a separate constrained operation", () => {
            var request=Request(false,3); DnsRequest parsed;
            Assert(request.Arguments.StartsWith("--dns-operation ag-cleanup 0 "),"Cleanup action missing");
            Assert(DnsRequest.TryParse(request.Arguments.Split(' '),out parsed) && !parsed.Enable && parsed.Set==3,"Cleanup request changed");
            DnsRequest rejected;
            Assert(!DnsRequest.TryParse(request.Arguments.Replace("ag-cleanup 0","ag-cleanup 1").Split(' '),out rejected),"Arbitrary cleanup selector accepted");
            int code;var reply=Exchange(parsed,(on,set)=>{Assert(!on&&set==3,"Wrong cleanup operation");return null;},out code);
            Assert(code==0&&reply.Code==0,"Cleanup IPC failed");
        });
        Test("Explicit known DNS reset round-trips and crosses pipe", () => {
            DnsRequest parsed; Assert(DnsRequest.TryParse(Request(false, 1).Arguments.Split(' '), out parsed), "Reset rejected");
            Assert(!parsed.Enable && parsed.Set == 1, "Reset operation changed");
            int code; var reply = Exchange(parsed, (enable, set) => { Assert(!enable && set == 1, "Wrong reset operation"); return null; }, out code);
            Assert(code == 0 && reply.Code == 0, "Reset response failed");
        });
        Test("Arbitrary commands and malformed arguments are rejected", () => {
            string good = Request(true, 0).Arguments;
            foreach (string invalid in new[] { "", good + " extra", good.Replace("enable", "script"),
                good.Replace("enable 0", "enable 7"), good.Replace("enable 0", "enable -1"),
                good.Replace("enable 0", "restore 3"), good.Replace("enable 0", "restore 1"),
                good.Replace("enable 0", "reset-known 1"), "--dns-operation enable 0 not-a-guid 1",
                good.Replace("enable 0", "ag-enable " + AntigravityDns.ServerSets.Length), good.Replace("enable 0", "ag-disable 1"),
                good.Replace("enable 0", "ag-force " + AntigravityDns.ServerSets.Length), good.Replace("enable 0", "ag-force -1"),
                "--dns-operation enable 0 " + Guid.Empty.ToString("N") + " 1",
                "--dns-operation enable 0 " + Guid.NewGuid().ToString("N") + " 0" })
            {
                DnsRequest parsed; Assert(!DnsRequest.TryParse(invalid.Split(' '), out parsed), "Unsafe request accepted: " + invalid);
            }
        });
        Test("Antigravity operations use constrained named actions", () => {
            foreach (bool enable in new[] { true, false })
            {
                DnsRequest parsed; var request = Request(enable, enable ? 100 : 2);
                Assert(DnsRequest.TryParse(request.Arguments.Split(' '), out parsed), "AG action rejected");
                Assert(parsed.Enable == enable && parsed.Set == (enable ? 100 : 2), "AG action changed");
                int code; var reply = Exchange(parsed, (on, set) => { Assert(on == enable && set == (enable ? 100 : 2), "Wrong AG operation"); return null; }, out code);
                Assert(code == 0 && reply.Code == 0, "AG IPC failed");
            }
        });
        Test("Force takeover is an explicit constrained operation", () => {
            DnsRequest parsed; var request = Request(true, 101);
            Assert(request.Arguments.StartsWith("--dns-operation ag-force 0 "), "Force mode not explicit");
            Assert(DnsRequest.TryParse(request.Arguments.Split(' '), out parsed) && parsed.Enable && parsed.Set == 101, "Force request rejected");
            int code; var reply = Exchange(parsed, (on, set) => { Assert(on && set == 101, "Force operation changed"); return null; }, out code);
            Assert(code == 0 && reply.Code == 0, "Force IPC failed");
        });
        Test("Provider selection survives authenticated IPC for enable and force", () => {
            for (int provider = 0; provider < AntigravityDns.ServerSets.Length; provider++)
                foreach (bool force in new[] { false, true })
                {
                    int expected = 100 + provider * 2 + (force ? 1 : 0);
                    var request = Request(true, expected); DnsRequest parsed;
                    Assert(DnsRequest.TryParse(request.Arguments.Split(' '), out parsed) && parsed.Set == expected, "Provider mapping lost");
                    Assert(request.Arguments.Split(' ')[2] == provider.ToString(), "Provider index not explicit");
                    int code; var reply = Exchange(parsed, (on, set) => { Assert(on && set == expected, "Wrong provider reached helper"); return null; }, out code);
                    Assert(code == 0 && reply.Code == 0, "Provider IPC failed");
                }
        });
        Test("Malformed helper request cannot invoke DNS", () => {
            bool invoked = false;
            int result = DnsElevation.Handle(new[] { "--dns-operation", "anything" }, (enable, set) => { invoked = true; return null; });
            Assert(result == 2 && !invoked, "Malformed helper request executed");
        });
        Test("Successful result crosses authenticated local pipe", () => {
            int calls = 0, code;
            var reply = Exchange(Request(true, 2), (enable, set) => { Assert(enable && set == 2, "Wrong operation"); calls++; return null; }, out code);
            Assert(code == 0 && reply.Code == 0 && reply.Error == null && calls == 1, "Success response invalid");
        });
        Test("Restore errors preserve Unicode and newlines", () => {
            int code; const string error = "Адаптер недоступен\nПовторите восстановление";
            var reply = Exchange(Request(false, 0), (enable, set) => { Assert(!enable, "Not restore"); return error; }, out code);
            Assert(code == 1 && reply.Code == 1 && reply.Error == error, "Error text lost");
        });
        Test("Unexpected operation exception becomes an error response", () => {
            int code; var reply = Exchange(Request(true, 0), (enable, set) => { throw new IOException("Test exception"); }, out code);
            Assert(code == 1 && reply.Error == "Test exception", "Exception response lost");
        });
        Test("Wrong parent process is rejected before any DNS operation", () => {
            bool invoked = false; int code; var request = Request(true, 0); request.Parent++;
            var reply = Exchange(request, (enable, set) => { invoked = true; return null; }, out code);
            Assert(code == 5 && reply == null && !invoked, "Unauthenticated parent executed DNS");
        });
        Test("Excessive error messages are bounded", () => {
            int code; var reply = Exchange(Request(true, 0), (enable, set) => new string('x', 10000), out code);
            Assert(code == 1 && reply.Error.Length == 4000, "Response not bounded");
        });
        Test("Application manifest requests ordinary startup", () => {
            var xml = new XmlDocument(); xml.Load(args[0]);
            var node = xml.SelectSingleNode("//*[local-name()='requestedExecutionLevel']");
            Assert(node.Attributes["level"].Value == "asInvoker", "UI requires elevation");
        });
        Console.WriteLine("All " + passed + " elevation tests passed; no UAC prompts or real DNS changes performed.");
    }
}
