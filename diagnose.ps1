<#
  Диагностика Faceit Tunnel: проверяет по очереди, какой способ открывает Faceit
  у вас в сети — напрямую, с фрагментацией TLS (разные варианты), через VPN.
  Права администратора не нужны: проверки идут через локальный прокси sing-box, без TUN.
  Результат — diag-report.txt рядом со скриптом. Ключей и ссылок на подписку в нём нет.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root    = $PSScriptRoot
$SingBox = Join-Path $Root 'bin\sing-box.exe'
$Tunnel  = Join-Path $Root 'faceit-tunnel.ps1'
$Report  = Join-Path $Root 'diag-report.txt'
$Port    = 28081
$Hosts   = @('www.faceit.com', 'api.faceit.com', 'ac-client-ws.faceit.com')
$Domains = @('faceit.com', 'faceit-cdn.net', 'faceit.gg', 'challenges.cloudflare.com')

$lines = New-Object Collections.ArrayList
function Out-Report([string]$text = '') {
    [void]$lines.Add($text)
    Write-Host $text
}

# curl через прокси (или напрямую) — HTTP-код, время и код выхода curl.
function Test-Url([string]$url, [string]$proxy) {
    $curlArgs = @('-s', '-o', 'NUL', '-m', '12', '-w', '%{http_code} %{time_total}s')
    if ($proxy) { $curlArgs += @('-x', $proxy) }
    $out = & curl.exe @curlArgs $url
    $code = $LASTEXITCODE
    $verdict = 'OK'
    if ($code -eq 28) { $verdict = 'ЗАВИСАЕТ (блокировка)' }
    elseif ($code -ne 0) { $verdict = "ОШИБКА curl $code" }
    '{0,-28} {1,-14} {2}' -f ([Uri]$url).Host, $out, $verdict
}

function Get-TraceLoc([string]$proxy) {
    $curlArgs = @('-s', '-m', '12')
    if ($proxy) { $curlArgs += @('-x', $proxy) }
    $t = & curl.exe @curlArgs 'https://www.faceit.com/cdn-cgi/trace'
    $loc = ($t | Select-String '^loc=') -replace '^loc=', ''
    if (-not $loc) { $loc = '(нет ответа)' }
    $loc
}

