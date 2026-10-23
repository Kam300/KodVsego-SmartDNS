param([string]$AssemblyPath=(Join-Path $env:TEMP 'KodVsego-SmartDNS-tests/UninstallTests.exe'))
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $AssemblyPath).Path)
$clean=$assembly.GetType('HbAi.XboxDns.UninstallCleanup').GetMethod('CleanCache',[Reflection.BindingFlags]'Static,NonPublic')
$fixture=Join-Path $env:TEMP ('SmartDNS-cache-junction-test-'+[Guid]::NewGuid().ToString('N'))
$allowed=Join-Path $fixture 'app'
$outside=Join-Path $fixture 'outside'
$cache=Join-Path $allowed 'Updates'
$link=Join-Path $cache 'linked-folder'
New-Item -ItemType Directory -Path $cache,$outside -Force | Out-Null
$sentinel=Join-Path $outside 'must-stay.txt'
[IO.File]::WriteAllText($sentinel,'user data')
try {
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    $rejected=$false
    try{$clean.Invoke($null,[object[]]@([string]$allowed,[string]$cache))}catch{$failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException};$rejected=$failure -is [IO.IOException]}
    if(!$rejected -or [IO.File]::ReadAllText($sentinel) -ne 'user data'){throw ('Junction test failed: '+$(if($failure){$failure.GetType().FullName+': '+$failure.Message}else{'no exception'}))}
    Write-Output 'PASS cache junction is rejected before any file deletion; external user data survives'
    [IO.Directory]::Delete($link,$false)
    # Check a junction above the application root as well.
    Remove-Item -LiteralPath $cache
    Remove-Item -LiteralPath $allowed
    New-Item -ItemType Junction -Path $allowed -Target $outside | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $outside 'Updates') | Out-Null
    $rejected=$false
    try{$clean.Invoke($null,[object[]]@([string]$allowed,[string]$cache))}catch{$failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException};$rejected=$failure -is [IO.IOException]}
    if(!$rejected -or [IO.File]::ReadAllText($sentinel) -ne 'user data'){throw 'Ancestor junction was followed'}
    Write-Output 'PASS ancestor junction is rejected before cleanup'
} finally {
    # Remove only our junction itself; do not recurse through it.
    if(Test-Path -LiteralPath $link){[IO.Directory]::Delete($link,$false)}
    if((Test-Path -LiteralPath $allowed) -and ((Get-Item -LiteralPath $allowed -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){[IO.Directory]::Delete($allowed,$false)}
    $absolute=[IO.Path]::GetFullPath($fixture)
    if(!$absolute.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notmatch '^SmartDNS-cache-junction-test-[a-f0-9]{32}$'){throw 'Unsafe fixture cleanup path'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
