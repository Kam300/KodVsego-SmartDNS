# Opt-in live GitHub download test in a hardened standalone .NET executable.
# Does not run downloaded code, install updates, or change network settings.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $env:TEMP ('SmartDNS-update-network-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$source=@'
using System;using System.IO;using System.Reflection;using System.Threading;using HbAi.XboxDns;
class UpdateNetworkSmoke {
static void Direct(string url,Stream output,long limit,bool metadata) {
typeof(AppUpdate).GetMethod("ReceiveDirect",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{url,output,limit,CancellationToken.None,null,metadata});
}
static int Main(string[] args) {
try {
ProcessSecurity.Initialize();
var current=AppUpdate.Check("99.0.0",CancellationToken.None);
if(current!=null)throw new Exception("Unexpected downgrade");
Console.WriteLine("PASS standalone update check with normal DLL hardening and no network changes");
UpdateRelease release;
using(var output=new MemoryStream()) {
Direct(AppUpdate.LatestApi,output,1024*1024,true);
release=AppUpdate.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()),"0.0.0");
}
if(release==null||string.IsNullOrEmpty(release.Hash))throw new Exception("Missing release or GitHub SHA256");
Console.WriteLine("PASS encrypted independent resolver and certificate-validated GitHub API response");
string package=Path.Combine(args[0],"download-only.exe");
using(var output=File.Create(package))Direct(release.Url,output,release.Size,false);
AppUpdate.ValidatePackage(package,release);
Console.WriteLine("PASS direct release download, allowlisted redirect, SHA256 and PE version validation; no downloaded code launched");
return 0;
}catch(Exception ex){Console.WriteLine(ex.ToString());return 1;}
}}
'@
$sourcePath=Join-Path $folder 'Smoke.cs'
[IO.File]::WriteAllText($sourcePath,$source)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$mi=Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll'
$refs=@('/r:System.Core.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Management.dll',"/r:$mi")
$sources=@(& (Join-Path $root 'tools\Read-WindowsSources.ps1') -Root $root | ForEach-Object {Join-Path $root $_})
$exe=Join-Path $folder 'Smoke.exe'
& $compiler /nologo /target:exe /main:UpdateNetworkSmoke "/out:$exe" $refs $sources $sourcePath
if($LASTEXITCODE -ne 0){throw 'Network harness compilation failed'}
& $exe $folder
if($LASTEXITCODE -ne 0){throw 'Live standalone update test failed'}