# Запускает sing-box с данным конфигом как локальный прокси и проверяет все хосты.
function Test-WithConfig([string]$title, $config) {
    Out-Report "--- $title"
    $config.inbounds = @([ordered]@{ type = 'mixed'; tag = 'mixed-in'; listen = '127.0.0.1'; listen_port = $Port })
    $file = Join-Path $env:TEMP 'faceit-diag.json'
    $log = Join-Path $env:TEMP 'faceit-diag.log'
    [IO.File]::WriteAllText($file, (ConvertTo-Json -InputObject $config -Depth 20), (New-Object Text.UTF8Encoding($false)))
    $proc = Start-Process -FilePath $SingBox -ArgumentList 'run', '-c', "`"$file`"" -PassThru -WindowStyle Hidden -RedirectStandardError $log
    Start-Sleep -Milliseconds 1500
    try {
        if ($proc.HasExited) {
            Out-Report 'sing-box не запустился:'
            Get-Content $log -Tail 5 | ForEach-Object { Out-Report "  $_" }
            return
        }
        $proxy = "socks5h://127.0.0.1:$Port"
        foreach ($h in $Hosts) { Out-Report (Test-Url "https://$h/" $proxy) }
        Out-Report ("Страна, которую видит Faceit: " + (Get-TraceLoc $proxy))
    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
        Remove-Item $file -ErrorAction SilentlyContinue
    }
}

function New-DirectConfig([bool]$tcpFragment, [bool]$recordFragment) {
    $rule = [ordered]@{ domain_suffix = $Domains; action = 'route'; outbound = 'direct' }
    if ($tcpFragment) { $rule.tls_fragment = $true }
    if ($recordFragment) { $rule.tls_record_fragment = $true }
    [ordered]@{
        log       = [ordered]@{ level = 'warn' }
        dns       = [ordered]@{ servers = @([ordered]@{ type = 'udp'; tag = 'dns'; server = '1.1.1.1' }); final = 'dns' }
        inbounds  = @()
        outbounds = @([ordered]@{ type = 'direct'; tag = 'direct' })
        route     = [ordered]@{
            rules                   = @([ordered]@{ action = 'sniff' }, $rule)
            final                   = 'direct'
            default_domain_resolver = 'dns'
        }
    }
}

# ---- Подготовка ---------------------------------------------------------------

$running = Get-Process -Name 'sing-box' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $SingBox }
if ($running) {
    Write-Host 'Закройте окно Faceit Tunnel (туннель должен быть выключен) и запустите диагностику снова.' -ForegroundColor Yellow
    return
}
if (-not (Test-Path -LiteralPath $SingBox)) {
    Write-Host 'Скачиваю sing-box...' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Tunnel -CheckOnly -Mode direct | Out-Null
}

Out-Report "Faceit Tunnel — диагностика, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Out-Report ''

Out-Report '=== Сеть'
try {
    $info = Invoke-RestMethod -Uri 'https://ipinfo.io/json' -TimeoutSec 10
    Out-Report "Провайдер: $($info.org), $($info.city), $($info.country)"
} catch { Out-Report 'Провайдер: не удалось определить' }
$adapters = Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { "$($_.Name) ($($_.InterfaceDescription))" }
Out-Report ("Активные адаптеры: " + ($adapters -join '; '))
$watch = 'winws', 'Happ', 'xray', 'v2rayN', 'Hiddify', 'Proxifier', 'warp-svc', 'RvControlSvc', 'faceitservice', 'FACEIT'
$procs = Get-Process -Name $watch -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Sort-Object -Unique
Out-Report ("Запущено из важного: " + ($procs -join ', '))
Out-Report ''

Out-Report '=== 0. Контроль: обычные сайты (должны быть OK)'
foreach ($u in 'https://ya.ru/', 'https://www.cloudflare.com/') { Out-Report (Test-Url $u $null) }
Out-Report ''

Out-Report '=== 1. Напрямую, без обхода'
foreach ($h in $Hosts) { Out-Report (Test-Url "https://$h/" $null) }
Out-Report ''

# Тот же сервер Faceit, но с чужим именем сайта в TLS. Если и так зависает —
# блокируют весь IP-адрес, и фрагментация/zapret не помогут, нужен VPN.
Out-Report '=== 1b. Сервер Faceit под чужим именем (example.com)'
$faceitIp = (Resolve-DnsName 'www.faceit.com' -Type A -ErrorAction SilentlyContinue | Where-Object IPAddress | Select-Object -First 1).IPAddress
if ($faceitIp) {
    $out = & curl.exe -s -k -o NUL -m 12 -w '%{http_code} %{time_total}s' --connect-to "example.com:443:${faceitIp}:443" 'https://example.com/'
    $verdict = 'OK — блокируют по имени faceit.com'
    if ($LASTEXITCODE -eq 28) { $verdict = 'ЗАВИСАЕТ — блокируют весь IP Faceit' }
    elseif ($LASTEXITCODE -ne 0) { $verdict = "ОШИБКА curl $LASTEXITCODE" }
    Out-Report ('{0,-28} {1,-14} {2}' -f $faceitIp, $out, $verdict)
} else {
    Out-Report 'Не удалось узнать IP www.faceit.com'
}
Out-Report ''

Out-Report '=== 2. Без VPN, с фрагментацией TLS (режим start-direct.bat)'
Test-WithConfig 'фрагментация TCP' (New-DirectConfig $true $false)
Test-WithConfig 'фрагментация TLS-записей' (New-DirectConfig $false $true)
Test-WithConfig 'обе (так работает start-direct.bat)' (New-DirectConfig $true $true)
Out-Report ''

Out-Report '=== 3. Через VPN (режим start.bat)'
if (Test-Path -LiteralPath (Join-Path $Root 'subscription.txt')) {
    $cfgFile = Join-Path $Root 'config.json'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Tunnel -CheckOnly | Out-Null
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $cfgFile)) {
        $vpn = Get-Content -LiteralPath $cfgFile -Raw | ConvertFrom-Json
        $vpn.log = [pscustomobject]@{ level = 'warn' }
        Test-WithConfig 'VPN-сервер из подписки' $vpn
    } else {
        Out-Report 'Не удалось собрать конфиг из подписки (запустите start.bat и посмотрите ошибку).'
    }
} else {
    Out-Report 'Подписка не сохранена (start.bat ещё не запускали) — пропускаю.'
}
Out-Report ''
Out-Report 'Как читать: OK с кодом 200/403/404 — Faceit открывается; ЗАВИСАЕТ — блокировка не обойдена.'

[IO.File]::WriteAllText($Report, ($lines -join "`r`n"), (New-Object Text.UTF8Encoding($true)))
Write-Host ''
Write-Host "Готово. Отчёт сохранён в $Report — пришлите его." -ForegroundColor Green
