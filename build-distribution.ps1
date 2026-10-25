param([string]$Wix,[string]$AppCompiler,[string]$SourceExe,[switch]$SkipAppBuild,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$version=[regex]::Match([IO.File]::ReadAllText((Join-Path $root 'AssemblyInfo.cs')),'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+(?:-test\.\d+)?$') { throw 'Invalid distribution version.' }
if (!$OutputDirectory) { $OutputDirectory=Join-Path $root ('dist\'+$version+'\release') }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    if (!(Test-Path -LiteralPath $OutputDirectory -PathType Container)) { throw 'Output directory is a file.' }
    $existing=@(Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object { $_.Name -like 'KodVsego_SmartDNS.*' -or $_.Name -in @('SHA256SUMS.txt','release-files.json') })
    if ($existing.Count) { throw 'Release output already contains artifacts. Choose another OutputDirectory or a new version; existing files are preserved.' }
}
if (!$Wix) { $Wix=Join-Path $env:LOCALAPPDATA 'KodVsego\BuildTools\wix-5.0.2\wix.exe' }
$Wix=(Resolve-Path -LiteralPath $Wix).Path
$work=Join-Path (Split-Path $OutputDirectory -Parent) ('packaging-'+[Guid]::NewGuid().ToString('N'))
$appDirectory=Join-Path $work 'application'
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
$name='KodVsego_SmartDNS.'+$version+'.exe'
$exe=Join-Path $appDirectory $name
if ($SourceExe -or $SkipAppBuild) {
    if (!$SourceExe) { $SourceExe=Join-Path $root $name }
    $SourceExe=(Resolve-Path -LiteralPath $SourceExe).Path
    [IO.File]::Copy($SourceExe,$exe,$false)
} else {
    & (Join-Path $root 'tools\Build-VerifiedWindows.ps1') -Compiler $AppCompiler -OutputDirectory $appDirectory
}
$metadata=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ($metadata.ProductVersion -ne $version -or $metadata.ProductName -notlike '*Smart DNS*') { throw 'Missing application or mismatched product/version.' }
& (Join-Path $root 'tools\Test-WindowsArtifact.ps1') -File $exe -OutputPath (Join-Path $appDirectory 'artifact-policy.json')
$exeHash=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()

# Build all packages in a separate workspace before copying the release allowlist.
$portable=Join-Path $work 'portable'
New-Item -ItemType Directory -Path $portable | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $portable $name)
Copy-Item -LiteralPath (Join-Path $root 'res\wintun\wintun-x64.dll') -Destination (Join-Path $portable 'wintun.dll')
foreach($relative in @('LICENSE','LICENSE.ru.md','res\OFL-Unbounded.txt','res\OFL-Manrope.txt','res\wintun\LICENSE.txt','docs\DNS-TUNNEL.md','docs\LOCAL-DNS.md','docs\RESOLVER-CHECK.md','docs\licenses\HB-AI-LICENSE.ru.md')) {
    $destination=Join-Path $portable $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
}
$note=@"
Код Всего · Smart DNS $version — без установки

Распакуйте архив и запустите $name.
Для изменения DNS подтвердите отдельный запрос прав администратора.
Настройки и резервные копии сохраняются в Windows и общие с установленной версией.
Перед удалением выключите DNS в программе, если нужно восстановить прежние настройки сети.

Исходный код: https://github.com/Kam300/KodVsego-SmartDNS
Канал: https://t.me/TotalC0de
Поддержка: https://t.me/TotalCodeAdmin
"@
[IO.File]::WriteAllText((Join-Path $portable 'Прочитайте.txt'),$note,[Text.UTF8Encoding]::new($true))
$zipName='KodVsego_SmartDNS.Portable.'+$version+'.zip'
$zip=Join-Path $work $zipName
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip
$msiDirectory=Join-Path $work 'msi'
& (Join-Path $root 'build-msi.ps1') -Wix $Wix -SourceExe $exe -OutputDirectory $msiDirectory
$msiName='KodVsego_SmartDNS.Setup.'+$version+'.msi'
$msi=Join-Path $msiDirectory $msiName
$msiBuild=[IO.File]::ReadAllText((Join-Path $msiDirectory 'msi-build.json'))|ConvertFrom-Json
if ($msiBuild.SourceSHA256 -ne $exeHash -or (Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant() -ne $exeHash -or (Get-FileHash -LiteralPath $msi).Hash.ToLowerInvariant() -ne $msiBuild.SHA256) { throw 'Application or MSI changed during packaging.' }
$assets=@(
    [pscustomobject]@{Name=$msiName;Role='msi-installer';Path=$msi},
    [pscustomobject]@{Name=$zipName;Role='portable-archive';Path=$zip},
    [pscustomobject]@{Name=$name;Role='update-executable';Path=$exe}
)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$records=@(foreach($asset in $assets) {
    $destination=Join-Path $OutputDirectory $asset.Name
    [IO.File]::Copy($asset.Path,$destination,$false)
    [ordered]@{Name=$asset.Name;Role=$asset.Role;Bytes=(Get-Item -LiteralPath $destination).Length;SHA256=(Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant()}
})
$manifest=[ordered]@{SchemaVersion=1;Version=$version;PrimaryInstaller=$msiName;Assets=$records}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'release-files.json'),($manifest|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'),[string[]]@($records|ForEach-Object {$_.SHA256+'  '+$_.Name}),[Text.UTF8Encoding]::new($false))
Write-Output ('Release packages ready: '+$OutputDirectory)
Write-Output ('Build evidence: '+$work)
