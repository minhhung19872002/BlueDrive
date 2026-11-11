#!/bin/sh
# Update BlueDrive on the VM to a pushed commit and restart the stack (CD step of .github/workflows/ci-cd.yml).
# Installed on the VM as ~/apps/bluedrive-deploy.sh; the GitHub Actions deploy key is pinned to it
# (command="~/apps/bluedrive-deploy.sh",restrict in ~/.ssh/authorized_keys), so "<sha> pull" comes in
# SSH_ORIGINAL_COMMAND.
#   pull  (CD): the images were built on GitHub Actions and pushed to GHCR; this VM only pulls them. Two lines
#         on stdin: GHCR user, then a token that can read the packages (the job's GITHUB_TOKEN).
#   build (default; manual: ~/apps/bluedrive-deploy.sh <git sha>): build here. Avoid it while the VM is busy -
#         it is shared, has 11 GB RAM and no swap, and two .NET builds take several GB.
# deploy/.env (secrets) is git-ignored and never touched.
set -eu
set -f
[ $# -gt 0 ] || set -- ${SSH_ORIGINAL_COMMAND:-}
set +f
rev="${1:-}"
mode="${2:-build}"
usage() { echo "usage: bluedrive-deploy.sh <git sha> [build|pull]" >&2; exit 2; }
case "$rev" in *[!0-9a-f]* | "") usage ;; esac
case "$mode" in build | pull) ;; *) usage ;; esac
registry=ghcr.io/minhhung19872002
app="${BLUEDRIVE_HOME:-$HOME/BlueDrive}"
exec 9> "$HOME/apps/bluedrive.lock"
flock 9
log=$(mktemp)
ghcr=$(mktemp -d)   # throwaway docker config for the GHCR login; ~/.docker belongs to every app on the VM
trap 'rm -rf "$log" "$ghcr"' EXIT
# Show the tail of a step's output; a failing step stops the deploy with its log (sh has no pipefail).
step() { if ! "$@" > "$log" 2>&1; then tail -40 "$log"; echo "FAILED: $*" >&2; exit 1; fi; grep -vE "^#|^ *$" "$log" | tail -12; }
cd "$app"
step git fetch -q origin main
step git checkout -q -B main "$rev"
cd deploy
# Shown in "Thông tin hệ thống"; the environment overrides the GIT_SHA written to .env at install time.
GIT_SHA=$(git rev-parse --short HEAD)
export GIT_SHA
# Created by us (not by Docker as root) so that sync-desktop-downloads.sh can write into it.
mkdir -p downloads
if [ "$mode" = pull ]; then
  IFS= read -r ghcr_user && IFS= read -r ghcr_token || { echo "FAILED: pull needs '<user>' and '<token>' on stdin" >&2; exit 1; }
  printf '%s' "$ghcr_token" | docker --config "$ghcr" login ghcr.io -u "$ghcr_user" --password-stdin > "$log" 2>&1 \
    || { tail -5 "$log"; echo "FAILED: docker login ghcr.io" >&2; exit 1; }
  sha=$(git rev-parse HEAD)
  for svc in api worker web; do
    remote="$registry/bluedrive-$svc:$sha"
    # The name compose expects for this service (bluedrive/<svc>:$APP_VERSION), so docker-compose.yml stays as is.
    target=$(docker compose config --images | grep "^bluedrive/$svc:" | head -1)
    [ -n "$target" ] || { echo "FAILED: no compose image for $svc" >&2; exit 1; }
    step docker --config "$ghcr" pull -q "$remote"
    docker tag "$remote" "$target"
    docker rmi "$remote" > /dev/null || true   # drops the GHCR tag only
  done
  step docker compose up -d --no-build --wait --wait-timeout 600 --remove-orphans
else
  step docker compose up -d --build --wait --wait-timeout 600 --remove-orphans
fi
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
