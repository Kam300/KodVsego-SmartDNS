param([Parameter(Mandatory=$true)][string]$File,[string]$OutputPath)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$filePath=(Resolve-Path -LiteralPath $File).Path
$directory=Join-Path $env:TEMP ('SmartDNS-artifact-policy-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
if(!$OutputPath){$OutputPath=Join-Path $directory 'artifact-policy.json'}
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
$inspector=Join-Path $directory 'ArtifactInspection.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /noconfig /nologo /target:exe /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Xml.dll ("/out:"+$inspector) (Join-Path $PSScriptRoot 'ArtifactInspection.cs')
if($LASTEXITCODE -ne 0){throw 'Artifact inspector compilation failed'}
& $inspector $filePath $OutputPath
if($LASTEXITCODE -ne 0){throw 'Artifact composition policy rejected the executable'}