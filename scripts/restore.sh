#!/usr/bin/env sh
# Phục hồi từ bản sao lưu. Dùng: ./scripts/restore.sh <backup-id> [--yes]
set -eu
[ $# -ge 1 ] || { echo "Dùng: $0 <backup-id> [--yes]"; exit 1; }
if [ "${2:-}" != "--yes" ]; then
  printf "Phục hồi sẽ GHI ĐÈ dữ liệu hiện tại. Gõ 'PHUC HOI' để tiếp tục: "; read -r ans
  [ "$ans" = "PHUC HOI" ] || { echo "Đã hủy"; exit 1; }
fi
cd "$(dirname "$0")/../deploy"
docker compose stop api worker
set +e
docker compose run --rm --no-deps worker restore "$1"; code=$?
set -e
docker compose start api worker
[ $code -eq 0 ] && echo "Phục hồi thành công." || { echo "Phục hồi thất bại"; exit $code; }
