# Exercises the real WinForms timer. No DNS-changing controls are invoked.
# Showing the form may run its read-only startup DNS/VPN checks.
param([string]$AssemblyPath)
if(!$AssemblyPath){
    $root=Split-Path $PSScriptRoot -Parent
    $version=[regex]::Match([IO.File]::ReadAllText((Join-Path $root 'src\windows\Properties\AssemblyInfo.cs')),'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
    $AssemblyPath=Join-Path $root ('dist\dev\KodVsego_SmartDNS.'+$version+'.exe')
}
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$assembly = [Reflection.Assembly]::LoadFile($AssemblyPath)
$form = [Activator]::CreateInstance($assembly.GetType('HbAi.XboxDns.MainForm'), $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
function Set-Field([string]$name, $value) { $form.GetType().GetField($name, $flags).SetValue($form, $value) }
function Get-Field([string]$name) { $form.GetType().GetField($name, $flags).GetValue($form) }
function Pump([int]$milliseconds) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $milliseconds) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 10 }
}
function Assert([bool]$condition, [string]$message) { if (!$condition) { throw $message }; Write-Output ('PASS ' + $message) }
try {
    $form.StartPosition = 'Manual'
    $form.Location = New-Object Drawing.Point -ArgumentList -10000,-10000
    $form.ShowInTaskbar = $false
    # Isolate animation from real network status while pumping the message loop.
    Set-Field 'checkingNetwork' $true
    Set-Field 'checkUpdateAtStartup' $false
    Set-Field 'initializing' $false
    $form.Show()
    (Get-Field 'networkMonitor').Stop()
    Set-Field 'active' $true
    Set-Field 'dnsSet' 4
    $form.GetType().GetMethod('SetTheme', $flags).Invoke($form, @($true, $false)) | Out-Null
    $form.Invalidate()
    Pump 750
    Assert ((Get-Field 'powerMix') -gt 0.998) 'Connected-state transition settles'
    Assert ((Get-Field 'themeMix') -gt 0.998) 'Dark-theme transition settles'
    $segments = Get-Field 'rSeg'
    Assert ([Math]::Abs((Get-Field 'selectionLeft') - $segments[4].X) -lt 0.2) 'Provider highlight reaches the selected provider'
    Assert (!(Get-Field 'anim').Enabled) 'Connected idle state does not run an animation timer'
    Set-Field 'busy' $true
    $form.Invalidate()
    Pump 80
    $allowed = $form.GetType().GetProperty('MotionAllowed', $flags).GetValue($form, $null)
    Assert (((Get-Field 'anim').Enabled) -eq $allowed) 'Busy animation respects the system motion preference'
    Set-Field 'busy' $false
    Set-Field 'active' $false
    $form.Invalidate()
    Pump 750
    Assert ((Get-Field 'powerMix') -lt 0.002) 'Disconnected-state transition settles'
    Assert (!(Get-Field 'anim').Enabled) 'Disconnected idle state does not run an animation timer'
    Write-Output 'All 7 UI motion checks passed. No DNS settings changed.'
} finally { $form.Close(); $form.Dispose() }
