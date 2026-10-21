param([Parameter(Mandatory=$true)][string]$Compiler,[string]$SourceExe)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$SourceExe){$version=[regex]::Match([IO.File]::ReadAllText((Join-Path $root 'AssemblyInfo.cs')),'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value;$SourceExe=Join-Path $root "KodVsego_SmartDNS.$version.exe"}
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo($SourceExe).ProductVersion
$id=[Guid]::NewGuid().ToString('N')
$dir=Join-Path $env:TEMP "SmartDNS-packaging-$id"
$out=Join-Path $dir 'packages'
$app=Join-Path $dir 'app'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$name="Smart DNS Packaging Test $id"
$key="HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KodVsego.SmartDNS.PackagingTest.${id}_is1"
$menu=Join-Path ([Environment]::GetFolderPath('Programs')) ($name+'.lnk')
$desktop=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($name+'.lnk')
function Run-Quiet([string]$file,[string]$arguments){
    $p=Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if(!$p.WaitForExit(30000)){throw "Timed out; isolated test process PID $($p.Id)"}
    $p.Refresh();if($p.ExitCode -ne 0){throw "Test setup exited $($p.ExitCode)"}
}
function Assert([bool]$value,[string]$message){if(!$value){throw $message};Write-Output "PASS $message"}
& $Compiler /Qp "/DSourceExe=$SourceExe" "/DOutputPath=$out" "/DTestId=$id" (Join-Path $root 'installer\SmartDNS.iss')
if($LASTEXITCODE -ne 0){throw 'Test installer compilation failed'}
$setup=Join-Path $out "KodVsego_SmartDNS.Setup.$version.exe"
$uninstaller=Join-Path $app 'unins000.exe'
$uninstalled=$false
try {
    $arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /TASKS="desktopicon" /DIR="'+$app+'" /LOG="'+(Join-Path $dir 'install.log')+'"'
    Run-Quiet $setup $arguments
    Assert (Test-Path (Join-Path $app 'SmartDNS.exe')) 'Application installed to isolated per-user folder'
    Assert ((Get-FileHash (Join-Path $app 'SmartDNS.exe')).Hash -eq (Get-FileHash $SourceExe).Hash) 'Installed bytes equal portable EXE'
    foreach($file in @('LICENSE','LICENSE.ru.md','res\OFL-Unbounded.txt','res\OFL-Manrope.txt','docs\licenses\HB-AI-LICENSE.ru.md')) {
        Assert ((Get-FileHash -LiteralPath (Join-Path $app $file)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $root $file)).Hash) ("Installed license matches source: "+$file)
    }
    Assert ((Test-Path $menu) -and (Test-Path $desktop)) 'Start menu and optional desktop shortcuts created'
    $shell=New-Object -ComObject WScript.Shell
    Assert ($shell.CreateShortcut($menu).TargetPath -eq (Join-Path $app 'SmartDNS.exe')) 'Shortcut points to stable executable name'
    $records=@(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Get-ItemProperty | Where-Object {$_.DisplayName -eq $name})
    Assert ($records.Count -eq 1) 'Separate Windows uninstall record created'
    $key=$records[0].PSPath
    Run-Quiet $setup $arguments
    Assert ((Get-FileHash (Join-Path $app 'SmartDNS.exe')).Hash -eq (Get-FileHash $SourceExe).Hash) 'Reinstallation preserves correct application bytes'
    Assert (!(Get-Process -Name SmartDNS -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $app 'SmartDNS.exe')})) 'Silent installation does not start application or change DNS'
    Run-Quiet $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    $uninstalled=$true
    Assert (!(Test-Path (Join-Path $app 'SmartDNS.exe'))) 'Uninstall removes installed app'
    Assert (!(Test-Path $menu) -and !(Test-Path $desktop)) 'Uninstall removes test shortcuts'
    Assert (!(Test-Path $key)) 'Uninstall removes only isolated registration'
    Write-Output "Packaging smoke passed. Logs: $dir"
} finally {
    if(!$uninstalled -and (Test-Path $uninstaller)){Run-Quiet $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'}
}