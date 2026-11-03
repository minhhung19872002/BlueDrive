#!/bin/bash
# BlueDrive Desktop for Linux (Sync-Phase 11): self-contained publish (full sync mode), Velopack AppImage (updates itself, channel
# linux-<stable|beta>) and a .deb package (/opt/bluedrive, updated through apt / the IT department). Runs on Linux (Velopack packs Linux
# apps on Linux only); used by .github/workflows/desktop-release.yml.
#   desktop/packaging/linux/build-linux.sh <version> [stable|beta] [output dir]
# Needs: .NET 10 SDK, vpk 1.2.161, dpkg-deb.
set -euo pipefail
version="${1:?phiên bản, ví dụ 1.2.0}"
channel="${2:-stable}"
root="$(cd "$(dirname "$0")/../../.." && pwd)"
out="${3:-$root/artifacts/desktop}"
rid=linux-x64
work="$root/artifacts/desktop-build/$rid"
publish="$work/publish"
icon="$root/desktop/src/BlueDrive.Desktop/Assets/bluedrive-256.png"
mkdir -p "$out" "$work"
rm -rf "$publish"

echo "==> Biên dịch BlueDrive Desktop $version ($rid)"
dotnet publish "$root/desktop/src/BlueDrive.Desktop/BlueDrive.Desktop.csproj" -f net10.0 -r "$rid" -c Release --self-contained true \
  -p:Version="$version" -p:DebugType=none -o "$publish" -v q --nologo

echo "==> AppImage (Velopack, kênh linux-$channel)"
args=(pack --packId BlueDrive.Desktop --packVersion "$version" --packDir "$publish" --mainExe BlueDrive --packTitle BlueDrive
  --packAuthors BLUESTAR --icon "$icon" --categories "Network;FileTransfer;" --channel "linux-$channel" --runtime "$rid"
  --outputDir "$out" --delta None --yes)
[ -n "${RELEASE_NOTES:-}" ] && args+=(--releaseNotes "$RELEASE_NOTES")
vpk "${args[@]}"
suffix=""; [ "$channel" = stable ] || suffix="-$channel"
appimage=$(ls "$out"/*linux-"$channel"*.AppImage | head -1)
mv "$appimage" "$out/BlueDrive$suffix-x86_64.AppImage"

echo "==> Gói .deb"
debver="${version//-/\~}" # 1.3.0-beta.1 → 1.3.0~beta.1 (sorts before 1.3.0)
pkg="$work/deb"
rm -rf "$pkg"
mkdir -p "$pkg/DEBIAN" "$pkg/opt/bluedrive" "$pkg/usr/bin" "$pkg/usr/share/applications" "$pkg/usr/share/icons/hicolor/256x256/apps"
cp -a "$publish/." "$pkg/opt/bluedrive/"
ln -s /opt/bluedrive/BlueDrive "$pkg/usr/bin/bluedrive"
cp "$icon" "$pkg/usr/share/icons/hicolor/256x256/apps/bluedrive.png"
cat > "$pkg/usr/share/applications/bluedrive.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=BlueDrive
Comment=Đồng bộ tư liệu BlueDrive xuống máy tính
Exec=/opt/bluedrive/BlueDrive %U
Icon=bluedrive
Categories=Network;FileTransfer;
StartupNotify=false
DESKTOP
cat > "$pkg/DEBIAN/control" <<CONTROL
Package: bluedrive
Version: $debver
Section: net
Priority: optional
Architecture: amd64
Maintainer: BLUESTAR <support@bluestar.com.vn>
Depends: libc6, libicu74 | libicu72 | libicu70 | libicu67, libfontconfig1, libx11-6
Recommends: libnotify-bin
Description: BlueDrive Desktop
 Đồng bộ tư liệu BlueDrive (hệ thống lưu trữ sản phẩm truyền thông dân số) xuống máy tính:
 thư mục BlueDrive, biểu tượng khay hệ thống, chia sẻ, phiên bản, xử lý xung đột.
CONTROL
find "$pkg" -type d -exec chmod 755 {} +
deb="$out/bluedrive${suffix}_${debver}_amd64.deb"
dpkg-deb --root-owner-group --build "$pkg" "$deb" > /dev/null
echo "Xong:"
ls -la "$out"
