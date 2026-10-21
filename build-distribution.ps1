param([string]$Compiler = $env:INNO_SETUP_COMPILER, [switch]$SkipAppBuild)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if (!$Compiler) {
    $command=Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $Compiler=$command.Source }
    else {
        $candidates=@("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
        $Compiler=$candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (!$Compiler -or !(Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 6.7+ and pass -Compiler with the full ISCC.exe path, or set INNO_SETUP_COMPILER.' }
$source=[IO.File]::ReadAllText((Join-Path $root 'AssemblyInfo.cs'))
$version=[regex]::Match($source,'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$') { throw 'Invalid app version' }
if (!$SkipAppBuild) {
    Push-Location $root
    try { & cmd.exe /c build.bat; if ($LASTEXITCODE -ne 0) { throw 'Application build failed' } }
    finally { Pop-Location }
}
$name="KodVsego_SmartDNS.$version.exe"
$exe=Join-Path $root $name
if (!(Test-Path -LiteralPath $exe) -or [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -ne $version) { throw 'Missing EXE or wrong embedded version' }
$out=Join-Path $root "dist\$version"
$portable=Join-Path $out 'portable'
New-Item -ItemType Directory -Path $portable -Force | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $portable $name) -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'LICENSE.ru.md') -Destination $portable -Force
New-Item -ItemType Directory -Path (Join-Path $portable 'res'),(Join-Path $portable 'docs\licenses') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'res\OFL-Unbounded.txt'),(Join-Path $root 'res\OFL-Manrope.txt') -Destination (Join-Path $portable 'res') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\licenses\HB-AI-LICENSE.ru.md') -Destination (Join-Path $portable 'docs\licenses') -Force
$note=@"
Код Всего · Smart DNS $version — без установки

Запустите $name. Для изменения DNS подтвердите запрос прав администратора.
Установка и ярлыки не требуются. Настройки и резервные копии сохраняются в Windows, а не рядом с EXE; они общие с установленной версией.
Перед удалением файла выключите DNS в программе, если нужно восстановить прежние настройки сети.

Исходный код: https://github.com/Kam300/KodVsego-SmartDNS
Поддержка: https://t.me/TotalCodeAdmin
"@
[IO.File]::WriteAllText((Join-Path $portable 'Прочитайте.txt'),$note,[Text.UTF8Encoding]::new($true))
& $Compiler "/DSourceExe=$exe" "/DOutputPath=$out" (Join-Path $root 'installer\SmartDNS.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$setup=Join-Path $out "KodVsego_SmartDNS.Setup.$version.exe"
if (!(Test-Path -LiteralPath $setup)) { throw 'Installer output missing' }
$zip=Join-Path $out "KodVsego_SmartDNS.Portable.$version.zip"
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -Force
# Keep this exact standalone filename: the existing updater selects it in GitHub releases.
Copy-Item -LiteralPath $exe -Destination (Join-Path $out $name) -Force
$assets=@($name,[IO.Path]::GetFileName($setup),[IO.Path]::GetFileName($zip))
$sums=foreach($asset in $assets) { (Get-FileHash -LiteralPath (Join-Path $out $asset) -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$asset }
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'),$sums,[Text.UTF8Encoding]::new($false))
Write-Output "Packages ready: $out"