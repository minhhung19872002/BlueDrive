#!/usr/bin/env sh
# Nạp dữ liệu mẫu (canbo1..canbo5 / DEMO_PASSWORD trong deploy/.env, mặc định Demo@12345).
set -eu
cd "$(dirname "$0")/../deploy"
sed -i 's/^SEED_DEMO=.*/SEED_DEMO=true/' .env
grep -q '^DEMO_PASSWORD=.' .env || printf '\nDEMO_PASSWORD=Demo@12345\n' >> .env
docker compose up -d api
