param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$manifest=Join-Path $Root 'src/windows/sources.rsp'
$sources=@([IO.File]::ReadAllLines($manifest) | Where-Object {![string]::IsNullOrWhiteSpace($_)} | ForEach-Object {$_.Replace('\','/')})
$seen=@{}
foreach($source in $sources){
    if($source -cnotmatch '^src/windows/[A-Za-z]+/[A-Za-z][A-Za-z0-9]*\.cs$' -or $seen.ContainsKey($source)){
        throw ('Invalid or duplicate Windows source: '+$source)
    }
    if(!(Test-Path -LiteralPath (Join-Path $Root $source) -PathType Leaf)){throw ('Missing Windows source: '+$source)}
    $seen[$source]=$true
}
$actual=@(Get-ChildItem -LiteralPath (Join-Path $Root 'src/windows') -Filter '*.cs' -Recurse -File)
if(!$sources.Count -or $sources.Count -ne $actual.Count){throw 'Windows sources.rsp must list every application C# file exactly once.'}
$sources
