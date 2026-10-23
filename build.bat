@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\Sync-ProviderAvailability.ps1
if errorlevel 1 exit /b 1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:"KodVsego_SmartDNS.2.23.5.exe" ^
 /win32icon:res\app.ico /win32manifest:app.manifest ^
 /resource:LICENSE,licenses.MIT /resource:LICENSE.ru.md,licenses.Russian ^
 /resource:res\OFL-Unbounded.txt,licenses.OFL-Unbounded /resource:res\OFL-Manrope.txt,licenses.OFL-Manrope ^
 /resource:docs\licenses\HB-AI-LICENSE.ru.md,licenses.HB-AI-Russian ^
 /resource:res\app.ico,app.ico /resource:res\Unbounded-ExtraBold.ttf,Unbounded-ExtraBold.ttf ^
 /resource:res\Manrope-Bold.ttf,Manrope-Bold.ttf /resource:res\Manrope-Medium.ttf,Manrope-Medium.ttf ^
 /resource:res\service-icons\chatgpt.png,service-icons.chatgpt.png /resource:res\service-icons\claude.png,service-icons.claude.png ^
 /resource:res\service-icons\gemini.png,service-icons.gemini.png /resource:res\service-icons\elevenlabs.png,service-icons.elevenlabs.png ^
 /resource:res\service-icons\xbox.png,service-icons.xbox.png /resource:res\service-icons\spotify.png,service-icons.spotify.png ^
 /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Core.dll /r:System.Web.Extensions.dll XboxDns.cs ProviderAvailability.cs SingleInstance.cs AppUpdate.cs AutoDns.cs NrptCimReader.cs EncryptedDns.cs DohHttp2.cs NetworkCheck.cs DnsProbe.cs NativeDns.cs DnsElevation.cs UninstallCleanup.cs ProcessSecurity.cs VpnCheck.cs StyledDialog.cs AntigravityDns.cs AgTakeover.cs AgCleanup.cs DiagnosticReport.cs BrowserRestart.cs BrowserProfiles.cs AssemblyInfo.cs
