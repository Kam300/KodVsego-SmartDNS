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
        Check(DiagnosticReport.TemplateLabel(DotDns.Host(0))==DotDns.Host(0)&&DiagnosticReport.TemplateLabel("private.company.test")=="<custom-template>","Published TLS names remain visible while custom TLS names stay hidden");
        DiagnosticReport.Record("Activation","DNS-AI; token=example-secret");
        string report=DiagnosticReport.Create("Ошибка DNS","Failure at "+profile,"provider=DNS-AI",()=>"fake snapshot");
        Check(report.Contains(MainForm.VersionLabel)&&report.Contains("provider=DNS-AI")&&report.Contains("fake snapshot")&&report.Contains(DiagnosticReport.Contact),"Version, event, context, snapshot and contact present");
        Check(!report.Contains(profile)&&!report.Contains("example-secret"),"Whole report including event history is scrubbed");
        string failed=DiagnosticReport.Create("Test","error","context",()=>{throw new IOException("Reading failed");});
        Check(failed.Contains("Reading failed")&&failed.Contains("context"),"Snapshot error does not prevent saving the rest");
        for(int i=0;i<100;i++)DiagnosticReport.Record("entry","event "+i);
        string bounded=DiagnosticReport.Create("Test","error","context",()=>"");
        Check(!bounded.Contains("entry: event 0\r")&&bounded.Contains("entry: event 99"),"History drops old entries");
        string full=DiagnosticReport.Export("Google profile",()=>"fake snapshot");
        Check(full.Contains("entry: event 0\r")&&full.Contains("entry: event 99")&&full.Contains("Google profile"),"Download includes earlier operations beyond the short 24-entry support report");
        Check(!DiagnosticReport.Scrub("Cookie: session=private-cookie\r\nSet-Cookie: private-cookie=abc").Contains("private-cookie"),"Cookie headers cannot leak through detailed logs");
        for(int i=0;i<90;i++)DiagnosticReport.Record("Long journal","unique-entry-"+i+" "+new string('я',16000));
        full=DiagnosticReport.Export("context",()=>"fake snapshot");
        Check(full.Contains("unique-entry-89")&&!full.Contains("unique-entry-0 ")&&full.Contains("Удалено старых записей"),"Large journal retains recent operations and explicitly marks dropped history");
        Check(System.Text.Encoding.UTF8.GetByteCount(full)<2200000,"UTF-8 detailed journal remains bounded even for non-ASCII messages");
        Check(!DiagnosticReport.Recent(1000).Contains("example-secret")&&DiagnosticReport.Recent(1000).Length<1100,"Elevated-operation diagnostic transfer is bounded and scrubbed");
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
