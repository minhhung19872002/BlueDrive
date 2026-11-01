#!/usr/bin/env sh
# BlueDrive CI (Linux/macOS): build + test backend, build + lint web, validate docker compose.
set -eu
cd "$(dirname "$0")/.."
dotnet build BlueDrive.slnx -c Release --nologo
dotnet test tests/BlueDrive.UnitTests -c Release --no-build --nologo
[ "${SKIP_INTEGRATION:-0}" = "1" ] || dotnet test tests/BlueDrive.IntegrationTests -c Release --no-build --nologo
(cd web && npm ci --no-audit --no-fund && npm run lint && npm run build)
POSTGRES_PASSWORD=ci JWT_SECRET=ci_secret_ci_secret_ci_secret_ci_secret ADMIN_PASSWORD='Ci@12345' \
STORAGE_MASTER_KEY=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= ONLYOFFICE_JWT_SECRET=ci \
  docker compose -f deploy/docker-compose.yml config --quiet
echo "CI OK"
