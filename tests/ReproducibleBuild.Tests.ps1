param([string]$Compiler)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$dir=Join-Path $env:TEMP ('SmartDNS-reproducibility-'+[Guid]::NewGuid().ToString('N'))
$first=Join-Path $dir 'first-output';$copied=Join-Path $dir 'separate-source';$second=Join-Path $dir 'second-output'
$build=Join-Path $root 'tools/Build-VerifiedWindows.ps1'
& $build -Compiler $Compiler -OutputDirectory $first
$manifest=Get-Content -LiteralPath (Join-Path $first 'build-manifest.json') -Raw | ConvertFrom-Json
foreach($input in $manifest.Inputs){
    $destination=Join-Path $copied $input.Path
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $input.Path) -Destination $destination
}
& (Join-Path $copied 'tools/Build-VerifiedWindows.ps1') -Compiler $Compiler -OutputDirectory $second
$other=Get-Content -LiteralPath (Join-Path $second 'build-manifest.json') -Raw | ConvertFrom-Json
if($manifest.Output.SHA256 -ne $other.Output.SHA256){throw 'Same input in separate source/output directories produced different executable bytes'}
Write-Output 'PASS full EXE SHA-256 matches across separate source and output directories without masking'
if(!$manifest.Deterministic -or $manifest.DefaultResponseFile -or $manifest.CompilerSHA256 -ne $other.CompilerSHA256){throw 'Compiler metadata mismatch'}
Write-Output 'PASS same compiler and explicit references recorded; default response file disabled'
& (Join-Path $root 'tools/Test-WindowsArtifact.ps1') -File (Join-Path $first $manifest.Output.Name) -OutputPath (Join-Path $first 'artifact-policy.json')
if($LASTEXITCODE -ne 0){throw 'Verified artifact policy failed'}
$p=Join-Path $copied 'src\windows\Properties\AssemblyInfo.cs';$s=[IO.File]::ReadAllText($p).Replace('persistent per-adapter backup','audited per-adapter backup')
[IO.File]::WriteAllText($p,$s,[Text.UTF8Encoding]::new($true))
& (Join-Path $copied 'tools/Build-VerifiedWindows.ps1') -Compiler $Compiler -OutputDirectory $second
$changed=Get-Content -LiteralPath (Join-Path $second 'build-manifest.json') -Raw | ConvertFrom-Json
if($changed.Output.SHA256 -eq $manifest.Output.SHA256){throw 'Source mutation did not affect output hash'}
Write-Output 'PASS a harmless metadata change changes the exact executable hash'
Write-Output ('Reproducibility evidence: '+$dir)