#!/usr/bin/env sh
# Nạp dữ liệu mẫu (canbo1..canbo5 / DEMO_PASSWORD trong deploy/.env; để trống thì tự sinh ngẫu nhiên).
set -eu
cd "$(dirname "$0")/../deploy"
sed -i 's/^SEED_DEMO=.*/SEED_DEMO=true/' .env
if ! grep -q '^DEMO_PASSWORD=[^[:space:]]' .env; then
  # No shared default: upper, lower, digit and symbol guaranteed for the password policy.
  sed -i '/^DEMO_PASSWORD=/d' .env
  printf '\nDEMO_PASSWORD=Dm@%s7\n' "$(LC_ALL=C tr -dc 'A-HJ-NP-Za-km-z2-9' </dev/urandom | head -c 10)" >> .env
fi
docker compose up -d api
echo "Người dùng mẫu: canbo1..canbo5 / $(sed -n 's/^DEMO_PASSWORD=//p' .env)"
