param([string]$ConfigurationPath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'config\providers.json'),[string]$OutputRoot=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$config=[IO.File]::ReadAllText($ConfigurationPath) | ConvertFrom-Json
$keys=@('comss','geohide','geohide-eu','xbox','bezmezhau','dns-ai','magicdns')
if($config.Count -lt 7){throw 'Keep all historical provider IDs; disable instead of deleting.'}
$seen=@{}
for($i=0;$i -lt $config.Count;$i++){
    $p=$config[$i]
    $defaults=@{enabled=$true;encryptedOnly=$false;ipv6=@();legacyIpv4=@();windowsDohServers=@($p.ipv4);services=@();autoGoals=@();testHosts=@('example.com');description='';site='';url='';doh=$null;dot=$null;windowsDotServers=@()}
    foreach($field in $defaults.Keys){if($field -notin $p.PSObject.Properties.Name){$p | Add-Member -NotePropertyName $field -NotePropertyValue $defaults[$field]}}
    if(!$p.description){$p.description=@($p.services) -join ', '}
    if(($p.id -isnot [int] -and $p.id -isnot [long]) -or $p.id -ne $i -or ($i -lt 7 -and $p.key -cne $keys[$i]) -or $seen.ContainsKey([string]$p.key) -or $p.key -cnotmatch '^[a-z0-9-]+$'){throw 'IDs must be sequential, stable and keys unique. Append new providers.'}
    $seen[[string]$p.key]=$true
    if($p.enabled -isnot [bool] -or $p.encryptedOnly -isnot [bool] -or [string]::IsNullOrWhiteSpace($p.name)){throw 'Name and boolean flags are required.'}
    foreach($field in @('ipv4','ipv6','legacyIpv4','windowsDohServers','windowsDotServers','services','autoGoals','testHosts')){if($p.$field -isnot [array]){throw "$field must be an array"}}
    foreach($old in $p.legacyIpv4){
        if($old -isnot [array] -or !$old.Count){throw 'legacyIpv4 must contain non-empty address arrays.'}
        foreach($value in $old){$address=$null;if(![Net.IPAddress]::TryParse($value,[ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw "Invalid legacy IPv4 address: $value"}}
    }
    if(@($p.ipv4).Count -lt 1 -or @($p.windowsDohServers).Count -lt 1){throw 'IPv4 and Windows DoH bootstrap servers are required.'}
    foreach($field in @('ipv4','ipv6','windowsDohServers','windowsDotServers')){foreach($value in $p.$field){$address=$null;if(![Net.IPAddress]::TryParse($value,[ref]$address) -or $address.AddressFamily -ne $(if($field -eq 'ipv6'){[Net.Sockets.AddressFamily]::InterNetworkV6}else{[Net.Sockets.AddressFamily]::InterNetwork})){throw "Invalid $field address: $value"}}}
    if($null -ne $p.doh -and [string]::IsNullOrWhiteSpace($p.doh)){throw 'Use null for absent DoH, not an empty string.'}
    foreach($field in @('doh','url')){if($p.$field){$uri=$null;if(![Uri]::TryCreate($p.$field,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Fragment -and $field -eq 'doh'){throw "Invalid HTTPS $field URL"}}}
    if($null -ne $p.dot -and [string]::IsNullOrWhiteSpace($p.dot)){throw 'Use null for absent DoT, not an empty string.'}
    if($p.dot -and ([Uri]::CheckHostName($p.dot) -ne [UriHostNameType]::Dns -or !@($p.windowsDotServers).Count)){throw 'DoT needs a hostname and bootstrap IPs.'}
    if(!$p.dot -and @($p.windowsDotServers).Count){throw 'DoT bootstrap addresses need a hostname.'}
    if($p.encryptedOnly -and !$p.doh){throw 'encryptedOnly requires DoH.'}
    if(!@($p.testHosts).Count){throw 'testHosts must not be empty.'}
    foreach($hostName in $p.testHosts){if([Uri]::CheckHostName($hostName) -ne [UriHostNameType]::Dns){throw "Invalid test hostname: $hostName"}}
    foreach($goal in $p.autoGoals){if($goal -notin @('ChatGPT','Claude','Gemini','ElevenLabs','Spotify','Xbox','Antigravity')){throw "Unknown auto goal: $goal"}}
}
if(!@($config | Where-Object enabled).Count){throw 'At least one provider must remain enabled.'}
function Quote($value){if($null -eq $value){return 'null'};return (ConvertTo-Json -InputObject ([string]$value) -Compress)}
function Strings($values){return 'new String[]{'+(($values | ForEach-Object {Quote $_}) -join ',')+'}'}
$flags=($config | ForEach-Object {$_.enabled.ToString().ToLowerInvariant()}) -join ','
$plain=@($config | ForEach-Object {$_}); $json=Quote (ConvertTo-Json -InputObject $plain -Depth 12 -Compress)
$cs=@"
// Generated from config/providers.json. Do not edit directly.
namespace HbAi.XboxDns {
    sealed class ProviderEntry {
        public string key {get;set;} public string name {get;set;} public string doh {get;set;} public string dot {get;set;} public string description {get;set;} public string site {get;set;} public string url {get;set;}
        public string[] ipv4 {get;set;} public string[] ipv6 {get;set;} public string[] windowsDohServers {get;set;} public string[] windowsDotServers {get;set;} public string[] services {get;set;} public string[] autoGoals {get;set;} public string[] testHosts {get;set;}
        public string[][] legacyIpv4 {get;set;}
        public bool enabled {get;set;} public bool encryptedOnly {get;set;}
        public int id {get;set;}
    }
    static class ProviderCatalog {
        internal static readonly ProviderEntry[] All = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ProviderEntry[]>($json);
    }
    static class ProviderAvailability {
        static readonly bool[] Enabled = { $flags };
        internal static bool IsEnabled(int id) { return id>=0 && id<Enabled.Length && Enabled[id]; }
    }
}
"@
$rows=@($config | ForEach-Object {'new Provider('+(Quote $_.name)+','+(Strings $_.ipv4)+','+(Quote $_.doh)+','+$_.encryptedOnly.ToString().ToLowerInvariant()+','+(Strings $_.services)+','+(Strings $_.testHosts)+')'}) -join ",`n"
$java=@"
// Generated from config/providers.json. Do not edit directly.
package ru.kodvsego.smartdns;
final class ProviderAvailability {
    private static final boolean[] ENABLED = { $flags };
    static boolean isEnabled(int id) { return id>=0 && id<ENABLED.length && ENABLED[id]; }
    static Provider[] create() {return new Provider[]{ $rows };}
}
"@
foreach($entry in @(@('ProviderAvailability.cs',$cs),@('android\app\src\main\java\ru\kodvsego\smartdns\ProviderAvailability.java',$java))){
    $path=Join-Path $OutputRoot $entry[0]
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    if(!(Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -ne $entry[1]){[IO.File]::WriteAllText($path,$entry[1],[Text.UTF8Encoding]::new($false))}
}
