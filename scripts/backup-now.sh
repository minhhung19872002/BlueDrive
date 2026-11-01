#!/usr/bin/env sh
# Sao lưu đột xuất. Dùng: ./scripts/backup-now.sh [full]
set -eu
cd "$(dirname "$0")/../deploy"
docker compose run --rm --no-deps worker backup "${1:-auto}"
