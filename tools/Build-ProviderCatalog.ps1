param([object[]]$Providers,[string]$OutputRoot=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$sourceRoot=Split-Path $PSScriptRoot -Parent
if(!$Providers){$Providers=Get-Content (Join-Path $sourceRoot 'config/providers.json') -Raw | ConvertFrom-Json}
$checkers=Get-Content (Join-Path $sourceRoot 'config/provider-checkers.json') -Raw | ConvertFrom-Json
$path=Join-Path $OutputRoot 'config/catalog.json'
$existing=$null
if(Test-Path -LiteralPath $path){try{$existing=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json}catch{}}
$content=@{providers=$Providers;checkers=$checkers} | ConvertTo-Json -Depth 30 -Compress
$same=$existing -and ((@{providers=$existing.providers;checkers=$existing.checkers}|ConvertTo-Json -Depth 30 -Compress) -ceq $content)
if($same){$revision=[long]$existing.revision;$date=$existing.updatedUtc}else{
    $revision=[long][DateTime]::UtcNow.ToString('yyyyMMddHHmmss',[Globalization.CultureInfo]::InvariantCulture)
    if($existing -and $revision -le [long]$existing.revision){$revision=[long]$existing.revision+1}
    $date=[DateTime]::UtcNow.ToString('O')
}
$payload=([ordered]@{schema=1;revision=$revision;updatedUtc=$date;providers=$Providers;checkers=$checkers}|ConvertTo-Json -Depth 30 -Compress)
$quoted=ConvertTo-Json -InputObject $payload -Compress
$url='https://raw.githubusercontent.com/Kam300/KodVsego-SmartDNS/main/config/catalog.json'
$cs="// Generated catalog snapshot. Run tools/Sync-ProviderAvailability.ps1.`nnamespace HbAi.XboxDns { static class CatalogDefaults { internal const long Revision=$($revision)L; internal const string Url=`"$url`"; internal const string Json=$quoted; } }`n"
$java="// Generated catalog snapshot. Run tools/Sync-ProviderAvailability.ps1.`npackage ru.kodvsego.smartdns;`nfinal class CatalogDefaults {static final long REVISION=$($revision)L;static final String URL=`"$url`";static final String JSON=$quoted;}`n"
foreach($item in @(@('config/catalog.json',($payload+"`n")),@('src/windows/Providers/CatalogDefaults.cs',$cs),@('android/app/src/main/java/ru/kodvsego/smartdns/CatalogDefaults.java',$java))){
    $target=Join-Path $OutputRoot $item[0];New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    if(!(Test-Path -LiteralPath $target) -or [IO.File]::ReadAllText($target) -cne $item[1]){[IO.File]::WriteAllText($target,$item[1],[Text.UTF8Encoding]::new($false))}
}
