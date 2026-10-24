<#
  Faceit Tunnel — через VPN идёт только трафик Faceit (приложение, античит, сайт),
  всё остальное (CS2, Steam, игровые серверы, обычные сайты) идёт напрямую.
  Ядро: sing-box (https://github.com/SagerNet/sing-box), режим TUN.

  Запуск: start.bat (сам попросит права администратора).
  Без VPN, с вашим IP (обход блокировки фрагментацией TLS): start-direct.bat.
  Сменить подписку или сервер: change-subscription.bat.
#>
param(
    # vpn    — трафик Faceit идёт через VPN-сервер из подписки;
    # direct — без VPN: трафик Faceit идёт напрямую с вашего IP, а блокировку
    #          обходит фрагментация TLS (как zapret). Faceit видит вашу страну.
    [ValidateSet('vpn', 'direct')]
    [string]$Mode = 'vpn',
    [switch]$Reset,          # заново спросить подписку и сервер
    [switch]$CheckOnly,      # только собрать и проверить config.json, не подключаться
    [string]$Subscription    # подписка без вопроса: ссылка https://..., vless://... или путь к файлу
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root       = $PSScriptRoot
$Bin        = Join-Path $Root 'bin'
$SingBox    = Join-Path $Bin 'sing-box.exe'
$SubFile    = Join-Path $Root 'subscription.txt'
$ServerFile = Join-Path $Root 'server.txt'
$ConfigFile = Join-Path $Root 'config.json'
$LogFile    = Join-Path $Root 'sing-box.log'
$SbVersion  = '1.14.4'

# ---- Что пускать через VPN ---------------------------------------------------
# Домены (с поддоменами): сайт, API, CDN, античит (ac-client-ws.faceit.com),
# проверка Cloudflare, которую Faceit показывает перед сайтом.
$ProxyDomains   = @('faceit.com', 'faceit-cdn.net', 'faceit.gg', 'challenges.cloudflare.com')
# Адреса Faceit в Cloudflare — на случай соединений без имени домена.
$ProxyCidrs     = @('193.41.200.0/24')
# Процессы: приложение и античит. Весь их трафик идёт через VPN.
$ProxyProcesses = @('FACEIT.exe', 'faceitclient.exe', 'faceitservice.exe')
# Адреса для TUN-адаптера: берётся первый, который не занят другим VPN
# (Happ и многие клиенты на sing-box по умолчанию используют 172.19.0.1).
$TunAddresses   = @('172.23.233.1/30', '10.211.233.1/30', '192.168.233.1/30')
# -----------------------------------------------------------------------------

function Write-Info($msg) { Write-Host $msg -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host $msg -ForegroundColor Yellow }

function Install-SingBox {
    if (Test-Path -LiteralPath $SingBox) { return }
    Write-Info "Скачиваю sing-box $SbVersion с GitHub..."
    $url = "https://github.com/SagerNet/sing-box/releases/download/v$SbVersion/sing-box-$SbVersion-windows-amd64.zip"
    $zip = Join-Path $env:TEMP "sing-box-$SbVersion.zip"
    $dir = Join-Path $env:TEMP "sing-box-$SbVersion"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath $dir -Force
    New-Item -ItemType Directory -Force $Bin | Out-Null
    Get-ChildItem -LiteralPath $dir -Recurse -File |
        Where-Object { $_.Extension -in '.exe', '.dll' } |
        Copy-Item -Destination $Bin -Force
    Remove-Item -LiteralPath $zip, $dir -Recurse -Force
}

function Get-SubscriptionText([string]$src) {
    if ($src -match '^https?://') {
        $resp = Invoke-WebRequest -Uri $src -UseBasicParsing -TimeoutSec 30 -Headers @{ 'User-Agent' = 'v2rayN/7.10' }
        $content = $resp.Content
        if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
        return $content
    }
    if ($src -notmatch '://' -and (Test-Path -LiteralPath $src)) { return [IO.File]::ReadAllText($src) }
    return $src
}

function ConvertFrom-Base64Loose([string]$s) {
    $t = ($s -replace '\s', '').Replace('-', '+').Replace('_', '/')
    switch ($t.Length % 4) { 2 { $t += '==' } 3 { $t += '=' } }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($t))
}

function ConvertFrom-Query([string]$query) {
    $h = @{}
    if (-not $query) { return $h }
    foreach ($pair in $query.TrimStart('?').Split('&')) {
        if (-not $pair) { continue }
        $kv = $pair.Split('=', 2)
        $value = ''
        if ($kv.Count -gt 1) { $value = [Uri]::UnescapeDataString($kv[1]) }
        $h[$kv[0]] = $value
    }
    $h
}

function ConvertFrom-VlessLink([string]$link) {
    if ($link -notmatch '^vless://(?<uuid>[^@]+)@(?<host>\[[^\]]+\]|[^:/?#]+):(?<port>\d+)/?(?<query>\?[^#]*)?(?<name>#.*)?$') {
        return $null
    }
    $m = $Matches
    $q = ConvertFrom-Query $m['query']
    $name = $m['host']
    if ($m['name']) { $name = [Uri]::UnescapeDataString($m['name'].Substring(1)) }
    $network = 'tcp'
    if ($q['type']) { $network = $q['type'] }
    [pscustomobject]@{
        Name        = $name
        Server      = $m['host'].Trim('[', ']')
        Port        = [int]$m['port']
        Uuid        = [Uri]::UnescapeDataString($m['uuid'])
        Flow        = $q['flow']
        Security    = $q['security']
        Sni         = $q['sni']
        Fingerprint = $q['fp']
        PublicKey   = $q['pbk']
        ShortId     = $q['sid']
        Network     = $network
        Path        = $q['path']
        HostHeader  = $q['host']
        ServiceName = $q['serviceName']
    }
}

# Подписки для Happ и похожих клиентов иногда отдают готовые конфиги Xray (JSON).
function ConvertFrom-XrayJson($json) {
    foreach ($cfg in @($json)) {
        foreach ($ob in @($cfg.outbounds)) {
            if ($ob.protocol -ne 'vless') { continue }
            $st = $ob.settings
            if ($st.vnext) {
                $server = $st.vnext[0].address; $port = $st.vnext[0].port
                $uuid = $st.vnext[0].users[0].id; $flow = $st.vnext[0].users[0].flow
            } else {
                $server = $st.address; $port = $st.port; $uuid = $st.id; $flow = $st.flow
            }
            $ss = $ob.streamSettings
            $tlsCfg = $ss.tlsSettings
            if ($ss.security -eq 'reality') { $tlsCfg = $ss.realitySettings }
            $hostHeader = $ss.wsSettings.host
            if (-not $hostHeader -and $ss.wsSettings.headers) { $hostHeader = $ss.wsSettings.headers.Host }
            $name = $server
            if ($cfg.remarks) { $name = $cfg.remarks }
            $network = 'tcp'
            if ($ss.network) { $network = $ss.network }
            [pscustomobject]@{
                Name        = $name
                Server      = $server
                Port        = [int]$port
                Uuid        = $uuid
                Flow        = $flow
                Security    = $ss.security
                Sni         = $tlsCfg.serverName
                Fingerprint = $tlsCfg.fingerprint
                PublicKey   = $ss.realitySettings.publicKey
                ShortId     = $ss.realitySettings.shortId
                Network     = $network
                Path        = $ss.wsSettings.path
                HostHeader  = $hostHeader
                ServiceName = $ss.grpcSettings.serviceName
            }
        }
    }
}

function Get-Servers([string]$text) {
    $text = $text.Trim().TrimStart([char]0xFEFF)
    if ($text -notmatch '://' -and $text -notmatch '^[\[{]') {
        try { $text = (ConvertFrom-Base64Loose $text).Trim() } catch { throw 'Не удалось разобрать подписку: это не ссылки, не base64 и не JSON.' }
    }
    if ($text -match '^[\[{]') { return @(ConvertFrom-XrayJson ($text | ConvertFrom-Json)) }
    @($text -split '\r?\n' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -like 'vless://*' } |
        ForEach-Object { ConvertFrom-VlessLink $_ } |
        Where-Object { $_ })
}

