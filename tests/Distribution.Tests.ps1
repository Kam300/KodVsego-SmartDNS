param([string]$Wix,[string]$AppCompiler)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$script:passed=0
function Check([bool]$Condition,[string]$Name) {
    if (!$Condition) { throw ('FAIL '+$Name) }
    $script:passed++; Write-Output ('PASS '+$Name)
}
function FileHash([string]$Path) { return (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant() }
$testRoot=Join-Path $env:TEMP ('SmartDNS-distribution-test-'+[Guid]::NewGuid().ToString('N'))
$output=Join-Path $testRoot 'release'
$build=Join-Path $root 'tools\build\build-distribution.ps1'
& $build -Wix $Wix -AppCompiler $AppCompiler -OutputDirectory $output
$manifest=[IO.File]::ReadAllText((Join-Path $output 'release-files.json'))|ConvertFrom-Json
$version=$manifest.Version
$exeName='KodVsego_SmartDNS.'+$version+'.exe'
$zipName='KodVsego_SmartDNS.Portable.'+$version+'.zip'
$msiName='KodVsego_SmartDNS.Setup.'+$version+'.msi'
$expected=@($exeName,$zipName,$msiName)
Check ($manifest.PrimaryInstaller -eq $msiName) 'MSI is the primary installer'
Check ($manifest.Assets.Count -eq 3 -and @(Compare-Object ($expected|Sort-Object) ($manifest.Assets.Name|Sort-Object)).Count -eq 0) 'Release allowlist contains exactly MSI, portable ZIP and update EXE'
Check (!(Test-Path -LiteralPath (Join-Path $output ('KodVsego_SmartDNS.Setup.'+$version+'.exe')))) 'Legacy EXE installer is absent'
Check (@(Get-ChildItem -LiteralPath $output -File).Count -eq 5 -and @(Get-ChildItem -LiteralPath $output -Directory).Count -eq 0) 'Release folder contains only three packages and two release metadata files'
Check (($manifest.Assets|Where-Object {$_.Name -eq $exeName}).Role -eq 'update-executable') 'Standalone EXE remains available to existing updaters'
Check (($manifest.Assets|Where-Object {$_.Name -eq $zipName}).Role -eq 'portable-archive') 'Portable archive has a separate role'
$checksums=[IO.File]::ReadAllLines((Join-Path $output 'SHA256SUMS.txt'))
Check ($checksums.Count -eq 3) 'Checksum list has exactly three release entries'
foreach($asset in $manifest.Assets) {
    $path=Join-Path $output $asset.Name
    Check ((FileHash $path) -eq $asset.SHA256 -and (Get-Item -LiteralPath $path).Length -eq $asset.Bytes -and ($asset.SHA256+'  '+$asset.Name) -cin $checksums) ('Manifest and checksum match '+$asset.Name)
}
$exe=Join-Path $output $exeName
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $output $zipName))
try {
    $files=@($zip.Entries|Where-Object {$_.Name})
    $expectedZip=@($exeName,'wintun.dll','LICENSE','LICENSE.ru.md','res/OFL-Unbounded.txt','res/OFL-Manrope.txt','res/wintun/LICENSE.txt','docs/DNS-TUNNEL.md','docs/LOCAL-DNS.md','docs/RESOLVER-CHECK.md','docs/licenses/HB-AI-LICENSE.ru.md','Прочитайте.txt')
    Check ($files.Count -eq 12 -and @(Compare-Object ($expectedZip|Sort-Object) (@($files|ForEach-Object {$_.FullName.Replace('\','/')})|Sort-Object)).Count -eq 0) 'ZIP contains the app, wintun.dll, licenses, docs and readme; no MSI ownership marker'
    foreach($entry in $files) {
        $name=$entry.FullName.Replace('\','/')
        if ($name -eq 'Прочитайте.txt') { continue }
        $stream=$entry.Open(); $sha=[Security.Cryptography.SHA256]::Create()
        try { $hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() } finally { $stream.Dispose(); $sha.Dispose() }
        $original=Join-Path $root $name
        if ($name -eq $exeName) { $original=$exe }
        elseif ($name -eq 'wintun.dll') { $original=Join-Path $root 'res\wintun\wintun-x64.dll' }
        Check ($hash -eq (FileHash $original)) ('ZIP bytes match '+$name)
    }
} finally { $zip.Dispose() }
& (Join-Path $PSScriptRoot 'MsiPackaging.Tests.ps1') -MsiPath (Join-Path $output $msiName) -SkipInstallation

$before=@($manifest.Assets|ForEach-Object {FileHash (Join-Path $output $_.Name)})
$rejected=$false
try { & $build -Wix $Wix -SourceExe $exe -OutputDirectory $output } catch { $rejected=$_.Exception.Message -like '*already contains artifacts*' }
$after=@($manifest.Assets|ForEach-Object {FileHash (Join-Path $output $_.Name)})
Check ($rejected -and @(Compare-Object $before $after).Count -eq 0) 'Rebuild cannot overwrite previously scanned release files'
$legacyOutput=Join-Path $testRoot 'stale-legacy-output'
New-Item -ItemType Directory -Path $legacyOutput | Out-Null
$legacy=Join-Path $legacyOutput ('KodVsego_SmartDNS.Setup.'+$version+'.exe')
[IO.File]::WriteAllText($legacy,'harmless old package fixture')
$rejected=$false
try { & $build -Wix $Wix -SourceExe $exe -OutputDirectory $legacyOutput } catch { $rejected=$_.Exception.Message -like '*already contains artifacts*' }
Check ($rejected -and [IO.File]::ReadAllText($legacy) -eq 'harmless old package fixture') 'Stale legacy installer cannot enter a new release folder'
$invalid=Join-Path $testRoot 'invalid-source.exe'; [IO.File]::WriteAllText($invalid,'harmless invalid input')
$invalidOutput=Join-Path $testRoot 'invalid-output'
$rejected=$false
try { & $build -Wix $Wix -SourceExe $invalid -OutputDirectory $invalidOutput } catch { $rejected=$true }
Check ($rejected -and !(Test-Path -LiteralPath $invalidOutput)) 'Invalid application cannot produce release artifacts'
$reused=Join-Path $testRoot 'reuse-ready-exe'
& $build -Wix $Wix -SourceExe $exe -SkipAppBuild -OutputDirectory $reused
Check ((FileHash (Join-Path $reused $exeName)) -eq (FileHash $exe)) 'Packaging a ready EXE preserves its exact bytes'
Write-Output ('All '+$script:passed+' distribution checks passed, plus MSI structure checks. No application installed or launched.')
Write-Output ('Distribution test evidence: '+$testRoot)
