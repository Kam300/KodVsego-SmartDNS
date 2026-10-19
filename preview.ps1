# Creates the form and renders its canvas only; never invokes DNS changes.
param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot 'KodVsego_SmartDNS.2.20.0.exe'),
    [int]$Dpi = 96,
    [ValidateRange(-1,5)][int]$DnsSet = -1,
    [switch]$Active,
    [switch]$Confirmed,
    [ValidateSet('Live', 'Off', 'On', 'Unknown')][string]$VpnPreview = 'Live',
    [switch]$Inactive,
    [switch]$Busy,
    [switch]$Offline,
    [switch]$Secure,
    [switch]$Harden,
    [ValidateSet('Light', 'Dark')][string]$Theme = 'Light',
    [ValidateSet('None', 'Success', 'Warning', 'LongError', 'CancelUac', 'Force', 'DnsTest', 'DohConflict', 'Cleanup')][string]$Dialog = 'None',
    [ValidateSet('Ready', 'Working', 'Done', 'Error')][string]$BrowserState = 'Ready',
    [ValidateSet('Live', 'Off', 'On', 'Partial', 'External', 'Unknown')][string]$AgPreview = 'Live',
    [ValidateRange(0,3)][int]$AgProvider = 0,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'preview.png')
)
$ErrorActionPreference = 'Stop'
$asm = [Reflection.Assembly]::LoadFile($AssemblyPath)
if ($Harden) {
    $asm.GetType('HbAi.XboxDns.ProcessSecurity').GetMethod('Initialize', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @()) | Out-Null
}
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = [Activator]::CreateInstance($asm.GetType("HbAi.XboxDns.MainForm"), $true)
$f.GetType().GetMethod('SetTheme', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($f, @(($Theme -eq 'Dark'), $false)) | Out-Null
if ($DnsSet -ge 0) { $f.GetType().GetField('dnsSet', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $DnsSet) }
if ($AgPreview -eq 'Live') {
    $agStatus = $asm.GetType('HbAi.XboxDns.AntigravityDns').GetMethod('GetStatus', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @())
} else {
    $agStatus = [Activator]::CreateInstance($asm.GetType('HbAi.XboxDns.AgStatus'), $true)
    foreach ($name in @('Checked', 'Complete', 'External')) {
        $value = ($name -eq 'Checked' -and $AgPreview -in @('On','Partial')) -or ($name -eq 'Complete' -and $AgPreview -eq 'On') -or ($name -eq 'External' -and $AgPreview -eq 'External')
        $agStatus.GetType().GetField($name, [Reflection.BindingFlags]'Instance,NonPublic').SetValue($agStatus, $value)
    }
    if ($AgPreview -eq 'On') { $agStatus.GetType().GetField('Provider', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($agStatus, $AgProvider) }
    if ($AgPreview -eq 'Unknown') { $agStatus.GetType().GetField('Error', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($agStatus, 'Preview') }
}
$f.GetType().GetField('agStatus', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $agStatus)
$f.GetType().GetField('agLoading', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $false)
if ($Dialog -ne 'None') {
    $message = "Настройки DNS применены.`n`nПолностью закройте и заново откройте браузер или приложение, в котором используете сервис. Сам переключатель DNS перезапускать не требуется.`n`nЕсли сервис не открывается, отключите VPN и попробуйте ещё раз."
    $title = 'После смены DNS'
    $buttons = [Windows.Forms.MessageBoxButtons]::OK
    $icon = [Windows.Forms.MessageBoxIcon]::Information
    $default = [Windows.Forms.MessageBoxDefaultButton]::Button1
    if ($Dialog -eq 'Warning') {
        $message = "DNS сервиса уже задан, но копии прежних настроек нет.`n`nПерейти на автоматическое получение DNS? Изменятся только активные адаптеры, в которых все ручные DNS-адреса соответствующего семейства входят в список программы. Смешанные и сторонние адреса останутся без изменений.`n`nЭто не восстановит неизвестные прежние ручные настройки."
        $title = 'Нет резервной копии DNS'
        $buttons = [Windows.Forms.MessageBoxButtons]::YesNo
        $icon = [Windows.Forms.MessageBoxIcon]::Warning
        $default = [Windows.Forms.MessageBoxDefaultButton]::Button2
    } elseif ($Dialog -eq 'Force') {
        $providerLabel = @('Comss.one','GeoHide','GeoHide EU','Xbox DNS','Bezmezhau')[$f.GetType().GetField('dnsSet', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($f)]
        $message = "Antigravity уже настроен другой программой.`n`nЗаменить её DNS на ${providerLabel}? Прежние настройки сохранятся.`n`nЕсли доступ пропадёт, снимите галочку — вернём прежние DNS."
        $title = 'Заменить DNS Antigravity?'
        $buttons = [Windows.Forms.MessageBoxButtons]::YesNo
        $icon = [Windows.Forms.MessageBoxIcon]::Warning
        $default = [Windows.Forms.MessageBoxDefaultButton]::Button2
    } elseif ($Dialog -eq 'Cleanup') {
        $message = "Удалить старые DNS-правила Antigravity?`n`nAntigravity перейдёт на выбранный системный DNS. Полная копия правил сохранится. Старый анлокер может создать их снова."
        $title = 'Удалить старые правила?'
        $buttons = [Windows.Forms.MessageBoxButtons]::YesNo
        $icon = [Windows.Forms.MessageBoxIcon]::Warning
        $default = [Windows.Forms.MessageBoxDefaultButton]::Button2
    } elseif ($Dialog -eq 'DohConflict') {
        $message = "Подключить DoH, сохранив настройки Antigravity?`n`nДва домена Antigravity продолжат использовать прежние DNS. Остальные запросы пойдут через DoH. IPv6 временно приостановится."
        $title = 'DoH и Antigravity'
        $buttons = [Windows.Forms.MessageBoxButtons]::YesNo
        $icon = [Windows.Forms.MessageBoxIcon]::Warning
        $default = [Windows.Forms.MessageBoxDefaultButton]::Button2
    } elseif ($Dialog -eq 'CancelUac') {
        $message = 'Запрос прав администратора отменён. DNS не изменён.'
        $title = 'Ошибка DNS'
        $icon = [Windows.Forms.MessageBoxIcon]::Warning
    } elseif ($Dialog -eq 'LongError') {
        $message = ((1..25 | ForEach-Object { "Адаптер $_ недоступен. Повторите восстановление после подключения адаптера." }) -join "`n`n")
        $title = 'Ошибка DNS'
        $icon = [Windows.Forms.MessageBoxIcon]::Warning
    }
    if ($Dialog -eq 'DnsTest') { $title = 'Тест DNS'; $message = 'Результаты теста выбранного DNS' }
    $d = $f.GetType().GetMethod('CreateDialog', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($f, @($message, $title, $buttons, $icon, $default, [single]($Dpi / 96.0), ($Dialog -eq 'Success')))
    if ($Dialog -eq 'Force') { $d.GetType().GetMethod('SetPrimaryText', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($d, @('Заменить')) | Out-Null }
    if ($Dialog -eq 'DohConflict') { $d.GetType().GetMethod('SetPrimaryText', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($d, @('Подключить')) | Out-Null }
    if ($Dialog -eq 'Cleanup') { $d.GetType().GetMethod('SetPrimaryText', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($d, @('Удалить')) | Out-Null }
    if ($Dialog -eq 'Success' -and $BrowserState -ne 'Ready') {
        $d.GetType().GetMethod('SetBrowserBusy', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($d, @(($BrowserState -eq 'Working'))) | Out-Null
        $message = switch ($BrowserState) {
            'Working' { 'Определяем активные профили Google Chrome. Затем закроем его окна обычным способом и откроем снова. Ожидание завершения — до 15 секунд.' }
            'Done' { "Google Chrome запущен с сохранёнными профилями.`n`nМожно продолжать работу." }
            'Error' { "Браузер не завершился. Возможно, он ждёт сохранения данных или остался в фоне.`n`nЗакройте его вручную; принудительное завершение не выполняется." }
        }
        $d.GetType().GetMethod('ShowBrowserStatus', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($d, @($message, ($BrowserState -eq 'Error'))) | Out-Null
    }
    if ($Dialog -eq 'DnsTest') {
        $providers = [string[]]@('Comss.one','GeoHide','GeoHide EU','Xbox DNS','Bezmezhau','DNS-AI')
        $d.GetType().GetMethod('AddDnsTestControls', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($d, @($providers, 4, [Action[int]]{param($provider)})) | Out-Null
        $testMessage = "Ответ DNS не гарантирует доступность сайта.`n`n143.20.64.55`n✓ example.com — 25 мс`n✓ chatgpt.com — 31 мс`n✓ generativelanguage.googleapis.com — 28 мс`n✓ open.spotify.com — 26 мс`n✓ cloudcode-pa.googleapis.com — 29 мс`n✓ daily-cloudcode-pa.googleapis.com — 29 мс`n`n31.58.216.201`n✓ example.com — 31 мс`n✕ chatgpt.com — нет ответа (2 с)`n`nНастройки DNS не изменялись."
        $d.GetType().GetMethod('ShowDnsTestStatus', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($d, @($testMessage,'Bezmezhau · результаты', $false, $true)) | Out-Null
    }
    $bitmap = New-Object Drawing.Bitmap $d.Width, $d.Height
    try {
        $d.Show()
        [Windows.Forms.Application]::DoEvents()
        foreach ($button in $d.Controls | Where-Object { $_ -is [Windows.Forms.Button] -and $_.TabStop }) {
            if ($button.Bottom -ge $d.ClientSize.Height -or $button.Top -lt 0) { throw 'Dialog action button is clipped' }
        }
        $d.DrawToBitmap($bitmap, $d.ClientRectangle)
        $bitmap.Save($OutputPath)
        if ($Dialog -in @('Warning','Force') -and ($d.AcceptButton.DialogResult -ne [Windows.Forms.DialogResult]::No -or $d.CancelButton.DialogResult -ne [Windows.Forms.DialogResult]::No)) { throw 'Unsafe confirmation default' }
        if ($d.Height -gt [Windows.Forms.Screen]::FromControl($f).WorkingArea.Height) { throw 'Dialog exceeds screen height' }
    } finally { $bitmap.Dispose(); $d.Dispose(); $f.Dispose() }
    exit
}
if ($Active) {
    $f.GetType().GetField('active', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $true)
}
if ($Confirmed) {
    foreach ($field in @('hasBackup','dnsConfirmed')) { $f.GetType().GetField($field, [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $true) }
}
if ($VpnPreview -ne 'Live') {
    $vpn = [Activator]::CreateInstance($asm.GetType('HbAi.XboxDns.VpnStatus'), $true)
    $vpn.GetType().GetField('Failed', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($vpn, ($VpnPreview -eq 'Unknown'))
    [string[]]$connections = @()
    if ($VpnPreview -eq 'On') { $connections = @('Preview VPN') }
    $vpn.GetType().GetField('Connections', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($vpn, $connections)
    $f.GetType().GetMethod('UpdateVpnStatus', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($f, @($vpn)) | Out-Null
}
$f.GetType().GetMethod('EnsureProviderVisible', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($f, @()) | Out-Null
if ($Secure) { $f.GetType().GetField('secureDns', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $true) }
if ($Inactive) {
    foreach ($field in @('active','hasBackup','dnsConfirmed')) { $f.GetType().GetField($field, [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $false) }
}
if ($Offline) { $f.GetType().GetField('networkAvailable', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $false) }
if ($Busy) { $f.GetType().GetField('busy', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($f, $true) }
$f.GetType().GetMethod('SnapMotion', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($f, @()) | Out-Null
$logicalWidth = $f.GetType().GetField('CanvasWidth', [Reflection.BindingFlags]'Static,NonPublic').GetRawConstantValue()
$logicalHeight = $f.GetType().GetField('CanvasHeight', [Reflection.BindingFlags]'Static,NonPublic').GetRawConstantValue()
$scale = $Dpi / 96.0
$bmp = New-Object System.Drawing.Bitmap ([int]($logicalWidth * $scale)), ([int]($logicalHeight * $scale))
$bmp.SetResolution($Dpi, $Dpi)
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.Clear($f.BackColor)
    $g.ScaleTransform($scale, $scale)
    $render = $f.GetType().GetMethod('PaintCanvas', [Reflection.BindingFlags]'Instance,NonPublic')
    $render.Invoke($f, @($g)) | Out-Null
    $bmp.Save($OutputPath)
} finally {
    $g.Dispose()
    $bmp.Dispose()
    $f.Dispose()
}