function Select-Server($servers) {
    if ($servers.Count -eq 1) { return $servers[0] }
    if (-not $Reset -and (Test-Path -LiteralPath $ServerFile)) {
        $saved = [IO.File]::ReadAllText($ServerFile).Trim()
        $match = $servers | Where-Object { $_.Name -eq $saved } | Select-Object -First 1
        if ($match) { return $match }
    }
    Write-Info 'Серверы в подписке:'
    for ($i = 0; $i -lt $servers.Count; $i++) { Write-Host ("  {0}) {1}" -f ($i + 1), $servers[$i].Name) }
    do {
        $n = Read-Host 'Номер сервера (лучше ближайший к вам: Европа, Финляндия, Германия...)'
    } until ($n -match '^\d+$' -and [int]$n -ge 1 -and [int]$n -le $servers.Count)
    $s = $servers[[int]$n - 1]
    [IO.File]::WriteAllText($ServerFile, $s.Name)
    $s
}

function New-ProxyOutbound($s) {
    $out = [ordered]@{
        type        = 'vless'
        tag         = 'proxy'
        server      = $s.Server
        server_port = $s.Port
        uuid        = $s.Uuid
    }
    if ($s.Flow) { $out.flow = $s.Flow }

    if ($s.Security -eq 'tls' -or $s.Security -eq 'reality') {
        $fp = 'chrome'
        if ($s.Fingerprint -in 'chrome', 'firefox', 'edge', 'safari', '360', 'qq', 'ios', 'android', 'random', 'randomized') { $fp = $s.Fingerprint }
        $tls = [ordered]@{
            enabled = $true
            utls    = [ordered]@{ enabled = $true; fingerprint = $fp }
        }
        if ($s.Sni) { $tls.server_name = $s.Sni }
        if ($s.Security -eq 'reality') {
            $tls.reality = [ordered]@{ enabled = $true; public_key = $s.PublicKey; short_id = [string]$s.ShortId }
        }
        $out.tls = $tls
    }

    switch ($s.Network) {
        'tcp'  { }
        'raw'  { }
        'ws'   {
            $t = [ordered]@{ type = 'ws'; path = '/' }
            if ($s.Path) { $t.path = $s.Path }
            if ($s.HostHeader) { $t.headers = [ordered]@{ Host = $s.HostHeader } }
            $out.transport = $t
        }
        'grpc' { $out.transport = [ordered]@{ type = 'grpc'; service_name = [string]$s.ServiceName } }
        default { throw "Транспорт '$($s.Network)' этим скриптом не поддерживается. Выберите сервер с TCP, WS или gRPC." }
    }
    $out
}

