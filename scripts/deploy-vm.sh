#!/bin/sh
# Update BlueDrive on the VM to a pushed commit, rebuild and restart the stack (CD step of .github/workflows/ci-cd.yml).
# Installed on the VM as ~/apps/bluedrive-deploy.sh; the GitHub Actions deploy key is pinned to it
# (command="~/apps/bluedrive-deploy.sh",restrict in ~/.ssh/authorized_keys), so the SHA comes in SSH_ORIGINAL_COMMAND.
#   Manual: ~/apps/bluedrive-deploy.sh <git sha>
# deploy/.env (secrets) is git-ignored and never touched.
set -eu
rev="${1:-${SSH_ORIGINAL_COMMAND:-}}"
case "$rev" in
  *[!0-9a-f]* | "") echo "usage: bluedrive-deploy.sh <git sha>" >&2; exit 2 ;;
esac
app="${BLUEDRIVE_HOME:-$HOME/BlueDrive}"
exec 9> "$HOME/apps/bluedrive.lock"
flock 9
log=$(mktemp)
trap 'rm -f "$log"' EXIT
# Show the tail of a step's output; a failing step stops the deploy with its log (sh has no pipefail).
step() { if ! "$@" > "$log" 2>&1; then tail -40 "$log"; echo "FAILED: $*" >&2; exit 1; fi; grep -vE "^#|^ *$" "$log" | tail -12; }
cd "$app"
step git fetch -q origin main
step git checkout -q -B main "$rev"
cd deploy
# Shown in "Thông tin hệ thống"; the environment overrides the GIT_SHA written to .env at install time.
GIT_SHA=$(git rev-parse --short HEAD)
export GIT_SHA
step docker compose up -d --build --wait --wait-timeout 600 --remove-orphans
port=$(grep '^HTTP_PORT=' .env | cut -d= -f2)
i=0
until curl -fsS "http://localhost:${port:-80}/health" 2>/dev/null | grep -q Healthy; do
  i=$((i+1)); [ "$i" -gt 60 ] && { echo "FAILED: /health" >&2; docker compose logs --tail 40 api >&2; exit 1; }
  sleep 5
done
# BlueDrive Desktop installers + update feed from the GitHub release (never fails the deploy).
sh "$app/scripts/sync-desktop-downloads.sh" "$app/deploy/downloads" || true
echo "$rev" > "$HOME/apps/bluedrive.REVISION"
docker image prune -f > /dev/null
docker builder prune -f --filter until=72h > /dev/null
echo "Deployed $rev"
