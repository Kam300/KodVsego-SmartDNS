@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:"KodVsego_SmartDNS.2.19.2.exe" ^
 /win32icon:res\app.ico /win32manifest:app.manifest ^
 /resource:res\app.ico,app.ico /resource:res\Unbounded-ExtraBold.ttf,Unbounded-ExtraBold.ttf ^
 /resource:res\Manrope-Bold.ttf,Manrope-Bold.ttf /resource:res\Manrope-Medium.ttf,Manrope-Medium.ttf ^
 /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Core.dll /r:System.Web.Extensions.dll XboxDns.cs NrptCimReader.cs EncryptedDns.cs DohHttp2.cs NetworkCheck.cs DnsProbe.cs NativeDns.cs DnsElevation.cs ProcessSecurity.cs VpnCheck.cs StyledDialog.cs AntigravityDns.cs AgTakeover.cs AgCleanup.cs DiagnosticReport.cs BrowserRestart.cs BrowserProfiles.cs AssemblyInfo.cs
