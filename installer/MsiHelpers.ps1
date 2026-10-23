function ConvertTo-MsiVersion([string]$Version) {
    # MSI compares three numeric fields; reserve the last slot for the stable release.
    if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-test\.(\d+))?$') { throw 'MSI supports stable versions and -test.N only.' }
    $major=[int]$Matches[1]; $minor=[int]$Matches[2]; $patch=[int]$Matches[3]
    $testNumber=$Matches[4]
    if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 64) { throw 'Version exceeds the MSI mapping range.' }
    $revision=999
    if ($testNumber) {
        $revision=[int]$testNumber
        if ($revision -lt 1 -or $revision -gt 998) { throw 'Test number must be 1..998.' }
    }
    return '{0}.{1}.{2}' -f $major,$minor,($patch*1000+$revision)
}

function Get-MsiIdentity([string]$Value) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $bytes=$sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('KodVsego.SmartDNS.MSI/v1/'+$Value)) } finally { $sha.Dispose() }
    $guidBytes=New-Object byte[] 16
    [Array]::Copy($bytes,$guidBytes,16)
    # Deterministic name-derived identity, with the RFC variant and version bits set.
    $guidBytes[7]=($guidBytes[7] -band 15) -bor 80
    $guidBytes[8]=($guidBytes[8] -band 63) -bor 128
    return '{'+([Guid]::new($guidBytes)).ToString().ToUpperInvariant()+'}'
}

function ConvertTo-LicenseRtf([string]$Content) {
    $rtf=[Text.StringBuilder]::new()
    [void]$rtf.Append('{\rtf1\ansi\ansicpg1251\uc1\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ')
    foreach($character in $Content.ToCharArray()) {
        $code=[int]$character
        if ($code -eq 13) { continue }
        if ($code -eq 10) { [void]$rtf.Append('\par '); continue }
        if ($code -eq 92 -or $code -eq 123 -or $code -eq 125) { [void]$rtf.Append('\'); [void]$rtf.Append($character); continue }
        if ($code -gt 127) {
            if ($code -gt 32767) { $code-=65536 }
            [void]$rtf.Append('\u'+$code+'?')
        } else { [void]$rtf.Append($character) }
    }
    [void]$rtf.Append('}')
    return $rtf.ToString()
}
