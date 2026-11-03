#!/bin/sh
# Copies the latest BlueDrive Desktop releases (GitHub Releases tagged desktop-v*, built by .github/workflows/desktop-release.yml)
# into the folder nginx serves at /downloads/desktop/ (deploy/docker-compose.yml: DESKTOP_DOWNLOADS_DIR, default deploy/downloads).
#   stable = newest non-prerelease, beta = newest prerelease. Writes releases.json (versions, sizes, SHA-256) for the web page
#   "Tải ứng dụng máy tính"; the Velopack update feed is releases.<channel>.json + packages.
# Called by scripts/deploy-vm.sh after each deploy; can be run by hand: scripts/sync-desktop-downloads.sh [downloads dir]
# Needs curl, jq, sha256sum. Unchanged files (same size) are not downloaded again. Never fails the deploy (exit 0 on network errors).
set -eu
repo="${DESKTOP_RELEASES_REPO:-minhhung19872002/BlueDrive}"
root="${1:-${DESKTOP_DOWNLOADS_DIR:-$(cd "$(dirname "$0")/../deploy" && pwd)/downloads}}"
dest="$root/desktop"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$dest" "$work/new"

if ! curl -fsSL --retry 3 -H "Accept: application/vnd.github+json" "https://api.github.com/repos/$repo/releases?per_page=50" > "$work/list.json"; then
  echo "sync-desktop-downloads: không lấy được danh sách bản phát hành từ GitHub, giữ nguyên bản hiện có" >&2
  exit 0
fi

echo '{}' > "$work/channels.json"
for ch in stable beta; do
  if [ "$ch" = stable ]; then pre=false; else pre=true; fi
  jq -c --argjson pre "$pre" '[.[] | select((.tag_name | startswith("desktop-v")) and (.draft | not) and (.prerelease == $pre))] | first // empty' \
    "$work/list.json" > "$work/$ch.json"
  [ -s "$work/$ch.json" ] || continue
  version=$(jq -r '.tag_name | sub("^desktop-v"; "")' "$work/$ch.json")
  jq -r '.assets[] | [.name, .browser_download_url, (.size | tostring)] | @tsv' "$work/$ch.json" > "$work/$ch.assets"
  ok=true
  while IFS="$(printf '\t')" read -r name url size; do
    if [ -f "$dest/$name" ] && [ "$(wc -c < "$dest/$name" | tr -d ' ')" = "$size" ]; then
      cp -p "$dest/$name" "$work/new/$name"
    elif ! curl -fsSL --retry 3 -o "$work/new/$name" "$url"; then
      echo "sync-desktop-downloads: tải $name không thành công" >&2
      ok=false
    fi
  done < "$work/$ch.assets"
  $ok || { echo "sync-desktop-downloads: bỏ qua lần cập nhật này, giữ nguyên bản hiện có" >&2; exit 0; }

  setup=BlueDriveSetup.exe; [ "$ch" = stable ] || setup="BlueDriveSetup-$ch.exe"
  msix=BlueDrive.msix; [ "$ch" = stable ] || msix="BlueDrive-$ch.msix"
  describe() { # file → {file,size,sha256} or null
    if [ -f "$work/new/$1" ]; then
      jq -n --arg f "$1" --argjson s "$(wc -c < "$work/new/$1" | tr -d ' ')" --arg h "$(sha256sum "$work/new/$1" | cut -d' ' -f1)" '{file:$f,size:$s,sha256:$h}'
    else echo null; fi
  }
  pkg=BlueDrive.pkg; [ "$ch" = stable ] || pkg="BlueDrive-$ch.pkg"
  sfx=""; [ "$ch" = stable ] || sfx="-$ch"
  appimage="BlueDrive$sfx-x86_64.AppImage"
  deb=$(cd "$work/new" && ls "bluedrive${sfx}_"*_amd64.deb 2>/dev/null | head -1 || true)
  jq --arg ch "$ch" --arg v "$version" --argjson setup "$(describe "$setup")" --argjson msix "$(describe "$msix")" \
     --argjson macos "$(describe "$pkg")" --argjson appimage "$(describe "$appimage")" --argjson deb "$(describe "${deb:-none}")" \
     --arg at "$(jq -r '.published_at' "$work/$ch.json")" --arg notes "$(jq -r '.body // ""' "$work/$ch.json" | tr -d '\r')" \
     '.[$ch] = {version:$v, publishedAt:$at, runtime:"win-x64", minimumOs:"Windows 10 1809 (17763)", setup:$setup, msix:$msix, macos:$macos,
                linux:(if $appimage == null and $deb == null then null else {appimage:$appimage, deb:$deb} end),
                notes:(if $notes == "" then null else $notes end)}' "$work/channels.json" > "$work/channels.tmp"
  mv "$work/channels.tmp" "$work/channels.json"
done

jq -n --slurpfile c "$work/channels.json" '{product:"BlueDrive Desktop", channels:$c[0]}' > "$work/new/releases.json"
# Swap in one step (nginx serves the parent folder).
rm -rf "$root/desktop.old"
mv "$dest" "$root/desktop.old"
mv "$work/new" "$dest"
chmod 755 "$dest"
rm -rf "$root/desktop.old"
echo "sync-desktop-downloads: $(jq -r '.channels | to_entries | map(.key + " " + .value.version) | join(", ")' "$dest/releases.json")"
