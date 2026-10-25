param([switch]$LiveHttps, [string[]]$Only = @(), [string]$OutputDirectory = (Join-Path $env:TEMP 'KodVsego-SmartDNS-tests'), [string]$Compiler)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'tools\Sync-ProviderAvailability.ps1')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = if($Compiler){(Resolve-Path -LiteralPath $Compiler).Path}else{Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'}
$sources = @('XboxDns.cs','ProviderAvailability.cs','SingleInstance.cs','AppUpdate.cs','AutoDns.cs','ServiceAvailability.cs','ResolverCheck.cs','DnsTunnel.cs','LocalDns.cs','DnsTunnelNative.cs','DnsTunnelPackets.cs','DnsTunnelWindow.cs','NrptCimReader.cs','EncryptedDns.cs','DotDns.cs','DohHttp2.cs','NetworkCheck.cs','DnsProbe.cs','NativeDns.cs','DnsElevation.cs','UninstallCleanup.cs','ProcessSecurity.cs','VpnCheck.cs','StyledDialog.cs','ConnectionUi.cs','AntigravityDns.cs','AgTakeover.cs','AgCleanup.cs','DiagnosticReport.cs','BrowserRestart.cs','BrowserProfiles.cs') | ForEach-Object { Join-Path $root $_ }
$miAssembly = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll'
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('mscorlib.dll','System.dll','System.Xml.dll','System.Core.dll','System.Management.dll','System.Web.Extensions.dll','System.Security.dll','System.Windows.Forms.dll','System.Drawing.dll') | ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs += "/r:$miAssembly"
$cases = @(@('NativeDnsTests','NativeDns.Tests.cs'),@('DotDnsTests','DotDns.Tests.cs'),@('ElevationTests','DnsElevation.Tests.cs'),@('ProbeTests','DnsProbe.Tests.cs'),@('NetworkTests','NetworkCheck.Tests.cs'),@('VpnCheckTests','VpnCheck.Tests.cs'),@('BrowserTests','BrowserRestart.Tests.cs'),@('ProfileTests','BrowserProfiles.Tests.cs'),@('AntigravityTests','AntigravityDns.Tests.cs'),@('TakeoverTests','AgTakeover.Tests.cs'),@('CleanupTests','AgCleanup.Tests.cs'),@('DiagnosticTests','DiagnosticReport.Tests.cs'),@('UiPerformanceTests','UiPerformance.Tests.cs'),@('AutoDnsTests','AutoDns.Tests.cs'),@('ServiceAvailabilityTests','ServiceAvailability.Tests.cs'),@('AppUpdateTests','AppUpdate.Tests.cs'),@('SingleInstanceTests','SingleInstance.Tests.cs'))
$cases+=,@('TunnelTests','DnsTunnel.Tests.cs')
$cases+=,@('UninstallTests','UninstallCleanup.Tests.cs')
$cases+=,@('ResolverCheckTests','ResolverCheck.Tests.cs')
$cases+=,@('LocalDnsTests','LocalDns.Tests.cs')
$cases+=,@('ConnectionMenuTests','ConnectionMenu.Tests.cs')
$cases+=,@('LocalAutoTests','LocalAuto.Tests.cs')
$cases+=,@('SessionPipeTests','SessionPipe.Tests.cs')
foreach ($case in $cases) {
    if ($Only.Count -gt 0 -and $case[0] -notin $Only) { continue }
    $exe = Join-Path $OutputDirectory ($case[0] + '.exe')
    $tests = @((Join-Path $PSScriptRoot $case[1]))
    $dpiManifest=if($case[0] -eq 'ConnectionMenuTests'){@('/win32manifest:'+(Join-Path $root 'app.manifest'))}else{@()}
    if ($case[0] -eq 'LocalDnsTests') { $tests += Join-Path $PSScriptRoot 'NativeDns.Tests.cs' }
    if ($case[0] -eq 'TunnelTests') { $tests += Join-Path $PSScriptRoot 'NativeDns.Tests.cs' }
    if ($case[0] -eq 'DotDnsTests') { $tests += Join-Path $PSScriptRoot 'NativeDns.Tests.cs' }
    if ($case[0] -eq 'TakeoverTests') { $tests += Join-Path $PSScriptRoot 'AntigravityDns.Tests.cs' }
    if ($case[0] -eq 'UninstallTests') { $tests += @('NativeDns.Tests.cs','AgTakeover.Tests.cs','AntigravityDns.Tests.cs') | ForEach-Object {Join-Path $PSScriptRoot $_} }
    & $compiler /noconfig /nostdlib+ /langversion:5 /codepage:65001 /nologo /target:exe "/out:$exe" "/main:$($case[0])" $dpiManifest $refs $sources $tests
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $($case[0])" }
    if ($case[0] -eq 'ElevationTests') { & $exe (Join-Path $root 'app.manifest') }
    elseif ($case[0] -eq 'ProbeTests' -and $LiveHttps) { & $exe --https }
    else { & $exe }
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($case[0])" }
    if ($case[0] -eq 'UninstallTests') { & (Join-Path $PSScriptRoot 'UninstallCache.Tests.ps1') -AssemblyPath $exe }
}

if ($Only.Count -eq 0) { & (Join-Path $PSScriptRoot 'ProviderSwitches.Tests.ps1'); & (Join-Path $PSScriptRoot 'VirusTotalReport.Tests.ps1') }
