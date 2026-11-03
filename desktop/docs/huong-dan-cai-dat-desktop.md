# Hướng dẫn cài đặt BlueDrive Desktop

Dành cho người dùng (cài đơn lẻ) và bộ phận CNTT (cài hàng loạt, cấu hình máy chủ). Cách dùng ứng dụng: `huong-dan-su-dung-desktop.md`.

## 1. Yêu cầu

| Mục | Yêu cầu |
|---|---|
| Hệ điều hành | Windows 10 phiên bản 1809 (build 17763) trở lên, 64-bit (x64). Tệp theo yêu cầu cần ổ NTFS. |
| Quyền | Không cần quyền quản trị (cài theo người dùng vào `%LOCALAPPDATA%\BlueDrive.Desktop`). Cài ONLYOFFICE Desktop Editors cần quyền quản trị (một lần xác nhận UAC). |
| Dung lượng | Ứng dụng ~ 200 MB; dữ liệu đồng bộ tùy chế độ (tệp theo yêu cầu chỉ chiếm chỗ khi mở tệp). |
| Mạng | HTTPS (443) tới máy chủ BlueDrive, gồm WebSocket `/hubs/sync` (thông báo thay đổi tức thì; không có thì ứng dụng tự hỏi định kỳ). |

## 2. Cài đơn lẻ

**macOS** (Apple silicon, macOS 14 Sonoma trở lên — yêu cầu của .NET 10): tải **macOS (Apple silicon)** trên trang Tải ứng dụng máy tính, mở `BlueDrive.pkg` và làm theo
trình cài. BlueDrive ở thanh menu (góc trên bên phải). Bản chưa notarize: lần đầu chuột phải ứng dụng → **Mở**. Trên macOS BlueDrive tải toàn bộ
tệp của các thư mục đã chọn (chưa có chế độ tệp theo yêu cầu); mã đăng nhập được giữ trong Keychain. Triển khai hàng loạt (MDM): đặt
`/Library/Application Support/BlueDrive/deploy.json` với `{ "server": "https://…", "channel": "stable" }`.

**Linux** (x64; Ubuntu 22.04+/Debian 12+ hoặc tương đương): **Linux (.deb)** → `sudo apt install ./bluedrive_<phiên bản>_amd64.deb`
(lệnh `bluedrive`, có trong menu ứng dụng; cập nhật bằng gói mới), hoặc **Linux (AppImage)** → `chmod +x BlueDrive-x86_64.AppImage` rồi chạy (tự
cập nhật). Biểu tượng khay cần môi trường hỗ trợ AppIndicator (GNOME: cài tiện ích "AppIndicator and KStatusNotifierItem Support"). Triển khai
hàng loạt: `/etc/bluedrive/deploy.json`.

**Windows:**

1. Đăng nhập BlueDrive trên web → menu tài khoản (góc phải) → **Tải ứng dụng máy tính** → **Tải BlueDrive … cho Windows**.
2. Chạy `BlueDriveSetup.exe`. Bộ cài chạy ngay, không hỏi gì; BlueDrive mở và hiện trình hướng dẫn: nhập địa chỉ máy chủ (nếu chưa có sẵn), đăng nhập
   trên trình duyệt, chọn thư mục đồng bộ, chọn thư mục muốn có trên máy, tùy chọn **Cài ONLYOFFICE Desktop Editors để cùng soạn thảo tài liệu**
   (mặc định bật).
3. Biểu tượng BlueDrive ở khay hệ thống; thư mục BlueDrive trong Explorer.

Kiểm tra bộ cài: trang **Tải ứng dụng máy tính** hiện mã SHA-256; trong PowerShell: `Get-FileHash .\BlueDriveSetup.exe` và
`Get-AuthenticodeSignature .\BlueDriveSetup.exe` (bản phát hành chính thức được ký số).

## 3. Cài hàng loạt (GPO, Intune, SCCM)

BlueDrive cài **theo người dùng**: chạy lệnh cài trong phiên của người dùng (script đăng nhập GPO, Intune "Install behavior: User"), không chạy
dưới tài khoản SYSTEM.

### 3.1. Script cài đặt

`desktop/packaging/install-bluedrive.ps1` (tương đương `BlueDriveSetup.exe /silent /server=… /with-onlyoffice=1` của đặc tả):

