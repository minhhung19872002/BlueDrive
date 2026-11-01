#!/usr/bin/env sh
# Nạp dữ liệu mẫu (canbo1..canbo5 / Demo@12345).
set -eu
cd "$(dirname "$0")/../deploy"
sed -i 's/^SEED_DEMO=.*/SEED_DEMO=true/' .env
docker compose up -d api
