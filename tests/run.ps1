param([switch]$LiveHttps, [string[]]$Only = @(), [string]$OutputDirectory = (Join-Path $env:TEMP 'KodVsego-SmartDNS-tests'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @('XboxDns.cs','AppUpdate.cs','AutoDns.cs','NrptCimReader.cs','EncryptedDns.cs','DohHttp2.cs','NetworkCheck.cs','DnsProbe.cs','NativeDns.cs','DnsElevation.cs','ProcessSecurity.cs','VpnCheck.cs','StyledDialog.cs','AntigravityDns.cs','AgTakeover.cs','AgCleanup.cs','DiagnosticReport.cs','BrowserRestart.cs','BrowserProfiles.cs') | ForEach-Object { Join-Path $root $_ }
$miAssembly = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll'
$refs = @('/r:System.Core.dll','/r:System.Management.dll','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
$refs += "/r:$miAssembly"
$cases = @(@('NativeDnsTests','NativeDns.Tests.cs'),@('ElevationTests','DnsElevation.Tests.cs'),@('ProbeTests','DnsProbe.Tests.cs'),@('NetworkTests','NetworkCheck.Tests.cs'),@('VpnCheckTests','VpnCheck.Tests.cs'),@('BrowserTests','BrowserRestart.Tests.cs'),@('ProfileTests','BrowserProfiles.Tests.cs'),@('AntigravityTests','AntigravityDns.Tests.cs'),@('TakeoverTests','AgTakeover.Tests.cs'),@('CleanupTests','AgCleanup.Tests.cs'),@('DiagnosticTests','DiagnosticReport.Tests.cs'),@('UiPerformanceTests','UiPerformance.Tests.cs'),@('AutoDnsTests','AutoDns.Tests.cs'),@('AppUpdateTests','AppUpdate.Tests.cs'))
foreach ($case in $cases) {
    if ($Only.Count -gt 0 -and $case[0] -notin $Only) { continue }
    $exe = Join-Path $OutputDirectory ($case[0] + '.exe')
    $tests = @((Join-Path $PSScriptRoot $case[1]))
    if ($case[0] -eq 'TakeoverTests') { $tests += Join-Path $PSScriptRoot 'AntigravityDns.Tests.cs' }
    & $compiler /nologo /target:exe "/out:$exe" "/main:$($case[0])" $refs $sources $tests
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $($case[0])" }
    if ($case[0] -eq 'ElevationTests') { & $exe (Join-Path $root 'app.manifest') }
    elseif ($case[0] -eq 'ProbeTests' -and $LiveHttps) { & $exe --https }
    else { & $exe }
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($case[0])" }
}
