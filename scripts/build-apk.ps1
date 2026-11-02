<#
.SYNOPSIS
  Build APK Android của ứng dụng BlueDrive trong container Linux (không cần cài Android SDK/JDK trên máy, tránh giới hạn
  đường dẫn 260 ký tự của Windows). Kết quả: mobile/build-output/bluedrive-<version>.apk
.EXAMPLE
  pwsh -File scripts/build-apk.ps1                 # arm64-v8a (đa số điện thoại hiện nay)
  pwsh -File scripts/build-apk.ps1 -AllAbis        # arm64-v8a, armeabi-v7a, x86_64
  pwsh -File scripts/build-apk.ps1 -Abis x86_64    # chỉ x86_64 (chạy thử trên Android Emulator), ít tốn RAM hơn
  pwsh -File scripts/build-apk.ps1 -DefaultServer https://bluedrive.bluestar.com.vn   # điền sẵn địa chỉ máy chủ ở màn hình đăng nhập
#>
param([switch]$AllAbis, [string]$Abis, [string]$DefaultServer = '', [string]$Image = 'reactnativecommunity/react-native-android:v22.0')
$ErrorActionPreference = 'Stop'
$mobile = Join-Path (Split-Path -Parent $PSScriptRoot) 'mobile'
$out = Join-Path $mobile 'build-output'
New-Item -ItemType Directory -Force $out | Out-Null
$abis = if ($Abis) { $Abis } elseif ($AllAbis) { 'arm64-v8a,armeabi-v7a,x86_64' } else { 'arm64-v8a' }
$version = (Get-Content (Join-Path $mobile 'app.json') -Raw | ConvertFrom-Json).expo.version
if ($Abis) { $version = "$version-$($Abis -replace ',', '-')" }
$script = @"
set -e
mkdir -p /app && tar -C /src --exclude=./node_modules --exclude=./android --exclude=./ios --exclude=./.expo --exclude=./build-output -cf - . | tar -C /app -xf -
cd /app && npm ci --no-audit --no-fund
npx expo prebuild -p android --no-install
cd android && ./gradlew assembleRelease --no-daemon -Dorg.gradle.workers.max=2 -PreactNativeArchitectures=$abis
cp app/build/outputs/apk/release/app-release.apk /out/bluedrive-$version.apk
"@
docker run --rm -e "EXPO_PUBLIC_DEFAULT_SERVER_URL=$DefaultServer" -v "${mobile}:/src:ro" -v "${out}:/out" -v bluedrive_gradle_cache:/root/.gradle $Image bash -lc $script
if ($LASTEXITCODE -ne 0) { throw 'Build APK thất bại.' }
Write-Host "APK: $(Join-Path $out "bluedrive-$version.apk")" -ForegroundColor Green
