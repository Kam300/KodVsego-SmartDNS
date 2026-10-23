$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$dir=Join-Path $env:TEMP ('SmartDNS-VT-report-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$file=Join-Path $dir 'harmless.txt';[IO.File]::WriteAllText($file,'Harmless VirusTotal client test. No network requests are made.')
$hash=(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
$oldKey=[Environment]::GetEnvironmentVariable('VIRUSTOTAL_API_KEY')
$fixtureState=@{calls=0;fail=$false}
function Invoke-RestMethod {
    param($Uri,$Headers,$TimeoutSec)
    $fixtureState.calls++
    if($Uri -cne ('https://www.virustotal.com/api/v3/files/'+$hash)){throw 'Unexpected endpoint or upload attempt'}
    if($Headers['x-apikey'] -ne 'synthetic-fixture-key'){throw 'Wrong test credential'}
    if($fixtureState.fail){throw 'synthetic-fixture-key must never appear in the public error'}
    return [pscustomobject]@{data=[pscustomobject]@{attributes=[pscustomobject]@{
        sha256=$hash;last_analysis_date=1792683639
        last_analysis_stats=[pscustomobject]@{malicious=1;suspicious=0;undetected=70;failure=0;'type-unsupported'=4}
        last_analysis_results=[pscustomobject]@{Microsoft=[pscustomobject]@{engine_name='Microsoft';category='undetected';result=$null};TestEngine=[pscustomobject]@{engine_name='TestEngine';category='malicious';result='Test only'}}
    }}}
}
try {
    [Environment]::SetEnvironmentVariable('VIRUSTOTAL_API_KEY','synthetic-fixture-key')
    $result=& (Join-Path $root 'tools/Get-VirusTotalReport.ps1') -File $file -OutputDirectory $dir
    if($fixtureState.calls -ne 1 -or $result.SHA256 -ne $hash -or $result.Uploaded -or $result.RescanRequested -or $result.Malicious -ne 1 -or $result.Microsoft.category -ne 'undetected'){throw 'Unexpected report or mutation'}
    Write-Output 'PASS existing hash report fetched without upload or reanalysis'
    if($result.Detections.Count -ne 1 -or $result.Detections[0].engine_name -ne 'TestEngine'){throw 'Wrong detection summary'}
    Write-Output 'PASS detection summary preserves engine categories'
    $saved=[IO.File]::ReadAllText((Join-Path $dir ($hash+'.json')))
    if($saved.Contains('synthetic-fixture-key')){throw 'Credential leaked into the saved report'}
    Write-Output 'PASS saved report contains no API credential'
    $fixtureState.fail=$true;$message=$null
    try{& (Join-Path $root 'tools/Get-VirusTotalReport.ps1') -File $file}catch{$message=$_.Exception.Message}
    if(!$message -or $message.Contains('synthetic-fixture-key')){throw 'Request error exposed credentials or was ignored'}
    Write-Output 'PASS network errors omit credentials'
    [Environment]::SetEnvironmentVariable('VIRUSTOTAL_API_KEY',$null)
    $before=$fixtureState.calls;$rejected=$false
    try{& (Join-Path $root 'tools/Get-VirusTotalReport.ps1') -File $file}catch{$rejected=$true}
    if(!$rejected -or $fixtureState.calls -ne $before){throw 'Missing credential accepted'}
    Write-Output 'PASS missing credential rejected before any request'
}
finally{[Environment]::SetEnvironmentVariable('VIRUSTOTAL_API_KEY',$oldKey)}