function Get-FreeTunAddress {
    foreach ($cidr in $TunAddresses) {
        $ip = $cidr.Split('/')[0]
        if (-not (Get-NetIPAddress -IPAddress $ip -ErrorAction SilentlyContinue)) { return $cidr }
    }
    throw ('Все адреса для TUN заняты другими сетевыми адаптерами. Отключите другие VPN ' +
           'или допишите свободный адрес в $TunAddresses в начале скрипта.')
}

# $s — сервер из подписки (режим vpn) или $null (режим direct).
function New-Config($s, $tunAddress) {
    $matchers = @(
        [ordered]@{ process_name = $ProxyProcesses },
        [ordered]@{ domain_suffix = $ProxyDomains },
        [ordered]@{ ip_cidr = $ProxyCidrs }
    )
    $rules = New-Object Collections.ArrayList
    [void]$rules.Add([ordered]@{ action = 'sniff' })
    [void]$rules.Add([ordered]@{ ip_is_private = $true; outbound = 'direct' })
    if ($s) {
        $outbounds = @((New-ProxyOutbound $s), [ordered]@{ type = 'direct'; tag = 'direct' })
        foreach ($m in $matchers) {
            $m.outbound = 'proxy'
            [void]$rules.Add($m)
        }
    } else {
        $outbounds = @([ordered]@{ type = 'direct'; tag = 'direct' })
        # QUIC так не фрагментируется — отклоняем его, браузер сам перейдёт на TCP.
        [void]$rules.Add([ordered]@{ protocol = 'quic'; domain_suffix = $ProxyDomains; action = 'reject' })
        [void]$rules.Add([ordered]@{ protocol = 'quic'; ip_cidr = $ProxyCidrs; action = 'reject' })
        foreach ($m in $matchers) {
            $m.action = 'route'
            $m.outbound = 'direct'
            $m.tls_fragment = $true
            $m.tls_record_fragment = $true
            [void]$rules.Add($m)
        }
    }

    [ordered]@{
        log       = [ordered]@{ level = 'info'; timestamp = $true }
        dns       = [ordered]@{
            servers = @([ordered]@{ type = 'udp'; tag = 'dns-direct'; server = '1.1.1.1' })
            final   = 'dns-direct'
        }
        inbounds  = @([ordered]@{
            type           = 'tun'
            tag            = 'tun-in'
            interface_name = 'faceit-tunnel'
            address        = @($tunAddress)
            mtu            = 9000
            auto_route     = $true
            strict_route   = $false
            stack          = 'mixed'
        })
        outbounds = $outbounds
        route     = [ordered]@{
            rules                   = $rules.ToArray()
            final                  = 'direct'
            auto_detect_interface   = $true
            default_domain_resolver = 'dns-direct'
        }
    }
}

