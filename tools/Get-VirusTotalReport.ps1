param(
    [Parameter(Mandatory=$true)][string[]]$File,
    [string]$OutputDirectory,
    [Security.SecureString]$ApiKey
)
# Reads existing reports by SHA-256. Never uploads files or requests a rescan.
$ErrorActionPreference='Stop'
$plainKey=$null;$keyPointer=[IntPtr]::Zero
try {
    if($ApiKey){$keyPointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($ApiKey);$plainKey=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)}
    else{$plainKey=[Environment]::GetEnvironmentVariable('VIRUSTOTAL_API_KEY')}
    if([string]::IsNullOrWhiteSpace($plainKey)){throw 'Provide -ApiKey as SecureString or set VIRUSTOTAL_API_KEY in the current process. Never commit the key.'}
    if($OutputDirectory){New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null}
    foreach($path in $File){
        $item=Get-Item -LiteralPath $path
        if($item.PSIsContainer){throw 'File must point to a regular file'}
        $hash=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $headers=@{'x-apikey'=$plainKey}
        try {$response=Invoke-RestMethod -Uri ('https://www.virustotal.com/api/v3/files/'+$hash) -Headers $headers -TimeoutSec 40}
        catch {
            $httpStatus=0;if($_.Exception.Response){$httpStatus=[int]$_.Exception.Response.StatusCode}
            if($httpStatus -eq 404){Write-Warning ('No existing VirusTotal report for '+$item.Name+'; file was not uploaded.');continue}
            if($httpStatus -eq 429){throw 'VirusTotal request limit reached. Try later; no file was uploaded.'}
            throw ('VirusTotal request failed, HTTP '+$httpStatus+'. The API key has been omitted from this error.')
        }
        finally {$headers.Clear()}
        $attributes=$response.data.attributes
        if(!$attributes -or $attributes.sha256 -ne $hash){throw 'VirusTotal returned a missing or mismatched SHA-256'}
        if($OutputDirectory){[IO.File]::WriteAllText((Join-Path $OutputDirectory ($hash+'.json')),($response | ConvertTo-Json -Depth 45),[Text.UTF8Encoding]::new($false))}
        $stats=$attributes.last_analysis_stats
        [pscustomobject]@{
            File=$item.Name;SHA256=$hash
            AnalysisUtc=([DateTimeOffset]::FromUnixTimeSeconds($attributes.last_analysis_date).ToString('u'))
            Malicious=$stats.malicious;Suspicious=$stats.suspicious;Undetected=$stats.undetected
            Failed=$stats.failure;Unsupported=$stats.'type-unsupported'
            Microsoft=$attributes.last_analysis_results.Microsoft
            Detections=@($attributes.last_analysis_results.PSObject.Properties.Value | Where-Object {$_.category -in @('malicious','suspicious')} | Select-Object engine_name,category,result)
            ReportUrl=('https://www.virustotal.com/gui/file/'+$hash+'/detection')
            Uploaded=$false;RescanRequested=$false
        }
    }
}
finally {
    if($keyPointer -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)}
    $plainKey=$null
}