```powershell
powershell -ExecutionPolicy Bypass -File install-bluedrive.ps1 -Server https://bluedrive.bluestar.com.vn -WithOnlyOffice 1
# Bộ cài để trên máy chủ tệp nội bộ, không cài ONLYOFFICE, kênh beta, tắt tự cập nhật:
powershell -ExecutionPolicy Bypass -File install-bluedrive.ps1 -Server https://drive.example.gov.vn -Setup \\fs\soft\BlueDriveSetup.exe -WithOnlyOffice 0 -Channel beta -DisableUpdates
```

Script ghi chính sách triển khai (mục 3.2), tải bộ cài từ máy chủ BlueDrive (hoặc lấy từ `-Setup`), kiểm tra chữ ký số và chạy
`BlueDriveSetup.exe --silent`. BlueDrive tự chạy ở lần đăng nhập Windows tiếp theo và mở trình hướng dẫn với máy chủ đã đặt sẵn.

> Velopack 1.2.161 (bộ cài) không chuyển tiếp được tham số tùy biến cho ứng dụng (`--server=` sau `--` làm bộ cài dừng lỗi), nên địa chỉ máy chủ
> được truyền qua chính sách registry hoặc `deploy.json` trong gói (mục 3.3).

### 3.2. Chính sách (registry) — GPO Preferences / Intune

Khóa `HKLM\SOFTWARE\Policies\BlueDrive` (áp cho mọi người dùng; `HKCU\…` cho một người dùng):

| Giá trị | Kiểu | Ý nghĩa |
|---|---|---|
| `Server` | REG_SZ | Địa chỉ máy chủ BlueDrive, ví dụ `https://bluedrive.bluestar.com.vn` (trình hướng dẫn khóa sẵn ô máy chủ). |
| `InstallOnlyOffice` | REG_DWORD | `1` (mặc định): tự cài ONLYOFFICE Desktop Editors sau khi đăng nhập; `0`: không cài. |
| `UpdateChannel` | REG_SZ | `stable` (mặc định) hoặc `beta`. |
| `DisableUpdates` | REG_DWORD | `1`: không tự cập nhật (CNTT tự phân phối phiên bản mới). |

```cmd
reg add HKLM\SOFTWARE\Policies\BlueDrive /v Server /t REG_SZ /d https://bluedrive.bluestar.com.vn /f
reg add HKLM\SOFTWARE\Policies\BlueDrive /v InstallOnlyOffice /t REG_DWORD /d 1 /f
```

### 3.3. Gói có sẵn máy chủ

Bản phát hành có thể đóng gói sẵn địa chỉ máy chủ (`deploy.json` cạnh `BlueDrive.exe`):
`pwsh -File desktop/packaging/build.ps1 -Version 1.2.0 -Server https://bluedrive.bluestar.com.vn`.
Thứ tự ưu tiên: chính sách registry → tham số dòng lệnh → `deploy.json`.

### 3.4. MSIX (Intune, App Installer)

`BlueDrive.msix` (cùng nội dung): `Add-AppxPackage BlueDrive.msix` hoặc giao cho Intune dạng "Line-of-business app". Chứng thư ký phải được tin
cậy trên máy (Intune: triển khai chứng thư vào "Trusted People"). Bản MSIX cập nhật qua Intune / App Installer (không qua Velopack); khởi động cùng
Windows qua StartupTask của gói (Cài đặt → Ứng dụng → Khởi động). Trước khi gỡ bản MSIX, dùng **Hủy liên kết máy này** trong Cài đặt để gỡ thư mục
khỏi Explorer.

### 3.5. ONLYOFFICE Desktop Editors

