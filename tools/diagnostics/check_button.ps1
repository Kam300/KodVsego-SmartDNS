# Проверка кнопки HB AI · Xbox DNS: какой DNS прописан и открываются ли сервисы.
# Запускать при ВЫКЛЮЧЕННОМ VPN, после нажатия кнопки в программе.
$ErrorActionPreference = 'SilentlyContinue'
"=== DNS на активных адаптерах"
Get-NetAdapter | ? Status -eq 'Up' | % {
  $d = (Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4).ServerAddresses -join ', '
  "{0,-28} {1}" -f $_.Name, $d
}
Clear-DnsClientCache
"`n=== Что отвечает системный DNS"
foreach ($h in 'gemini.google.com','elevenlabs.io','chatgpt.com','claude.ai') {
  $ip = (Resolve-DnsName $h -Type A -QuickTimeout | ? IPAddress | select -First 1 -Expand IPAddress)
  "{0,-20} {1}" -f $h, $(if ($ip) { $ip } else { 'НЕТ ОТВЕТА' })
}
"`n=== Открываются ли сайты (код ответа, куда перенаправляет)"
$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128 Safari/537.36'
foreach ($u in 'https://gemini.google.com/app','https://elevenlabs.io/','https://chatgpt.com/','https://claude.ai/') {
  $o = & curl.exe -s -o NUL --noproxy '*' -m 20 -A $ua -w '%{http_code} -> %{redirect_url} (%{remote_ip}, %{time_total} c)' $u 2>&1
  $verdict = if ($o -match '^000') { 'НЕ ОТКРЫВАЕТСЯ' }
             elseif ($o -match 'restrict|unsupported|not-available|unavailable') { 'БЛОК ПО СТРАНЕ' }
             else { 'ок' }
  "{0,-32} {1,-16} {2}" -f $u, $verdict, $o
}
