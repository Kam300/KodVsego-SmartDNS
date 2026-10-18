using System;
using System.IO;
using HbAi.XboxDns;
static class DiagnosticTests
{
    static int passed;
    static void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static void Main()
    {
        foreach(string message in new[]{"Запрос прав администратора отменён. DNS не изменён.", "Отключите VPN и повторите попытку.", NetworkCheck.OfflineMessage, "Сервер не ответил через HTTPS. Настройки не изменены.", "Браузер снова запущен или ещё работает в фоне. Повторный запуск отменён."})
            Check(!DiagnosticReport.ShouldOffer("Ошибка DNS",message),"Routine situation does not offer a report: "+message);
        foreach(string message in new[]{"Windows не вернула результат чтения NRPT.", "SetInterfaceDnsSettings failed: 87", "Не удалось подтвердить применённые настройки DNS."})
            Check(DiagnosticReport.ShouldOffer("Ошибка DNS",message),"Technical failure offers diagnostics: "+message);
        string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check(!DiagnosticReport.Scrub(profile+@"\Documents\file.txt").Contains(profile),"User profile path removed");
        foreach(string secret in new[]{"Bearer example-secret", "token=example-secret", "api_key=example-secret", "\"password\":\"example-secret\"", "refresh_token: example-secret"})
            Check(!DiagnosticReport.Scrub(secret).Contains("example-secret"),"Credential removed: "+secret.Split('=')[0]);
        Check(!DiagnosticReport.TemplateLabel("https://user:password@resolver.test/private-secret?key=example-secret").Contains("secret") && !DiagnosticReport.TemplateLabel("https://user:password@resolver.test/private-secret").Contains("password"),"Custom DoH credentials and path are excluded");
        Check(DiagnosticReport.TemplateLabel(EncryptedDns.Templates[5])==EncryptedDns.Templates[5],"Published DNS-AI template remains useful in report");
        DiagnosticReport.Record("Activation","DNS-AI; token=example-secret");
        string report=DiagnosticReport.Create("Ошибка DNS","Failure at "+profile,"provider=DNS-AI",()=>"fake snapshot");
        Check(report.Contains(MainForm.VersionLabel)&&report.Contains("provider=DNS-AI")&&report.Contains("fake snapshot")&&report.Contains(DiagnosticReport.Contact),"Version, event, context, snapshot and contact present");
        Check(!report.Contains(profile)&&!report.Contains("example-secret"),"Whole report including event history is scrubbed");
        string failed=DiagnosticReport.Create("Test","error","context",()=>{throw new IOException("Reading failed");});
        Check(failed.Contains("Reading failed")&&failed.Contains("context"),"Snapshot error does not prevent saving the rest");
        for(int i=0;i<100;i++)DiagnosticReport.Record("entry","event "+i);
        string bounded=DiagnosticReport.Create("Test","error","context",()=>"");
        Check(!bounded.Contains("entry: event 0\r")&&bounded.Contains("entry: event 99"),"History drops old entries");
        Check(DiagnosticReport.Create("Test",new string('x',100000),"context",()=>"").Length<66000,"Report size is bounded");
        string live=DiagnosticReport.Create("Read-only smoke","No operation","test");
        Check(live.Contains("Windows build:")&&live.Contains("OS x64:")&&!live.Contains(profile),"Real read-only snapshot can be exported without username path");
        string file=Path.Combine(Path.GetTempPath(),"SmartDNS-report-test-"+Guid.NewGuid().ToString("N")+".txt");
        File.WriteAllText(file,report,new System.Text.UTF8Encoding(true));
        Check(File.ReadAllText(file)==report,"UTF-8 report can be written and read back");
        File.Delete(file);
        Console.WriteLine("All "+passed+" diagnostic checks passed; no report sent, no network settings changed.");
    }
}
