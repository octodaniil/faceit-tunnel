#!/usr/bin/env bash
# Faceit Tunnel — свой VPN-сервер на домашнем Linux (Debian/Ubuntu с systemd).
# Ставит sing-box (тот же движок, что в клиенте Faceit Tunnel) с VLESS + REALITY
# и печатает ссылку vless://... для start.bat / change-subscription.bat.
#
#   sudo bash install-server.sh
#
# Повторный запуск не меняет ключи и печатает ту же ссылку.
# Если раньше стоял Xray от install-xray.sh, его ключи переносятся, а сам Xray отключается.
# Настройки через переменные окружения (sudo PORT=8443 bash install-server.sh):
#   PORT=443               порт, который проброшен на роутере
#   HOST=myhome.ddns.net   адрес для ссылки (по умолчанию — внешний IP)
#   SNI=www.microsoft.com  сайт, под который маскируется соединение
#   NAME=home              название сервера в ссылке
set -euo pipefail

SB_VERSION=1.14.4
PORT="${PORT:-443}"
SNI="${SNI:-www.microsoft.com}"
NAME="${NAME:-home}"

CONF_DIR=/etc/sing-box
CONF="$CONF_DIR/config.json"
STATE="$CONF_DIR/faceit-tunnel.env"
XRAY_STATE=/usr/local/etc/xray/faceit-tunnel.env

if [ "$(id -u)" -ne 0 ]; then
    echo "Запустите через sudo." >&2
    exit 1
fi

if ! command -v curl >/dev/null; then
    apt-get update -qq && apt-get install -y -qq curl
fi

if ! command -v sing-box >/dev/null || ! sing-box version | grep -q "$SB_VERSION"; then
    echo "==> Устанавливаю sing-box $SB_VERSION..."
    ARCH="$(dpkg --print-architecture)"
    DEB="$(mktemp --suffix=.deb)"
    curl -fsSL -o "$DEB" "https://github.com/SagerNet/sing-box/releases/download/v$SB_VERSION/sing-box_${SB_VERSION}_linux_${ARCH}.deb"
    dpkg -i "$DEB" >/dev/null
    rm -f "$DEB"
fi

mkdir -p "$CONF_DIR"
if [ -f "$STATE" ]; then
    # shellcheck disable=SC1090
    . "$STATE"
elif [ -f "$XRAY_STATE" ]; then
    echo "==> Переношу ключи из Xray (ссылка не изменится)..."
    # shellcheck disable=SC1090
    . "$XRAY_STATE"
else
    echo "==> Создаю ключи..."
    UUID="$(sing-box generate uuid)"
    KEYS="$(sing-box generate reality-keypair)"
    PRIVATE_KEY="$(printf '%s\n' "$KEYS" | awk -F': *' '/^PrivateKey/ {print $2; exit}')"
    PUBLIC_KEY="$(printf '%s\n' "$KEYS" | awk -F': *' '/^PublicKey/ {print $2; exit}')"
    SHORT_ID="$(head -c 8 /dev/urandom | od -An -tx1 | tr -d ' \n')"
fi
if [ ! -f "$STATE" ]; then
    (umask 077 && cat > "$STATE" <<EOF
UUID='$UUID'
PRIVATE_KEY='$PRIVATE_KEY'
PUBLIC_KEY='$PUBLIC_KEY'
SHORT_ID='$SHORT_ID'
EOF
    )
fi

# Xray с тем же портом мешает sing-box — отключаем его, если он остался от прошлой установки.
if systemctl list-unit-files xray.service >/dev/null 2>&1 && systemctl is-enabled --quiet xray 2>/dev/null; then
    echo "==> Отключаю Xray..."
    systemctl disable --now xray >/dev/null 2>&1 || true
fi

echo "==> Пишу $CONF (порт $PORT, маскировка под $SNI)..."
(umask 077 && cat > "$CONF" <<EOF
{
  "log": { "level": "warn" },
  "inbounds": [
    {
      "type": "vless",
      "tag": "vless-in",
      "listen": "::",
      "listen_port": $PORT,
      "users": [ { "uuid": "$UUID", "flow": "xtls-rprx-vision" } ],
      "tls": {
        "enabled": true,
        "server_name": "$SNI",
        "reality": {
          "enabled": true,
          "handshake": { "server": "$SNI", "server_port": 443 },
          "private_key": "$PRIVATE_KEY",
          "short_id": [ "$SHORT_ID" ]
        }
      }
    }
  ],
  "outbounds": [ { "type": "direct", "tag": "direct" } ],
  "route": {
    "rules": [
      { "ip_is_private": true, "action": "reject" }
    ],
    "final": "direct"
  }
}
EOF
)

# Служба из пакета может работать не от root — тогда даём её группе прочитать конфиг.
SERVICE_GROUP="$(systemctl show -p Group --value sing-box 2>/dev/null || true)"
if [ -n "$SERVICE_GROUP" ] && [ "$SERVICE_GROUP" != root ]; then
    chown root:"$SERVICE_GROUP" "$CONF_DIR" "$CONF"
    chmod 750 "$CONF_DIR"
    chmod 640 "$CONF"
fi

sing-box check -c "$CONF"

if command -v ufw >/dev/null && ufw status | grep -q '^Status: active'; then
    echo "==> Открываю порт $PORT/tcp в ufw..."
    ufw allow "$PORT/tcp" >/dev/null
fi

systemctl enable sing-box >/dev/null 2>&1
systemctl restart sing-box
sleep 1
if ! systemctl is-active --quiet sing-box; then
    echo "sing-box не запустился, лог:" >&2
    journalctl -u sing-box -n 20 --no-pager >&2
    exit 1
fi

if [ -z "${HOST:-}" ]; then
    HOST="$(curl -4 -fsS --max-time 10 https://ifconfig.me || true)"
fi
if [ -z "$HOST" ]; then
    echo "Не удалось узнать внешний IP — задайте HOST=... и запустите снова." >&2
    exit 1
fi

LINK="vless://$UUID@$HOST:$PORT?type=tcp&security=reality&pbk=$PUBLIC_KEY&sid=$SHORT_ID&sni=$SNI&fp=chrome&flow=xtls-rprx-vision#$NAME"

echo
echo "Готово. sing-box работает и запускается сам после перезагрузки."
echo
echo "Ссылка для Faceit Tunnel (передайте другу лично — это ключ доступа):"
echo
echo "  $LINK"
echo
echo "Дальше:"
echo "  1. На роутере пробросьте TCP-порт $PORT на этот сервер ($(hostname -I | awk '{print $1}'):$PORT)."
echo "  2. Друг запускает change-subscription.bat и вставляет эту ссылку."
echo "  3. Если у друга стоит zapret: добавить $HOST в lists/ipset-exclude.txt."
