$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$dir=Join-Path $env:TEMP ('SmartDNS-artifact-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$inspector=Join-Path $dir 'Inspector.exe'
& $compiler /noconfig /nologo /target:exe /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Xml.dll ("/out:"+$inspector) (Join-Path $root 'tools/ArtifactInspection.cs')
if($LASTEXITCODE -ne 0){throw 'Inspector compilation failed'}
$manifest=Join-Path $root 'app.manifest'
$script:passed=0
function Fixture([string]$name,[string]$source,[string[]]$options=@()){
    $cs=Join-Path $dir ($name+'.cs');$exe=Join-Path $dir ($name+'.exe')
    [IO.File]::WriteAllText($cs,$source,[Text.UTF8Encoding]::new($false))
    & $compiler /noconfig /nologo /target:winexe /r:System.dll ("/out:"+$exe) ("/win32manifest:"+$manifest) $options $cs
    if($LASTEXITCODE -ne 0){throw ('Fixture compilation failed: '+$name)}
    return $exe
}
function Inspect([string]$file,[bool]$expected,[string]$reason){
    $report=Join-Path $dir ([IO.Path]::GetFileNameWithoutExtension($file)+'.json')
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$inspector;$start.Arguments='"'+$file+'" "'+$report+'"'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEnd();$stderr=$process.StandardError.ReadToEnd();$process.WaitForExit()
    $accepted=$process.ExitCode -eq 0;$process.Dispose()
    if($accepted -ne $expected -or (!$expected -and $stderr -notmatch [regex]::Escape($reason))){throw ('Wrong artifact policy result: '+$file+'; '+$stdout+' '+$stderr)}
    $script:passed++;Write-Output ('PASS '+[IO.Path]::GetFileName($file)+' '+$(if($expected){'accepted'}else{'rejected: '+$reason}))
}
$marker=Join-Path $dir 'must-not-run.txt'
$escaped=$marker.Replace('\','\\')
$baseline=Fixture 'ordinary' ('class Program {static Program(){System.IO.File.WriteAllText("'+$escaped+'","Unexpected execution");}static void Main(){}}')
Inspect $baseline $true ''
if(Test-Path -LiteralPath $marker){throw 'Inspector executed the inspected application'}
$script:passed++;Write-Output 'PASS application and static constructor were never executed'
$untrusted=Fixture 'untrusted-dll' 'using System.Runtime.InteropServices; class Program {[DllImport("plugin.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern void Call();static void Main(){}}'
Inspect $untrusted $false 'Unapproved native library'
$unsafe=Fixture 'unrestricted-search' 'using System.Runtime.InteropServices;class Program {[DllImport("kernel32.dll")]static extern void Call();static void Main(){}}'
Inspect $unsafe $false 'restrict search to System32'
$allowed=Fixture 'system32-only' 'using System.Runtime.InteropServices;class Program {[DllImport("kernel32.dll")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern void Call();static void Main(){}}'
Inspect $allowed $true ''
$blob=Join-Path $dir 'harmless-resource.bin';[IO.File]::WriteAllText($blob,'Harmless resource fixture')
$extra=Fixture 'unexpected-resource' 'class Program {static void Main(){}}' @('/resource:'+$blob+',unexpected.exe')
Inspect $extra $false 'Unapproved embedded resource'
[IO.File]::WriteAllBytes($blob,[byte[]](77,90,0,0,0,0))
$embedded=Fixture 'executable-in-image' 'class Program {static void Main(){}}' @('/resource:'+$blob+',service-icons.chatgpt.png')
Inspect $embedded $false 'Embedded executable payload'
$highManifest=Join-Path $dir 'elevated.manifest'
[IO.File]::WriteAllText($highManifest,[IO.File]::ReadAllText($manifest).Replace('asInvoker','requireAdministrator'))
$manifest=$highManifest
$elevated=Fixture 'elevated-at-startup' 'class Program {static void Main(){}}'
Inspect $elevated $false 'Manifest must use asInvoker'
$manifest=Join-Path $root 'app.manifest'
$dependency=Join-Path $dir 'dependency.cs';[IO.File]::WriteAllText($dependency,'public class ExternalDependency {public static void Call(){}}')
$fake=Join-Path $dir 'System.Management.dll'
& $compiler /noconfig /nologo /target:library ("/out:"+$fake) $dependency
if($LASTEXITCODE -ne 0){throw 'Dependency fixture failed'}
$fakeReference=Fixture 'forged-framework-name' 'class Program {static void Main(){ExternalDependency.Call();}}' @('/r:'+$fake)
Inspect $fakeReference $false 'Unapproved assembly reference'
$memory=Fixture 'unreviewed-memory-api' 'using System.Runtime.InteropServices;class Program {[DllImport("kernel32.dll",EntryPoint="ReadProcessMemory")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern void HarmlessLookingAlias();static void Main(){}}'
Inspect $memory $false 'Native API needs explicit security review: ReadProcessMemory'
$thread=Fixture 'unreviewed-thread-api' 'using System.Runtime.InteropServices;class Program {[DllImport("ntdll.dll",EntryPoint="NtCreateThreadEx")][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]static extern void AnotherAlias();static void Main(){}}'
Inspect $thread $false 'Native API needs explicit security review: NtCreateThreadEx'
Write-Output ('All '+$script:passed+' artifact policy checks passed; inspected files were never executed.')