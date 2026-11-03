#!/bin/bash
# BlueDrive Desktop for macOS (Sync-Phase 10): self-contained publish (full sync mode), app icon, Velopack .app bundle + installer .pkg
# + update feed (channel osx-<stable|beta>). Runs on macOS (vpk cannot pack macOS apps elsewhere); used by .github/workflows/desktop-release.yml.
#   desktop/packaging/macos/build-macos.sh <version> [stable|beta] [osx-arm64|osx-x64] [output dir]
# Signing / notarization (optional, Apple Developer ID): BLUEDRIVE_MAC_APP_IDENTITY, BLUEDRIVE_MAC_INSTALL_IDENTITY, BLUEDRIVE_MAC_NOTARY_PROFILE.
# Without them the app is ad-hoc signed: Gatekeeper asks the user to confirm the first start (Finder → Open).
set -euo pipefail
version="${1:?phiên bản, ví dụ 1.2.0}"
channel="${2:-stable}"
rid="${3:-osx-arm64}"
root="$(cd "$(dirname "$0")/../../.." && pwd)"
out="${4:-$root/artifacts/desktop}"
work="$root/artifacts/desktop-build/$rid"
publish="$work/publish"
mkdir -p "$out" "$work"
rm -rf "$publish"

echo "==> Biên dịch BlueDrive Desktop $version ($rid)"
dotnet publish "$root/desktop/src/BlueDrive.Desktop/BlueDrive.Desktop.csproj" -f net10.0 -r "$rid" -c Release --self-contained true \
  -p:Version="$version" -p:DebugType=none -o "$publish" -v q --nologo

echo "==> Biểu tượng"
iconset="$work/bluedrive.iconset"
rm -rf "$iconset" && mkdir -p "$iconset"
png="$root/desktop/src/BlueDrive.Desktop/Assets/bluedrive-256.png"
for s in 16 32 128 256; do
  sips -z "$s" "$s" "$png" --out "$iconset/icon_${s}x${s}.png" > /dev/null
  d=$((s * 2)); sips -z "$d" "$d" "$png" --out "$iconset/icon_${s}x${s}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$work/bluedrive.icns"

echo "==> Đóng gói Velopack (osx-$channel)"
args=(pack --packId BlueDrive.Desktop --packVersion "$version" --packDir "$publish" --mainExe BlueDrive --packTitle BlueDrive
  --packAuthors BLUESTAR --icon "$work/bluedrive.icns" --bundleId vn.bluestar.bluedrive --channel "osx-$channel" --runtime "$rid"
  --outputDir "$out" --delta None --noPortable --yes)
[ -n "${BLUEDRIVE_MAC_APP_IDENTITY:-}" ] && args+=(--signAppIdentity "$BLUEDRIVE_MAC_APP_IDENTITY")
[ -n "${BLUEDRIVE_MAC_INSTALL_IDENTITY:-}" ] && args+=(--signInstallIdentity "$BLUEDRIVE_MAC_INSTALL_IDENTITY")
[ -n "${BLUEDRIVE_MAC_NOTARY_PROFILE:-}" ] && args+=(--notaryProfile "$BLUEDRIVE_MAC_NOTARY_PROFILE")
[ -n "${RELEASE_NOTES:-}" ] && args+=(--releaseNotes "$RELEASE_NOTES")
vpk "${args[@]}"

name="BlueDrive.pkg"; [ "$channel" = stable ] || name="BlueDrive-$channel.pkg"
cp "$out/BlueDrive.Desktop-osx-$channel-Setup.pkg" "$out/$name"
rm "$out/BlueDrive.Desktop-osx-$channel-Setup.pkg"
echo "Xong: $out/$name"
ls -la "$out"