- BlueDrive tải bộ cài chính thức (x64/ARM64) từ `download.onlyoffice.com`, kiểm tra chữ ký số của Ascensio System SIA, cài im lặng
  (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`) và chép provider "bluedrive" vào `%ProgramFiles%\ONLYOFFICE\DesktopEditors\providers\` trong cùng
  một lần nâng quyền.
- Máy không ra Internet: chép bộ cài vào máy chủ tệp và thêm `--onlyoffice-installer=\\fs\soft\DesktopEditors_x64.exe` vào lối tắt khởi động,
  hoặc cài sẵn ONLYOFFICE bằng gói MSI chính thức (`msiexec /i DesktopEditors_x64.msi /qn`) — BlueDrive chỉ còn thêm provider.
- Đã có ONLYOFFICE cũ hơn 8.0: BlueDrive báo "nên cập nhật" (Cài đặt → Giới thiệu → **Cài / cập nhật ONLYOFFICE Desktop Editors**).

## 4. Máy chủ: phát hành và tải về

| Bước | Thực hiện |
|---|---|
| Phát hành | Đẩy tag `desktop-v1.2.0` (ổn định) hoặc `desktop-v1.3.0-beta.1` (beta) lên GitHub → workflow `Desktop release` (máy Windows): kiểm thử, biên dịch, ký số, đóng gói Velopack + MSIX, tạo GitHub Release. |
| Ký số | Secret `BLUEDRIVE_SIGN_PFX_BASE64` (chứng thư .pfx dạng base64) và `BLUEDRIVE_SIGN_PASSWORD`; không có thì dùng chứng thư tự ký (chỉ để thử, SmartScreen cảnh báo). Ký thủ công: `desktop/packaging/sign.ps1`. |
| Đưa lên máy chủ | Mỗi lần deploy, `scripts/sync-desktop-downloads.sh` lấy bản ổn định / beta mới nhất từ GitHub Release vào `deploy/downloads/desktop/` (biến `DESKTOP_DOWNLOADS_DIR`), nginx phục vụ tại `/downloads/desktop/`. Chạy tay: `sh scripts/sync-desktop-downloads.sh`. |
| Tự cập nhật | Ứng dụng kiểm tra `/downloads/desktop/releases.<kênh>.json` lúc khởi động và mỗi 6 giờ, tải nền, cài khi người dùng thoát hoặc chọn **Khởi động lại để cập nhật** (khay hệ thống, Cài đặt → Giới thiệu). |
| Trang tải | Web: menu tài khoản → **Tải ứng dụng máy tính** (đọc `/downloads/desktop/releases.json`). |

Build cục bộ: `pwsh -File desktop/packaging/build.ps1 -Version 1.0.0 -Channel stable -Msix` (cần .NET 10 SDK, `dotnet tool install -g vpk --version 1.2.161`, Windows SDK).

## 5. Tường lửa, proxy

- Cho phép HTTPS tới máy chủ BlueDrive, kể cả nâng cấp WebSocket tại `/hubs/sync` (proxy chặn WebSocket: ứng dụng tự chuyển sang hỏi định kỳ,
  thay đổi trên web đến chậm hơn).
- Proxy: theo cấu hình Windows (mặc định), hoặc Cài đặt → Mạng → proxy tự nhập (`http://proxy:8080`), có hỗ trợ xác thực Windows.
- Cài ONLYOFFICE cần truy cập `download.onlyoffice.com` (hoặc dùng bộ cài nội bộ, mục 3.5).
- Đăng nhập dùng trình duyệt mặc định và một cổng ngẫu nhiên trên `127.0.0.1` (loopback, không mở ra mạng).

## 6. Gỡ cài đặt

Cài đặt Windows → Ứng dụng → **BlueDrive** → Gỡ cài đặt (hoặc `%LOCALAPPDATA%\BlueDrive.Desktop\Update.exe --uninstall`). BlueDrive:

1. Hỏi **giữ lại các tệp đã tải về trong thư mục BlueDrive?** (mặc định giữ; tự chọn sau 12 giây khi gỡ im lặng). Tệp chỉ có trên mạng
   (tệp giữ chỗ chưa tải) được gỡ khỏi máy; mọi tệp vẫn còn trên BlueDrive.
2. Gỡ thư mục khỏi Explorer (sync root), menu chuột phải, khởi động cùng Windows, provider "bluedrive" trong ONLYOFFICE.
3. Nếu BlueDrive đã cài ONLYOFFICE: hỏi có gỡ luôn ONLYOFFICE Desktop Editors không (mặc định giữ lại).
4. Xóa mã đăng nhập thiết bị và dữ liệu trạng thái (giữ nhật ký trong `%LOCALAPPDATA%\BlueDrive\logs`). Thiết bị vẫn hiện trong "Thiết bị đã kết
   nối" trên web cho tới khi người dùng hoặc quản trị thu hồi.
