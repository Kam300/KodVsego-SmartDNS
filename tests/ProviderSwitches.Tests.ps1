param([string]$JavaBin)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $env:TEMP ('SmartDNS-provider-policy-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$config=Get-Content (Join-Path $root 'config\providers.json') -Raw | ConvertFrom-Json
$config[1].enabled=$false;$config[2].enabled=$false
$path=Join-Path $fixture 'providers.json'
[IO.File]::WriteAllText($path,($config | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
& (Join-Path $root 'tools\Sync-ProviderAvailability.ps1') -ConfigurationPath $path -OutputRoot $fixture
$cs='using HbAi.XboxDns; class PolicyTest {static int Main(){return ProviderAvailability.IsEnabled(0)&&!ProviderAvailability.IsEnabled(1)&&!ProviderAvailability.IsEnabled(2)&&ProviderAvailability.IsEnabled(6)&&!ProviderAvailability.IsEnabled(7)&&!ProviderAvailability.IsEnabled(-1)?0:1;}}'
[IO.File]::WriteAllText((Join-Path $fixture 'PolicyTest.cs'),$cs,[Text.UTF8Encoding]::new($false))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /r:System.Web.Extensions.dll /target:exe ("/out:"+(Join-Path $fixture 'PolicyTest.exe')) (Join-Path $fixture 'ProviderAvailability.cs') (Join-Path $fixture 'PolicyTest.cs')
if($LASTEXITCODE -ne 0){throw 'Generated Windows policy did not compile'}
& (Join-Path $fixture 'PolicyTest.exe');if($LASTEXITCODE -ne 0){throw 'Windows policy differs from JSON'}
Write-Output 'PASS shared JSON disables GeoHide on Windows without changing IDs'
if($JavaBin){
    $java='package ru.kodvsego.smartdns; class PolicyTest {public static void main(String[] args){if(!ProviderAvailability.isEnabled(0)||ProviderAvailability.isEnabled(1)||ProviderAvailability.isEnabled(2)||!ProviderAvailability.isEnabled(6)||ProviderAvailability.isEnabled(7)||ProviderAvailability.isEnabled(-1))throw new AssertionError();}}'
    [IO.File]::WriteAllText((Join-Path $fixture 'PolicyTest.java'),$java,[Text.UTF8Encoding]::new($false))
    & (Join-Path $JavaBin 'javac.exe') -encoding UTF-8 --release 8 -Xlint:-options -d $fixture (Join-Path $fixture 'android\app\src\main\java\ru\kodvsego\smartdns\ProviderAvailability.java') (Join-Path $root 'android\app\src\main\java\ru\kodvsego\smartdns\Provider.java') (Join-Path $fixture 'PolicyTest.java')
    if($LASTEXITCODE -ne 0){throw 'Generated Android policy did not compile'}
    & (Join-Path $JavaBin 'java.exe') -cp $fixture ru.kodvsego.smartdns.PolicyTest
    if($LASTEXITCODE -ne 0){throw 'Android policy differs from JSON'}
    Write-Output 'PASS same shared JSON disables GeoHide on Android'
}
$before=(Get-FileHash (Join-Path $fixture 'ProviderAvailability.cs')).Hash
foreach($provider in $config){$provider.enabled=$false}
[IO.File]::WriteAllText($path,($config | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
$rejected=$false
try{& (Join-Path $root 'tools\Sync-ProviderAvailability.ps1') -ConfigurationPath $path -OutputRoot $fixture}catch{$rejected=$true}
if(!$rejected -or (Get-FileHash (Join-Path $fixture 'ProviderAvailability.cs')).Hash -ne $before){throw 'Invalid configuration was accepted or changed output'}
Write-Output 'PASS all-disabled configuration rejected before writing output'
# A minimal eighth provider must work without editing either platform's source tables.
$config=Get-Content (Join-Path $root 'config\providers.json') -Raw | ConvertFrom-Json
$config+= [pscustomobject]@{id=7;key='test-provider';name='Test "DNS"';ipv4=@('203.0.113.53');legacyIpv4=@();doh='https://resolver.example/dns-query';autoGoals=@('Gemini')}
[IO.File]::WriteAllText($path,($config | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
& (Join-Path $root 'tools\Sync-ProviderAvailability.ps1') -ConfigurationPath $path -OutputRoot $fixture
$cs='using HbAi.XboxDns; class PolicyTest {static int Main(){var p=ProviderCatalog.All[7];return ProviderCatalog.All.Length==8&&p.id==7&&p.name=="Test \"DNS\""&&p.ipv4.Length==1&&p.windowsDohServers[0]=="203.0.113.53"&&p.doh=="https://resolver.example/dns-query"&&p.autoGoals[0]=="Gemini"&&p.testHosts[0]=="example.com"&&p.ipv6.Length==0&&ProviderAvailability.IsEnabled(7)?0:1;}}'
[IO.File]::WriteAllText((Join-Path $fixture 'PolicyTest.cs'),$cs,[Text.UTF8Encoding]::new($false))
& $compiler /nologo /r:System.Web.Extensions.dll /target:exe ("/out:"+(Join-Path $fixture 'PolicyTest.exe')) (Join-Path $fixture 'ProviderAvailability.cs') (Join-Path $fixture 'PolicyTest.cs')
if($LASTEXITCODE -ne 0){throw 'Extended Windows catalog did not compile'}
& (Join-Path $fixture 'PolicyTest.exe');if($LASTEXITCODE -ne 0){throw 'Extended Windows data differs from JSON'}
Write-Output 'PASS minimal eighth provider with one IP and DoH is generated for Windows'
if($JavaBin){
    $java='package ru.kodvsego.smartdns; class PolicyTest {public static void main(String[] args){Provider p=Provider.at(7);if(Provider.ALL.length!=8||!p.name.equals("Test \"DNS\"")||p.servers.length!=1||!p.servers[0].equals("203.0.113.53")||!p.doh.equals("https://resolver.example/dns-query")||!p.testHosts()[0].equals("example.com")||!Provider.isAvailable(7))throw new AssertionError();}}'
    [IO.File]::WriteAllText((Join-Path $fixture 'PolicyTest.java'),$java,[Text.UTF8Encoding]::new($false))
    & (Join-Path $JavaBin 'javac.exe') -encoding UTF-8 --release 8 -Xlint:-options -d $fixture (Join-Path $fixture 'android\app\src\main\java\ru\kodvsego\smartdns\ProviderAvailability.java') (Join-Path $root 'android\app\src\main\java\ru\kodvsego\smartdns\Provider.java') (Join-Path $fixture 'PolicyTest.java')
    if($LASTEXITCODE -ne 0){throw 'Extended Android catalog did not compile'}
    & (Join-Path $JavaBin 'java.exe') -cp $fixture ru.kodvsego.smartdns.PolicyTest
    if($LASTEXITCODE -ne 0){throw 'Extended Android data differs from JSON'}
    Write-Output 'PASS same eighth provider is usable through the Android Provider API'
}
$before=(Get-FileHash (Join-Path $fixture 'ProviderAvailability.cs')).Hash
foreach($bad in @(@{field='ipv4';value=@('invalid')},@{field='doh';value='http://resolver.example/query'},@{field='id';value=2},@{field='autoGoals';value=@('Unknown')},@{field='legacyIpv4';value=@('203.0.113.1')},@{field='legacyIpv4';value=@(@('invalid','203.0.113.2'))},@{field='legacyIpv4';value=@(@('::1','203.0.113.2'))})){
    $last=$config[7].($bad.field);$config[7].($bad.field)=$bad.value
    [IO.File]::WriteAllText($path,($config | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    $rejected=$false;try{& (Join-Path $root 'tools\Sync-ProviderAvailability.ps1') -ConfigurationPath $path -OutputRoot $fixture}catch{$rejected=$true}
    if(!$rejected -or (Get-FileHash (Join-Path $fixture 'ProviderAvailability.cs')).Hash -ne $before){throw 'Invalid catalog changed generated output'}
    $config[7].($bad.field)=$last
}
Write-Output 'PASS invalid IP, HTTP DoH, duplicate ID, unknown service and invalid legacy sets rejected without output changes'
