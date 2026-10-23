param([string]$MsiPath,[switch]$SkipInstallation)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'installer\MsiHelpers.ps1')
$script:passed=0
function Check([bool]$Condition,[string]$Name) {
    if (!$Condition) { throw ('FAIL '+$Name) }
    $script:passed++; Write-Output ('PASS '+$Name)
}
foreach($pair in @(@('2.23.2-test.1','2.23.2-test.2'),@('2.23.2-test.998','2.23.2'),@('2.23.2','2.23.3-test.1'),@('2.23.64','2.24.0-test.1'))) {
    Check (([version](ConvertTo-MsiVersion $pair[0])) -lt ([version](ConvertTo-MsiVersion $pair[1]))) ('MSI ordering '+$pair[0]+' < '+$pair[1])
}
foreach($invalid in @('2.23.2-test.0','2.23.2-test.999','2.23.2-beta.1','256.0.0','2.23.65','2.23.2.1','2.23.2;cmd')) {
    $rejected=$false; try { $null=ConvertTo-MsiVersion $invalid } catch { $rejected=$true }
    Check $rejected ('Reject unsupported MSI version '+$invalid)
}
Check ((Get-MsiIdentity 'same') -eq (Get-MsiIdentity 'same')) 'Product identity is stable across rebuilds'
Check ((Get-MsiIdentity 'same') -ne (Get-MsiIdentity 'other')) 'Different product families have different identities'
$rtf=ConvertTo-LicenseRtf ('Пример {текста}\'+[Environment]::NewLine+'MIT')
Check ($rtf -match '\\u1055\?' -and $rtf -match '\\\{' -and $rtf -match '\\par ' -and $rtf -notmatch '[^\x00-\x7F]') 'License RTF escapes Cyrillic, braces, backslashes and paragraphs'

$installer=New-Object -ComObject WindowsInstaller.Installer
function Read-MsiRows($Database,[string]$Sql,[int]$Columns) {
    $view=$Database.OpenView($Sql); $null=$view.Execute()
    try {
        while($record=$view.Fetch()) {
            $values=@(); for($index=1;$index -le $Columns;$index++) { $values+=$record.StringData($index) }
            [pscustomobject]@{Values=$values}
        }
    } finally { $null=$view.Close() }
}
if (!$MsiPath) {
    $version=[regex]::Match([IO.File]::ReadAllText((Join-Path $root 'AssemblyInfo.cs')),'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
    $MsiPath=Join-Path $root ('dist\'+$version+'\KodVsego_SmartDNS.Setup.'+$version+'.msi')
}
$MsiPath=(Resolve-Path -LiteralPath $MsiPath).Path
$database=$installer.OpenDatabase($MsiPath,0)
$tables=@(Read-MsiRows $database 'SELECT `Name` FROM `_Tables`' 1 | ForEach-Object {$_.Values[0]})
$customActions=@(); if ('CustomAction' -in $tables) { $customActions=@(Read-MsiRows $database 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`' 4) }
Check ($customActions.Count -eq 1 -and $customActions[0].Values[0] -eq 'PrepareCleanUninstall' -and $customActions[0].Values[1] -eq '18' -and $customActions[0].Values[2] -eq 'SmartDNSExe' -and $customActions[0].Values[3] -eq '--prepare-uninstall [UILevel]') 'Only custom action is the installed application cleanup; no scripts, arbitrary paths or ignored failures'
$allUsers=@(Read-MsiRows $database "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ALLUSERS'" 1)
Check ($allUsers.Count -eq 0) 'Package is strictly per-user'
$registry=@(Read-MsiRows $database 'SELECT `Root` FROM `Registry`' 1)
Check ($registry.Count -gt 0 -and @($registry|Where-Object {$_.Values[0] -ne '1'}).Count -eq 0) 'All application registry entries are HKCU'
$files=@(Read-MsiRows $database 'SELECT `FileName` FROM `File`' 1 | ForEach-Object {$_.Values[0]})
Check ($files.Count -eq 8 -and @($files|Where-Object {$_ -match '(?i)\.exe$'}).Count -eq 1 -and @($files|Where-Object {$_ -match 'SmartDNS.Msi.json$'}).Count -eq 1) 'Payload is one application, MSI marker, readme and five license files'
$sequence=@(Read-MsiRows $database 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 2)
$cleanup=[int]($sequence|Where-Object {$_.Values[0] -eq 'PrepareCleanUninstall'}).Values[1]
$validate=[int]($sequence|Where-Object {$_.Values[0] -eq 'InstallValidate'}).Values[1]
$initialize=[int]($sequence|Where-Object {$_.Values[0] -eq 'InstallInitialize'}).Values[1]
Check ($validate -lt $cleanup -and $cleanup -lt $initialize) 'Network recovery must finish before the file-removal transaction begins'
$cleanupCondition=@(Read-MsiRows $database "SELECT ``Condition`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'PrepareCleanUninstall'" 1)[0].Values[0]
Check ($cleanupCondition -eq 'Installed AND REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE') 'Cleanup runs only during final uninstall, never install, repair or major upgrade'
$remove=[int]($sequence|Where-Object {$_.Values[0] -eq 'RemoveExistingProducts'}).Values[1]
$finalize=[int]($sequence|Where-Object {$_.Values[0] -eq 'InstallFinalize'}).Values[1]
$installExecute=[int]($sequence|Where-Object {$_.Values[0] -eq 'InstallExecute'}).Values[1]
Check ($initialize -lt $installExecute -and $installExecute -lt $remove -and $remove -lt $finalize) 'Major upgrade removes the old product only after installing new files, inside the transaction'
$ui=@(Read-MsiRows $database 'SELECT `Action`, `Sequence` FROM `InstallUISequence`' 2)
$welcome=[int]($ui|Where-Object {$_.Values[0] -eq 'WelcomeDlg'}).Values[1]
$progress=[int]($ui|Where-Object {$_.Values[0] -eq 'ProgressDlg'}).Values[1]
$execute=[int]($ui|Where-Object {$_.Values[0] -eq 'ExecuteAction'}).Values[1]
Check ($welcome -lt $progress -and $progress -lt $execute) 'Welcome and progress dialogs appear in the correct order'
$conditions=@(Read-MsiRows $database 'SELECT `Condition` FROM `LaunchCondition`' 1 | ForEach-Object {$_.Values[0]})
Check (@($conditions|Where-Object {$_ -match 'WINDOWSBUILD'}).Count -eq 1) 'Installer checks Windows 10 or later'

# Full UI has an additional Windows Installer contract which quiet installs do
# not exercise: missing error/files-in-use dialogs cause MSI error 2803.
$dialogs=@(Read-MsiRows $database 'SELECT `Dialog`, `Attributes`, `Control_First` FROM `Dialog`' 3)
$dialogIds=@($dialogs|ForEach-Object {$_.Values[0]})
$errorProperty=@(Read-MsiRows $database "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ErrorDialog'" 1)
Check ($errorProperty.Count -eq 1 -and $errorProperty[0].Values[0] -in $dialogIds) 'ErrorDialog resolves to an authored dialog instead of triggering error 2803'
$errorId=$errorProperty[0].Values[0]
$errorDialog=@($dialogs|Where-Object {$_.Values[0] -eq $errorId})[0]
Check (([int]$errorDialog.Values[1] -band 65536) -ne 0 -and $errorDialog.Values[2] -eq 'ErrorText') 'Error dialog has the MSI error style and ErrorText as its first control'
$errorControls=@(Read-MsiRows $database ('SELECT `Control`, `Type`, `Control_Next` FROM `Control` WHERE `Dialog_` = '''+$errorId+'''') 3)
Check (@($errorControls|Where-Object {$_.Values[0] -eq 'ErrorText' -and $_.Values[1] -eq 'Text'}).Count -eq 1) 'Installer error message has the required ErrorText receiver'
$buttons=@($errorControls|Where-Object {$_.Values[1] -eq 'PushButton'})
Check ($buttons.Count -eq 7 -and @($buttons|Where-Object {$_.Values[2]}).Count -eq 0) 'All seven error responses are available without linking the hidden buttons into the tab order'
$events=@(Read-MsiRows $database 'SELECT `Dialog_`, `Control_`, `Event`, `Argument` FROM `ControlEvent`' 4)
$errorResponses=@($events|Where-Object {$_.Values[0] -eq $errorId -and $_.Values[2] -eq 'EndDialog'}|ForEach-Object {$_.Values[3]}|Sort-Object -Unique)
Check (($errorResponses -join ',') -eq ((@('ErrorAbort','ErrorCancel','ErrorIgnore','ErrorNo','ErrorOk','ErrorRetry','ErrorYes')|Sort-Object) -join ',')) 'Error buttons return the standard Windows Installer response values'
foreach($terminal in @(-3,-2,-1)) {
    $exit=@($ui|Where-Object {[int]$_.Values[1] -eq $terminal})
    Check ($exit.Count -eq 1 -and $exit[0].Values[0] -in $dialogIds) ('Full UI has a valid terminal dialog for sequence '+$terminal)
}
foreach($lockedDialog in @('FilesInUse','MsiRMFilesInUse')) {
    Check ($lockedDialog -in $dialogIds) ('Reserved locked-file dialog exists: '+$lockedDialog)
    $list=@(Read-MsiRows $database ('SELECT `Type`, `Property` FROM `Control` WHERE `Dialog_` = '''+$lockedDialog+''' AND `Control` = ''List''') 2)
    Check ($list.Count -eq 1 -and $list[0].Values[0] -eq 'ListBox' -and $list[0].Values[1] -eq 'FileInUseProcess') ($lockedDialog+' displays the processes reported by MSI')
    Check (@($events|Where-Object {$_.Values[0] -eq $lockedDialog -and $_.Values[2] -eq 'EndDialog' -and $_.Values[3] -eq 'Exit'}).Count -eq 1) ($lockedDialog+' permits cancellation before replacing busy files')
}
$restartEvents=@($events|Where-Object {$_.Values[2] -eq 'RMShutdownAndRestart'})
Check ($restartEvents.Count -eq 1 -and $restartEvents[0].Values[0] -eq 'MsiRMFilesInUse' -and $restartEvents[0].Values[1] -eq 'Continue') 'Restart Manager closes applications only from the explicit confirmation button'
$danglingDialogs=@($events|Where-Object {$_.Values[2] -in @('SpawnDialog','NewDialog') -and $_.Values[3] -notin $dialogIds})
Check ($danglingDialogs.Count -eq 0) 'Every wizard dialog transition resolves to an existing dialog'
$repairMode=@($events|Where-Object {$_.Values[0] -eq 'MaintenanceDlg' -and $_.Values[2] -eq '[REINSTALLMODE]'})
Check ($repairMode.Count -eq 1 -and $repairMode[0].Values[3] -cmatch 'v') 'Interactive repair refreshes the cached MSI so its dialogs are corrected for later uninstall'
$null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
if ($SkipInstallation) { Write-Output ('All '+$script:passed+' MSI structure/version checks passed.'); return }

$testId=[Guid]::NewGuid().ToString('N')
$testRoot=Join-Path $env:TEMP ('SmartDNS-msi-test-'+$testId)
New-Item -ItemType Directory -Path $testRoot | Out-Null
$installFolder=Join-Path $testRoot 'installed'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$packages=@()
function Run-Installer([string[]]$Arguments,[string]$LogName,[bool]$Success=$true) {
    $log=Join-Path $testRoot ($LogName+'.log')
    $argumentsText=($Arguments -join ' ')+' /qn /norestart /l*v "'+$log+'"'
    $process=Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $argumentsText -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(45000)) { throw ('Isolated MSI operation did not finish in 45 seconds: '+$LogName) }
    $code=$process.ExitCode
    if ($Success -and $code -notin @(0,3010)) { throw ('MSI '+$LogName+' failed with exit '+$code+'. Log: '+$log) }
    return $code
}
function Installed([string]$Code) { try { return $installer.ProductState($Code) -eq 5 } catch { return $false } }
try {
    foreach($testVersion in @('2.23.2-test.1','2.23.2-test.2','2.23.2-test.3','2.23.2')) {
        $directory=Join-Path $testRoot $testVersion; New-Item -ItemType Directory -Path $directory | Out-Null
        $source=Join-Path $directory 'Fixture.cs'; $exe=Join-Path $directory 'Fixture.exe'
        $fileVersion=(ConvertTo-MsiVersion $testVersion)+'.0'
        $sourceText='using System; using System.IO; using System.Reflection; [assembly:AssemblyProduct("Smart DNS MSI Fixture")] [assembly:AssemblyVersion("2.23.2.0")] [assembly:AssemblyFileVersion("'+$fileVersion+'")] [assembly:AssemblyInformationalVersion("'+$testVersion+'")] static class Program { static int Main(string[] args) { string root=AppDomain.CurrentDomain.BaseDirectory; if(args.Length==2 && args[0]=="--prepare-uninstall") { if(File.Exists(Path.Combine(root,"block-cleanup")))return 1; File.AppendAllText(Path.Combine(root,"cleanup-completed.txt"),"cleaned"); return 0; } File.WriteAllText(Path.Combine(root,"SHOULD-NOT-LAUNCH.txt"),"unexpected launch"); return 99; } }'
        [IO.File]::WriteAllText($source,$sourceText,[Text.Encoding]::ASCII)
        & $compiler /nologo /target:winexe ("/out:"+$exe) $source
        if ($LASTEXITCODE -ne 0) { throw 'MSI fixture compilation failed' }
        & (Join-Path $root 'build-msi.ps1') -SourceExe $exe -OutputDirectory $directory -TestId $testId
        $packages+=([IO.File]::ReadAllText((Join-Path $directory 'msi-build.json'))|ConvertFrom-Json)
    }
    Check ($packages[0].UpgradeCode -eq $packages[1].UpgradeCode -and $packages[0].ProductCode -ne $packages[1].ProductCode -and $packages[0].UpgradeCode -ne (Get-MsiIdentity 'production')) 'Fixture upgrade family is isolated from the actual product'
    $null=Run-Installer @('/i',('"'+$packages[0].File+'"'),('INSTALLFOLDER="'+$installFolder+'"')) 'install'
    $installedExe=Join-Path $installFolder 'SmartDNS.exe'
    Check ((Installed $packages[0].ProductCode) -and (Get-FileHash -LiteralPath $installedExe).Hash.ToLowerInvariant() -eq $packages[0].SourceSHA256) 'Real MSI install registers the product and installs exact application bytes'
    foreach($file in @('SmartDNS.Msi.json','LICENSE','LICENSE.ru.md','Прочитайте.txt','res\OFL-Unbounded.txt','res\OFL-Manrope.txt','docs\licenses\HB-AI-LICENSE.ru.md')) { Check (Test-Path -LiteralPath (Join-Path $installFolder $file)) ('Installed '+$file) }
    $shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) ($packages[0].ShortcutName+'.lnk')
    Check (Test-Path -LiteralPath $shortcut) 'Start menu shortcut is created for this user'
    Check (Test-Path -LiteralPath ('HKCU:\'+$packages[0].RegistryBase)) 'Product metadata is registered only for this user'
    [IO.File]::WriteAllText($installedExe,'corrupted fixture data')
    $null=Run-Installer @('/fvamus',('"'+$packages[0].File+'"')) 'repair'
    Check ((Get-FileHash -LiteralPath $installedExe).Hash.ToLowerInvariant() -eq $packages[0].SourceSHA256) 'Repair restores a corrupted executable from Windows Installer cache/source'
    $keep=Join-Path $installFolder 'user-settings.keep'; [IO.File]::WriteAllText($keep,'unrelated user data')
    $null=Run-Installer @('/i',('"'+$packages[1].File+'"')) 'upgrade'
    Check (!(Installed $packages[0].ProductCode) -and (Installed $packages[1].ProductCode) -and (Get-FileHash -LiteralPath $installedExe).Hash.ToLowerInvariant() -eq $packages[1].SourceSHA256) 'Major upgrade replaces the old package and preserves the install location'
    $downgradeCode=Run-Installer @('/i',('"'+$packages[0].File+'"')) 'downgrade' $false
    Check ($downgradeCode -eq 1603 -and (Installed $packages[1].ProductCode) -and (Get-FileHash -LiteralPath $installedExe).Hash.ToLowerInvariant() -eq $packages[1].SourceSHA256) 'Downgrade is refused and the newer application remains intact'
    # Fault injection in a disposable fixture only: make its embedded CAB unavailable.
    # InstallFiles must fail before removal of the old product, exercising real rollback.
    $broken=$installer.OpenDatabase($packages[2].File,1)
    $change=$broken.OpenView("UPDATE ``Media`` SET ``Cabinet`` = '#MissingFixtureCabinet'")
    $null=$change.Execute(); $null=$change.Close(); $null=$broken.Commit()
    $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($change)
    $null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($broken)
    $rollbackCode=Run-Installer @('/i',('"'+$packages[2].File+'"')) 'rollback' $false
    $rollbackLog=[IO.File]::ReadAllText((Join-Path $testRoot 'rollback.log'))
    Check ($rollbackCode -eq 1603 -and $rollbackLog -match 'MissingFixtureCabinet' -and $rollbackLog -match 'Rollback' -and $rollbackLog -notmatch 'Doing action: RemoveExistingProducts' -and (Installed $packages[1].ProductCode) -and !(Installed $packages[2].ProductCode) -and (Get-FileHash -LiteralPath $installedExe).Hash.ToLowerInvariant() -eq $packages[1].SourceSHA256) 'Failed file installation rolls back while preserving the previous product and bytes'
    $null=Run-Installer @('/i',('"'+$packages[3].File+'"')) 'upgrade-to-stable'
    Check (!(Installed $packages[1].ProductCode) -and (Installed $packages[3].ProductCode) -and (Get-FileHash -LiteralPath $installedExe).Hash.ToLowerInvariant() -eq $packages[3].SourceSHA256) 'Stable release replaces test build registration and executable bytes'
    Check (!(Test-Path -LiteralPath (Join-Path $installFolder 'SHOULD-NOT-LAUNCH.txt'))) 'Install, repair and upgrade never launch the application'
    $cleanupMarker=Join-Path $installFolder 'cleanup-completed.txt'
    Check (!(Test-Path -LiteralPath $cleanupMarker)) 'Install, repair and major upgrades never invoke cleanup'
    $blockCleanup=Join-Path $installFolder 'block-cleanup';[IO.File]::WriteAllText($blockCleanup,'controlled recovery failure')
    $failedRemoval=Run-Installer @('/x',$packages[3].ProductCode) 'blocked-uninstall' $false
    Check ($failedRemoval -eq 1603 -and (Installed $packages[3].ProductCode) -and (Get-FileHash -LiteralPath $installedExe).Hash.ToLowerInvariant() -eq $packages[3].SourceSHA256 -and (Test-Path -LiteralPath $shortcut) -and !(Test-Path -LiteralPath $cleanupMarker)) 'Recovery failure aborts uninstall before any program files or shortcut are removed'
    Remove-Item -LiteralPath $blockCleanup
    $null=Run-Installer @('/x',$packages[3].ProductCode) 'uninstall'
    Check ((Test-Path -LiteralPath $cleanupMarker) -and [IO.File]::ReadAllText($cleanupMarker) -eq 'cleaned') 'Uninstall invokes cleanup exactly once before removing its executable'
    Check (!(Installed $packages[3].ProductCode) -and !(Test-Path -LiteralPath $installedExe) -and !(Test-Path -LiteralPath $shortcut)) 'Uninstall removes owned files, shortcut and product registration'
    Check ((Test-Path -LiteralPath $keep) -and [IO.File]::ReadAllText($keep) -eq 'unrelated user data') 'Uninstall preserves unrelated user files'
} finally {
    # Only codes generated for our unique fixture family may be cleaned up.
    foreach($package in $packages) {
        if ($package.TestId -ne $testId -or $package.UpgradeCode -ne (Get-MsiIdentity ('test/'+$testId))) { throw 'Unsafe fixture identity' }
        if ($installer.ProductState($package.ProductCode) -ge 0) { $null=Run-Installer @('/x',$package.ProductCode) 'cleanup' }
    }
    Write-Output ('MSI test evidence: '+$testRoot)
}
Write-Output ('All '+$script:passed+' MSI checks passed; only isolated harmless fixtures were installed.')
