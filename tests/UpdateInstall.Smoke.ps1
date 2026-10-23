# Builds two isolated harnesses and exercises real update handoff and restart.
# Never touches the installed/running Smart DNS, DNS settings, or GitHub releases.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$testDirectory=Join-Path $env:TEMP ('SmartDNS-update-smoke-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$source=@'
using System;using System.IO;using System.Threading;using System.Windows.Forms;using HbAi.XboxDns;
class UpdateSmoke {
[STAThread]static int Main(string[] args) {
try{return Run(args);}catch(Exception error){File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),"smoke-error.txt"),error.ToString());return 99;}
}
static int Run(string[] args) {
ProcessSecurity.Initialize();
if(args.Length==1&&args[0]=="--install-update")return AppUpdate.Install();
string folder=Path.GetDirectoryName(Application.ExecutablePath);
#if UPDATED
File.WriteAllText(Path.Combine(folder,"new-started.txt"),AppUpdate.HashFile(Application.ExecutablePath));return 0;
#else
if(DnsElevation.IsAdministrator())return 77;
string stage=Path.Combine(AppUpdate.CacheRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
File.WriteAllText(Path.Combine(folder,"stage-path.txt"),stage);
string package=Path.Combine(stage,"update.exe");File.Copy(Path.Combine(folder,"new-package.exe"),package);
var release=new UpdateRelease{Version=System.Diagnostics.FileVersionInfo.GetVersionInfo(package).ProductVersion,Size=new FileInfo(package).Length,Hash=AppUpdate.HashFile(package)};
using(var modified=new FileStream(package,FileMode.Append,FileAccess.Write))modified.WriteByte(1);
bool rejected=false;try{AppUpdate.StartInstall(package,release,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
if(!rejected||File.Exists(Path.Combine(stage,"request.json")))throw new Exception("Changed package was not rejected before helper startup");
File.Copy(Path.Combine(folder,"new-package.exe"),package,true);
using(AppUpdate.OpenValidatedPackage(package,release,CancellationToken.None)) {
    bool blocked=false;try{using(var write=new FileStream(package,FileMode.Open,FileAccess.Write,FileShare.ReadWrite)){} }catch(IOException){blocked=true;}
    if(!blocked)throw new Exception("Verified package was writable while preparing startup");
}
var ticket=AppUpdate.StartInstall(package,release,CancellationToken.None);ticket.Commit();return 0;
#endif
}}
'@
$sourceFile=Join-Path $testDirectory 'Smoke.cs'
[IO.File]::WriteAllText($sourceFile,$source,[Text.UTF8Encoding]::new($true))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$mi=Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll'
$refs=@('/r:System.Core.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Management.dll',"/r:$mi")
# A simulated stable update must stay stable even when the application is currently a prerelease.
$stableInfo=Join-Path $testDirectory 'StableAssemblyInfo.cs'
$info=[IO.File]::ReadAllText((Join-Path $root 'AssemblyInfo.cs'))
$info=[regex]::Replace($info,'AssemblyInformationalVersion\("[^"]+"\)','AssemblyInformationalVersion("99.0.0")')
$info=[regex]::Replace($info,'Assembly(?:File)?Version\("[^"]+"\)',{param($match) $match.Value.Substring(0,$match.Value.IndexOf('("'))+'("99.0.0.0")'})
[IO.File]::WriteAllText($stableInfo,$info,[Text.UTF8Encoding]::new($true))
$sources=@(Get-ChildItem $root -Filter '*.cs' | Where-Object Name -ne 'AssemblyInfo.cs' | ForEach-Object FullName)+@($stableInfo)
foreach($targetName in @('KodVsego_SmartDNS.smoke.exe','SmartDNS.exe')) {
    $phase=Join-Path $testDirectory ([IO.Path]::GetFileNameWithoutExtension($targetName));New-Item -ItemType Directory -Path $phase | Out-Null
    $target=Join-Path $phase $targetName
    $newPackage=Join-Path $phase 'new-package.exe'
    & $compiler /nologo /target:winexe /main:UpdateSmoke "/out:$target" $refs $sources $sourceFile
    if($LASTEXITCODE -ne 0){throw 'Old smoke harness compilation failed'}
    & $compiler /nologo /target:winexe /main:UpdateSmoke /define:UPDATED "/out:$newPackage" $refs $sources $sourceFile
    if($LASTEXITCODE -ne 0){throw 'New smoke harness compilation failed'}
    $oldHash=(Get-FileHash $target).Hash;$newHash=(Get-FileHash $newPackage).Hash
    $start=[Diagnostics.ProcessStartInfo]::new($target);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $parent=[Diagnostics.Process]::Start($start)
    if(!$parent.WaitForExit(20000)){throw 'Smoke parent did not finish'}
    if($parent.ExitCode -eq 77){Write-Output 'SKIP updater smoke requires an ordinary, non-admin user';exit 0}
    if($parent.ExitCode -ne 0){$detail=Join-Path $phase 'smoke-error.txt';if(Test-Path $detail){Get-Content $detail};throw "Smoke parent failed: $($parent.ExitCode)"}
    $watch=[Diagnostics.Stopwatch]::StartNew();$marker=Join-Path $phase 'new-started.txt'
    while(!(Test-Path $marker) -and $watch.ElapsedMilliseconds -lt 20000){Start-Sleep -Milliseconds 100}
    if(!(Test-Path $marker)){throw "Replacement did not restart: $phase"}
    if((Get-Content $marker -Raw).Trim().ToUpperInvariant() -ne $newHash){throw 'Restarted bytes are not the new package'}
    if((Get-FileHash $target).Hash -ne $newHash){throw 'Target EXE was not replaced'}
    $backup=@(Get-ChildItem $phase -Filter '*.previous-*' | Where-Object {(Get-FileHash $_.FullName).Hash -eq $oldHash})
    if($backup.Count -ne 1){throw 'Old EXE rollback backup missing'}
    Write-Output "PASS $targetName : tampered download rejected before execution, verified bytes locked during startup, authenticated replacement and restart succeeded"
    Write-Output 'PASS previous harness bytes preserved for rollback'
    $parent.Dispose()
}
Write-Output ('Temporary evidence: '+$testDirectory)