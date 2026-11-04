#!/usr/bin/env sh
# Cài đặt trọn gói BlueDrive trên máy chủ Linux sạch (cần Docker + Docker Compose v2).
# Dùng: ./scripts/install.sh [domain] [acme-email]   (ví dụ: ./scripts/install.sh drive.example.gov.vn admin@example.gov.vn)
# Máy chủ đã có dịch vụ khác chiếm cổng 80/443: HTTP_PORT=8098 HTTPS_PORT=8448 PUBLIC_BASE_URL=http://<ip>:8098 ./scripts/install.sh
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEPLOY="$ROOT/deploy"
DOMAIN="${1:-}"
ACME="${2:-}"
HTTP_PORT="${HTTP_PORT:-80}"
HTTPS_PORT="${HTTPS_PORT:-443}"
rnd() { LC_ALL=C tr -dc 'A-HJ-NP-Za-km-z2-9' </dev/urandom | head -c "$1"; }
docker version >/dev/null || { echo "Docker chưa chạy"; exit 1; }
if [ ! -f "$DEPLOY/.env" ]; then
  ADMIN_PW="Bd@$(rnd 12)"
  # Upper, lower, digit and symbol guaranteed so the default password policy accepts it.
  DEMO_PW="Dm@$(rnd 10)7"
  if [ -n "$DOMAIN" ]; then SITE="$DOMAIN"; URL="https://$DOMAIN"
  elif [ "$HTTP_PORT" != 80 ]; then SITE=":80"; URL="http://localhost:$HTTP_PORT"
  else SITE=":80"; URL="http://localhost"; fi
  URL="${PUBLIC_BASE_URL:-$URL}"
  sed -e "s|^SITE_ADDRESS=.*|SITE_ADDRESS=$SITE|" \
      -e "s|^PUBLIC_BASE_URL=.*|PUBLIC_BASE_URL=$URL|" \
      -e "s|^ACME_EMAIL=.*|ACME_EMAIL=$ACME|" \
      -e "s|^HTTP_PORT=.*|HTTP_PORT=$HTTP_PORT|" \
      -e "s|^HTTPS_PORT=.*|HTTPS_PORT=$HTTPS_PORT|" \
      -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$(rnd 32)|" \
      -e "s|^JWT_SECRET=.*|JWT_SECRET=$(rnd 48)|" \
      -e "s|^ONLYOFFICE_JWT_SECRET=.*|ONLYOFFICE_JWT_SECRET=$(rnd 40)|" \
      -e "s|^STORAGE_MASTER_KEY=.*|STORAGE_MASTER_KEY=$(head -c 32 /dev/urandom | base64)|" \
      -e "s|^ADMIN_PASSWORD=.*|ADMIN_PASSWORD=$ADMIN_PW|" \
      -e "s|^DEMO_PASSWORD=.*|DEMO_PASSWORD=$DEMO_PW|" \
      -e "s|^GIT_SHA=.*|GIT_SHA=$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo dev)|" \
      "$DEPLOY/.env.example" > "$DEPLOY/.env"
  chmod 600 "$DEPLOY/.env"
  echo "Mật khẩu quản trị ban đầu: $ADMIN_PW (bắt buộc đổi khi đăng nhập lần đầu)"
  echo "QUAN TRỌNG: sao lưu deploy/.env — mất STORAGE_MASTER_KEY sẽ không giải mã được dữ liệu."
fi
cd "$DEPLOY"
docker compose up -d --build
PORT="$(grep '^HTTP_PORT=' .env | cut -d= -f2)"
i=0
until curl -fsS "http://localhost:${PORT:-80}/health" | grep -q Healthy; do
  i=$((i+1)); [ "$i" -gt 90 ] && { echo "Hệ thống chưa sẵn sàng, xem: docker compose logs api"; exit 1; }
  sleep 5
done
echo "BlueDrive đã sẵn sàng: $(grep '^PUBLIC_BASE_URL=' .env | cut -d= -f2)"
