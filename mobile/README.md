# BlueDrive Mobile (iOS/Android)

Ứng dụng di động của hệ thống lưu trữ tư liệu số **BlueDrive** (HSMT #71–#84), viết bằng React Native + Expo (TypeScript), dùng chung API với ứng dụng web.

## 1. Yêu cầu

- Node.js 20+ (đã thử với Node 22 / npm 10).
- Điện thoại Android/iOS có ứng dụng **Expo Go** (chạy thử nhanh) hoặc bản *development build* (khuyến nghị).
- Tài khoản [expo.dev](https://expo.dev) nếu build bằng EAS (dịch vụ build đám mây của Expo).

## 2. Chạy thử khi phát triển

```bash
cd mobile
npm install
npx expo start
```

- Quét mã QR bằng Expo Go (Android) hoặc ứng dụng Camera (iOS).
- Nhấn `a` để mở trên Android Emulator, `i` cho iOS Simulator (chỉ trên macOS).
- Một số tính năng gốc (mở khóa sinh trắc học Face ID, tải lên nền iOS, lưu vào Thư viện ảnh trên Android 13+) nên kiểm thử trên *development build*:

```bash
npx eas-cli@latest build -p android --profile development   # cài APK dev lên máy
npx expo start --dev-client
```

Kiểm tra mã nguồn:

```bash
npm run typecheck      # npx tsc --noEmit
npm run lint           # npx expo lint
npx expo-doctor        # kiểm tra cấu hình / phiên bản thư viện
npx expo export --platform android   # thử đóng gói JS
```

## 3. Cấu hình máy chủ

Không cần sửa mã nguồn. Ở màn hình **Đăng nhập**, nhập:

- **Địa chỉ máy chủ**: địa chỉ hệ thống BlueDrive của đơn vị, ví dụ `https://drive.example.gov.vn` (có thể bỏ `https://`, ứng dụng tự thêm). Địa chỉ được ghi nhớ cho lần sau.
- **Tên đăng nhập / Mật khẩu**: tài khoản BlueDrive.

Lưu ý:

- Ứng dụng gọi API tại `<địa chỉ máy chủ>/api/v1/...` và gửi `User-Agent: BlueDriveMobile/<phiên bản> (<nền tảng>)` để máy chủ ghi nhận thiết bị là "Ứng dụng BlueDrive trên Android/iOS" trong danh sách phiên đăng nhập.
- Token được lưu trong **SecureStore** (Keychain/Keystore). Access token được tự làm mới (một yêu cầu refresh duy nhất tại một thời điểm, refresh token xoay vòng).
- Nếu tài khoản bị buộc đổi mật khẩu (lần đầu hoặc hết hạn), ứng dụng tự chuyển sang màn hình **Đổi mật khẩu**.
- Môi trường nội bộ dùng `http://` vẫn chạy được (đã bật `usesCleartextTraffic` trên Android và `NSAllowsArbitraryLoads` trên iOS). Khi triển khai chính thức nên dùng HTTPS và có thể tắt hai tùy chọn này trong `app.json`.
- Trình xem Office/PDF dùng ONLYOFFICE Document Server mà máy chủ BlueDrive cấu hình (`OnlyOffice:PublicUrl`); điện thoại phải truy cập được địa chỉ đó.
- Đổi máy chủ: **Cài đặt → Đăng xuất**, rồi nhập địa chỉ mới ở màn hình đăng nhập.

## 4. Build APK / AAB / iOS bằng EAS

Lần đầu:

```bash
npx eas-cli@latest login
npx eas-cli@latest init        # gắn dự án với tài khoản Expo (ghi projectId vào app.json)
```

| Mục đích | Lệnh | Kết quả |
|---|---|---|
| APK cài trực tiếp (thử nghiệm nội bộ) | `npx eas-cli@latest build -p android --profile preview` | tệp `.apk` |
| Phát hành Google Play | `npx eas-cli@latest build -p android --profile production` | tệp `.aab` |
| iOS (TestFlight/App Store) | `npx eas-cli@latest build -p ios --profile production` | tệp `.ipa` (cần tài khoản Apple Developer) |
| Bản phát triển | `npx eas-cli@latest build -p android --profile development` | APK có dev client |

Các script tắt tương ứng: `npm run build:apk`, `npm run build:aab`, `npm run build:ios`. Các profile nằm trong `eas.json`. Tăng `version` (và `android.versionCode`, `ios.buildNumber`) trong `app.json` trước mỗi lần phát hành.

## 5. Build APK bằng Docker (khuyến nghị, không cần Android SDK)

```powershell
pwsh -File scripts/build-apk.ps1            # → mobile/build-output/bluedrive-<version>.apk (arm64-v8a)
pwsh -File scripts/build-apk.ps1 -AllAbis   # thêm armeabi-v7a, x86_64
pwsh -File scripts/build-apk.ps1 -Abis x86_64   # → bluedrive-<version>-x86_64.apk, chỉ để chạy trên Android Emulator
```

Cần Docker có ≥ 8 GB RAM trống (dừng bớt dịch vụ khác nếu cần).

### Chạy thử APK trên Android Emulator

- Emulator x86_64 cần APK có `x86_64` (bản mặc định arm64 sẽ thoát ngay với lỗi `libreactnative.so`).
- `adb install -r mobile/build-output/bluedrive-<version>-x86_64.apk`, địa chỉ máy chủ trong app: `http://10.0.2.2:<HTTP_PORT>`.
- Xem PDF/Office: ONLYOFFICE được nạp từ `PUBLIC_BASE_URL` (ví dụ `http://localhost:8088`), nên trên emulator chạy thêm `adb reverse tcp:8088 tcp:8088` để `localhost` của emulator trỏ về máy host. Máy chủ thật dùng domain nên không cần bước này.
- Tự động nhập liệu bằng `adb shell input text`: tắt bàn phím Gboard/nhập giọng nói (`adb shell ime disable <id>`) để tránh tự sửa chính tả.

## 6. Build Android cục bộ (không dùng EAS)

Cần Android Studio (Android SDK + JDK 17).

```bash
cd mobile
npx expo prebuild -p android      # sinh thư mục android/ từ app.json
cd android
./gradlew assembleRelease         # Windows PowerShell: .\gradlew.bat assembleRelease
# APK: android/app/build/outputs/apk/release/app-release.apk
./gradlew bundleRelease           # AAB: android/app/build/outputs/bundle/release/app-release.aab
```

**Trên Windows**: dùng JDK 17+ (ví dụ JDK đi kèm Android Studio: `$env:JAVA_HOME='C:\Program Files\Android\Android Studio\jbr'`). Nếu gặp lỗi `ninja: error: mkdir(... ) No such file or directory` do đường dẫn quá dài (giới hạn 260 ký tự của CMake), sao chép mã nguồn app sang một thư mục ngắn rồi build từ đó (không dùng `subst`/junction vì Node phân giải về đường dẫn thật, gây lỗi "different roots"):

```powershell
robocopy D:\duong\dan\BlueDrive\mobile C:\bdm /MIR /XD node_modules android .expo
cd C:\bdm; npm ci; npx expo prebuild -p android --no-install
cd android; .\gradlew.bat assembleRelease -PreactNativeArchitectures=arm64-v8a
```

Mặc định bản release cục bộ được ký bằng khóa debug; để phát hành cần cấu hình keystore riêng trong `android/app/build.gradle` (hoặc dùng EAS để quản lý khóa). Thư mục `android/`, `ios/` được sinh tự động, không sửa tay (đã có trong `.gitignore`).

## 6. Biểu tượng & màn hình chờ

`assets/icon.png`, `android-icon-*.png`, `splash-icon.png`, `favicon.png` được sinh bằng `npm run icons` (`scripts/generate-icons.js`: nền `#0b5394`, chữ "B" trắng). Có thể thay bằng logo chính thức của đơn vị (giữ nguyên kích thước 1024×1024).

## 7. Cấu trúc mã nguồn

```
mobile/
├─ app.json, eas.json, tsconfig.json, eslint.config.js
├─ assets/                     # icon, splash
├─ scripts/generate-icons.js
└─ src/
   ├─ app/                     # màn hình (Expo Router)
   │  ├─ _layout.tsx           # provider, bảo vệ route theo trạng thái đăng nhập, khóa sinh trắc học
   │  ├─ login.tsx, change-password.tsx
   │  ├─ (tabs)/               # Trang chủ, Tài liệu (#71), Chia sẻ (#72), Nhóm (#74), Thêm
   │  ├─ folder/[workspaceId].tsx   # duyệt thư mục (#71/#74/#82)
   │  ├─ tree/[workspaceId].tsx     # cây thư mục (#82)
   │  ├─ viewer/[id].tsx       # mở tài liệu (#76)
   │  ├─ file/[id].tsx         # chi tiết tệp (#83)
   │  ├─ recent.tsx (#73), search.tsx (#75), starred.tsx (#78), settings.tsx
   ├─ api/                     # client fetch (token, refresh, lỗi ProblemDetails), endpoint, kiểu dữ liệu
   ├─ components/              # danh sách, dòng mục, cây thư mục, picker thư mục, hộp thoại, trình xem
   ├─ features/                # thao tác mục (#76–#81, #53, #83, #84), tải lên, tải xuống, khóa sinh trắc
   ├─ hooks/, store/ (zustand), theme/ (chế độ sáng/tối), utils/
   └─ i18n/vi.ts               # toàn bộ chuỗi giao diện tiếng Việt, dùng qua t('khoa')
```

## 8. Chức năng theo HSMT

| Mã | Chức năng | Vị trí |
|---|---|---|
| #71 | Tài liệu của tôi | tab **Tài liệu** |
| #72 | Được chia sẻ với tôi | tab **Chia sẻ** |
| #73 | Gần đây | Trang chủ + **Thêm → Gần đây** |
| #74 | Nhóm tài nguyên / đơn vị | tab **Nhóm** |
| #75 | Tìm kiếm (có dấu/không dấu, tô đậm từ khóa) | biểu tượng kính lúp / **Thêm → Tìm kiếm** |
| #76 | Mở tài liệu: ảnh (phóng to), video, âm thanh, văn bản, PDF/Office (ONLYOFFICE) | chạm vào tệp / menu **Mở** |
| #77 | Đổi tên | menu ⋯ / nhấn giữ |
| #78 | Đánh dấu quan trọng | menu ⋯; danh sách tại **Thêm → Quan trọng** |
| #79 | Sao chép tới (chọn không gian + thư mục) | menu ⋯ |
| #80 | Di chuyển tới | menu ⋯ |
| #81 | Xóa (vào thùng rác, có xác nhận) | menu ⋯ |
| #82 | Cây thư mục + đường dẫn (breadcrumb) | thanh công cụ thư mục, picker |
| #83 | Chi tiết tệp + xem trước | menu ⋯ → **Chi tiết**, nút ⓘ trong trình xem |
| #84 | Tải xuống thiết bị (ảnh/video → Thư viện ảnh; tệp khác → Lưu vào Tệp/chia sẻ) | menu ⋯ → **Tải xuống** |
| #53 | Chia sẻ liên kết (tùy chọn tải xuống, mật khẩu, thời hạn) | menu ⋯ → **Chia sẻ liên kết** |
| — | Tải lên từ Thư viện / Máy ảnh / Tệp (tối đa 512 MB/tệp), tạo thư mục | nút **+** trong thư mục |
