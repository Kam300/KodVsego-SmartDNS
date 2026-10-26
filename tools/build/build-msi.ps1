param([string]$Wix,[string]$SourceExe,[string]$OutputDirectory,[string]$TestId)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root 'installer\MsiHelpers.ps1')
if (!$Wix) { $Wix=Join-Path $env:LOCALAPPDATA 'KodVsego\BuildTools\wix-5.0.2\wix.exe' }
$Wix=(Resolve-Path -LiteralPath $Wix).Path
$toolVersion=(& $Wix --version) -join ''
if ($LASTEXITCODE -ne 0 -or $toolVersion -notmatch '^5\.0\.2\+') { throw 'This prototype requires WiX CLI 5.0.2.' }
$version=[regex]::Match([IO.File]::ReadAllText((Join-Path $root 'src\windows\Properties\AssemblyInfo.cs')),'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
if (!$OutputDirectory) { $OutputDirectory=Join-Path $root ('dist\'+$version) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (!$SourceExe) {
    $appDirectory=Join-Path $OutputDirectory 'application'
    & (Join-Path $root 'tools\Build-VerifiedWindows.ps1') -OutputDirectory $appDirectory
    $SourceExe=Join-Path $appDirectory ('KodVsego_SmartDNS.'+$version+'.exe')
    & (Join-Path $root 'tools\Test-WindowsArtifact.ps1') -File $SourceExe -OutputPath (Join-Path $appDirectory 'artifact-policy.json')
}
$SourceExe=(Resolve-Path -LiteralPath $SourceExe).Path
$metadata=[Diagnostics.FileVersionInfo]::GetVersionInfo($SourceExe)
if ($metadata.ProductName -notlike '*Smart DNS*') { throw 'MSI input is not a Smart DNS application.' }
$version=$metadata.ProductVersion
$msiVersion=ConvertTo-MsiVersion $version
if ($metadata.FileVersion -ne ($msiVersion+'.0')) { throw 'EXE FileVersion must equal the mapped MSI version plus .0, so stable releases also replace test-build files.' }
$family='production'
$folder='SmartDNS-MSI'
$productName='Код Всего · Smart DNS (MSI)'
$shortcut='Smart DNS (MSI)'
$registry='Software\KodVsego\SmartDNS\Msi'
if ($TestId) {
    if ($TestId -notmatch '^[a-f0-9]{32}$') { throw 'TestId must be a lowercase GUID in N format.' }
    $family='test/'+$TestId
    $folder='SmartDNS-MSI-Test-'+$TestId
    $productName='Smart DNS MSI Test '+$TestId
    $shortcut='Smart DNS MSI Test '+$TestId
    $registry='Software\KodVsego\SmartDNS\MsiPackagingTests\'+$TestId
}
$upgradeCode=Get-MsiIdentity $family
$productCode=Get-MsiIdentity ($family+'/'+$version)
$inputs=Join-Path $OutputDirectory ('msi-inputs-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $inputs | Out-Null
$licenseRtf=Join-Path $inputs 'license.rtf'
[IO.File]::WriteAllText($licenseRtf,(ConvertTo-LicenseRtf ([IO.File]::ReadAllText((Join-Path $root 'installer\license-screen.ru.txt')))),[Text.Encoding]::ASCII)
$marker=Join-Path $inputs 'SmartDNS.Msi.json'
[IO.File]::WriteAllText($marker,([ordered]@{schema=1;installation='msi';version=$version;upgradeCode=$upgradeCode}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$readme=Join-Path $inputs 'readme.txt'
$readmeText=@"
Код Всего · Smart DNS $version — установщик MSI

Установка только для текущего пользователя. Ярлык: меню «Пуск» / $shortcut.
Эта копия размещается отдельно от прежней установки через EXE.
Обновляйте её новым MSI. Встроенная проверка открывает страницу релиза.
При удалении через настройки Windows программа восстанавливает прежние DNS, DoH, IPv6
и свои изменения правил Antigravity, затем очищает настройки приложения и кэш.
При необходимости подтвердите запрос прав администратора Windows.
Если восстановление не завершено, удаление остановится и копии сохранятся.
При обновлении через новый MSI очистка не выполняется.

Исходный код: https://github.com/Kam300/KodVsego-SmartDNS
Канал: https://t.me/TotalC0de
Сообщить об ошибке: https://github.com/Kam300/KodVsego-SmartDNS/issues
"@
[IO.File]::WriteAllText($readme,$readmeText,[Text.UTF8Encoding]::new($true))
$outputFile=Join-Path $OutputDirectory ('KodVsego_SmartDNS.Setup.'+$version+'.msi')
$definitions=[ordered]@{ProjectRoot=$root;ProductName=$productName;MsiVersion=$msiVersion;ProductCode=$productCode;UpgradeCode=$upgradeCode;AppVersion=$version;InstallFolderName=$folder;RegistryBase=$registry;ShortcutName=$shortcut;SourceExe=$SourceExe;MarkerFile=$marker;ReadmeFile=$readme;LicenseRtf=$licenseRtf}
foreach($component in @('Application','MsiMarker','LicenseMIT','LicenseRussian','Readme','FontLicenses','UpstreamLicense','WintunLicense','WintunLibrary')) { $definitions[$component+'Guid']=Get-MsiIdentity ($family+'/component/'+$component) }
$arguments=@('build',(Join-Path $root 'installer\SmartDNS.wxs'),'-arch','x64','-out',$outputFile,'-intermediateFolder',(Join-Path $inputs 'obj'))
foreach($entry in $definitions.GetEnumerator()) { $arguments+=@('-d',($entry.Key+'='+$entry.Value)) }
& $Wix $arguments
if ($LASTEXITCODE -ne 0) { throw 'WiX MSI build or validation failed.' }
$manifest=[ordered]@{SchemaVersion=1;Version=$version;MsiVersion=$msiVersion;ProductCode=$productCode;UpgradeCode=$upgradeCode;TestId=$TestId;RegistryBase=$registry;InstallFolderName=$folder;ShortcutName=$shortcut;WixVersion=$toolVersion;SourceExe=$SourceExe;SourceSHA256=(Get-FileHash -LiteralPath $SourceExe).Hash.ToLowerInvariant();File=$outputFile;SHA256=(Get-FileHash -LiteralPath $outputFile).Hash.ToLowerInvariant()}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'msi-build.json'),($manifest|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS-MSI.txt'),($manifest.SHA256+'  '+[IO.Path]::GetFileName($outputFile)+[Environment]::NewLine),[Text.Encoding]::ASCII)
Write-Output ('MSI built: '+$outputFile)
Write-Output ('SHA256: '+$manifest.SHA256)
