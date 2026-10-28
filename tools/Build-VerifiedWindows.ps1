param([string]$Compiler,[string]$OutputDirectory=(Join-Path $PSScriptRoot '..\dist\verified'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$Compiler){
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if(!(Test-Path -LiteralPath $vswhere)){throw 'Roslyn compiler required. Pass -Compiler or install Visual Studio Build Tools.'}
    $installation=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
    if($LASTEXITCODE -ne 0 -or !$installation){throw 'Visual Studio Build Tools not found'}
    $Compiler=Join-Path $installation 'MSBuild\Current\Bin\Roslyn\csc.exe'
}
$Compiler=(Resolve-Path -LiteralPath $Compiler).Path
$signature=Get-AuthenticodeSignature -LiteralPath $Compiler
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw 'Compiler must carry a valid Microsoft Authenticode signature.'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& (Join-Path $PSScriptRoot 'Sync-ProviderAvailability.ps1')
$build=[IO.File]::ReadAllText((Join-Path $root 'build.bat'))
$version=[regex]::Match([IO.File]::ReadAllText((Join-Path $root 'src\windows\Properties\AssemblyInfo.cs')),'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
if($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$'){throw 'Invalid version'}
$exe=Join-Path $OutputDirectory ('KodVsego_SmartDNS.'+$version+'.exe')
$sources=@(& (Join-Path $PSScriptRoot 'Read-WindowsSources.ps1') -Root $root)
if($sources.Count -lt 20 -or 'src/windows/Properties/AssemblyInfo.cs' -notin $sources -or $build -notmatch '@src\\windows\\sources\.rsp'){throw 'Could not resolve build sources'}
$resources=@([regex]::Matches($build,'/resource:([^,\s]+),([^\s\r\n]+)') | ForEach-Object {[pscustomobject]@{Path=$_.Groups[1].Value;Name=$_.Groups[2].Value}})
if($resources.Count -ne 16){throw 'Unexpected resource list; review before building'}
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references=@('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Management.dll','System.Web.Extensions.dll') | ForEach-Object {Join-Path $framework $_}
$references+=Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll'
foreach($file in $references){if(!(Test-Path -LiteralPath $file)){throw ('Missing framework reference: '+[IO.Path]::GetFileName($file))}}
$arguments=@('/noconfig','/nologo','/nostdlib+','/target:winexe','/optimize+','/deterministic+','/debug-','/langversion:5','/codepage:65001','/utf8output',('/pathmap:'+$root+'=/_/src'),('/out:'+$exe),('/win32icon:'+(Join-Path $root 'res\app.ico')),('/win32manifest:'+(Join-Path $root 'src\windows\Properties\app.manifest')))
$arguments+=@($references | ForEach-Object {'/r:'+$_})
$arguments+=@($resources | ForEach-Object {'/resource:'+(Join-Path $root $_.Path)+','+$_.Name})
$arguments+=@($sources | ForEach-Object {Join-Path $root $_})
$inputPaths=@($sources)+@($resources | ForEach-Object {$_.Path})+@('src\windows\Properties\app.manifest','src/windows/sources.rsp','config\providers.json','config/catalog.json','config/provider-checkers.json','tools/Build-ProviderCatalog.ps1','tools\Sync-ProviderAvailability.ps1','tools\Read-WindowsSources.ps1','tools\Build-VerifiedWindows.ps1','build.bat')
$inputs=@($inputPaths | Sort-Object -Unique | ForEach-Object {[pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $root $_)).Hash.ToLowerInvariant()}})
& $Compiler $arguments
if($LASTEXITCODE -ne 0){throw 'Deterministic app build failed'}
foreach($input in $inputs){if((Get-FileHash -LiteralPath (Join-Path $root $input.Path)).Hash.ToLowerInvariant() -ne $input.SHA256){throw ('Input changed during compilation: '+$input.Path)}}
$manifest=[ordered]@{
    SchemaVersion=1;Version=$version;CompilerVersion=((& $Compiler /nologo /version) -join '');CompilerSHA256=(Get-FileHash -LiteralPath $Compiler).Hash.ToLowerInvariant();CompilerSigner=$signature.SignerCertificate.Subject
    Deterministic=$true;DefaultResponseFile=$false;Inputs=$inputs
    References=@($references | ForEach-Object {[pscustomobject]@{Name=[IO.Path]::GetFileName($_);SHA256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
    Output=[ordered]@{Name=[IO.Path]::GetFileName($exe);Bytes=(Get-Item -LiteralPath $exe).Length;SHA256=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()}
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'build-manifest.json'),($manifest | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Write-Output ('Verified build: '+$exe)
Write-Output ('SHA256: '+$manifest.Output.SHA256)