# ---- Основной сценарий --------------------------------------------------------

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $CheckOnly -and -not $isAdmin) {
    throw 'Нужны права администратора (для TUN). Запускайте через start.bat.'
}

Install-SingBox

$server = $null
if ($Mode -eq 'vpn') {
    if ($Subscription) {
        $src = $Subscription
    } elseif (-not $Reset -and (Test-Path -LiteralPath $SubFile)) {
        $src = [IO.File]::ReadAllText($SubFile).Trim()
    } else {
        Write-Info 'Вставьте ссылку на подписку (https://...) или ссылку на сервер (vless://...) и нажмите Enter.'
        Write-Info 'Вставка в этом окне — правой кнопкой мыши.'
        $src = (Read-Host 'Подписка').Trim()
        [IO.File]::WriteAllText($SubFile, $src)
        if (Test-Path -LiteralPath $ServerFile) { Remove-Item -LiteralPath $ServerFile }
    }

    Write-Info 'Загружаю подписку...'
    # @() обязателен: один сервер PowerShell 5.1 вернёт как объект без .Count
    $servers = @(Get-Servers (Get-SubscriptionText $src))
    if ($servers.Count -eq 0) {
        throw ('В подписке не найдено серверов VLESS. Можно скопировать ссылку на сервер (vless://...) ' +
               'в Happ и вставить её вместо подписки через change-subscription.bat.')
    }
    $server = Select-Server $servers
    Write-Info "Сервер: $($server.Name)"
} else {
    Write-Info 'Режим без VPN: Faceit идёт напрямую с вашего IP, блокировку обходит фрагментация TLS.'
}

# Прошлый запуск мог остаться висеть и держать TUN-адаптер — тогда новый не поднимется.
$stale = Get-Process -Name 'sing-box' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $SingBox }
if ($stale -and -not $CheckOnly) {
    Write-Warn 'Останавливаю sing-box, оставшийся от прошлого запуска...'
    $stale | Stop-Process -Force
    Start-Sleep -Seconds 2
}

$json = ConvertTo-Json -InputObject (New-Config $server (Get-FreeTunAddress)) -Depth 20
[IO.File]::WriteAllText($ConfigFile, $json, (New-Object Text.UTF8Encoding($false)))

& $SingBox check -c $ConfigFile
if ($LASTEXITCODE -ne 0) { throw 'sing-box не принял config.json (ошибка выше).' }
Write-Info 'config.json собран и проверен.'
if ($CheckOnly) { return }

if ($server) {
    $serverIp = $server.Server
    if ($serverIp -notmatch '^[\d.]+$') {
        try { $serverIp = (Resolve-DnsName $server.Server -Type A | Where-Object IPAddress | Select-Object -First 1).IPAddress } catch { }
    }
    Write-Warn "Если работает zapret: добавьте $serverIp в lists\ipset-exclude.txt и перезапустите zapret."
} else {
    Write-Warn 'Если работает zapret: Faceit не должен быть в его списках (list-general.txt, ipset-all.txt).'
}

$others = Get-Process -Name 'Happ', 'xray', 'v2rayN', 'Hiddify', 'nekobox', 'Throne', 'clash-verge', 'FlClash', 'Proxifier' -ErrorAction SilentlyContinue
if ($others) {
    Write-Warn ("Запущены другие VPN или прокси: {0}. Отключите их, иначе будет конфликт." -f (($others.Name | Sort-Object -Unique) -join ', '))
}

Write-Host ''
Write-Info 'Подключаюсь. Ниже зелёным видны соединения Faceit. Полный лог — в sing-box.log.'
Write-Info 'Отключиться: Ctrl+C или закрыть окно.'
Write-Host ''

# sing-box пишет лог в stderr; в Windows PowerShell 5.1 это ErrorRecord, поэтому без Stop.
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ansi = [char]27 + '\[[0-9;]*m'
$log = New-Object IO.StreamWriter($LogFile, $false, (New-Object Text.UTF8Encoding($false)))
$log.AutoFlush = $true
try {
    & $SingBox run -c $ConfigFile 2>&1 | ForEach-Object {
        $line = ("$_") -replace $ansi, ''
        $log.WriteLine($line)
        if ($line -match 'FATAL|ERROR') { Write-Host $line -ForegroundColor Red }
        elseif ($line -match 'WARN') { Write-Host $line -ForegroundColor Yellow }
        elseif ($line -match '\[proxy\]|faceit|193\.41\.200\.|sing-box started') { Write-Host $line -ForegroundColor Green }
    }
} finally {
    $log.Dispose()
}
