# Cùng soạn thảo bằng ONLYOFFICE Desktop Editors

Tài liệu Bước 0 của Sync-Phase 8b: tóm tắt tài liệu chính thức và mã nguồn ONLYOFFICE, cách BlueDrive tích hợp, và những gì còn lại.
Tham khảo ngày 03/11/2026 (tài liệu API Desktop Editors hiện hành; mã nguồn nhánh `master` của `ONLYOFFICE/desktop-apps`,
`ONLYOFFICE/desktop-sdk`).

## 1. Kết quả nghiên cứu

### 1.1. DMS provider (`providers/<id>/config.json`)
Nguồn: <https://api.onlyoffice.com/docs/desktop-editors/usage-api/adding-a-dms-provider/>

- Thư mục: Windows `%ProgramFiles%\ONLYOFFICE\DesktopEditors\providers\`, macOS `/Applications/ONLYOFFICE.app/Contents/Resources/providers/`,
  Linux `/opt/onlyoffice/desktopeditors/providers/`. Mã nguồn `desktop-sdk/ChromiumBasedEditors/lib/src/providers.h` còn đọc một thư mục
  "người dùng" (`m_sUserDirectory`), nhưng tài liệu không công bố vị trí → BlueDrive chỉ dùng thư mục chính thức (cần quyền quản trị).
- Trường: `provider` (mã dùng trong lệnh JS), `name`, `check.url` + `check.headers` (GET, mong 200 + JSON; bỏ qua khi có `entryPage`),
  `entryPage` (URL kết nối, thay địa chỉ người dùng nhập), `extraLogout` (URL đăng xuất), `editorPage` (đường dẫn tương đối hoặc `regex:…`
  đánh dấu trang là tài liệu), `startPage`, `icons.themeLight|themeDark.connectionsList` (36×36) và `.buttonLogo` (148×38), SVG khuyên dùng.

### 1.2. Lệnh JavaScript `window.AscDesktopEditor.execCommand(command, jsonString)`
Nguồn: trang `execCommand`, `login-and-logout`, `opening-documents`, `changing-a-theme` cùng mục.

| Lệnh | Tham số |
|---|---|
| `portal:login` | `displayName`*, `email`, `domain`*, `provider`*, `userId`*, `uiTheme` (`theme-light`, `theme-dark`, …; Windows/Linux) |
| `portal:logout` | `domain` (xóa cookie của miền) |
| `open:document` | `url` (tuyệt đối), `type` (`word`/`cell`/`slide`/`pdf`), `provider` — hoặc `window.open(url)` khớp `editorPage` |
| `portal:uitheme` | mã giao diện, ví dụ `"theme-dark"` |

Nhận biết đang chạy trong Desktop Editors: có `window.AscDesktopEditor` (tài liệu chỉ dẫn đối tượng này).

### 1.3. Mở một tài liệu từ bên ngoài
Tài liệu không mô tả cách chính thức. Đọc mã nguồn `desktop-apps/win-linux/src/cascapplicationmanagerwrapper.cpp` (`handleInputCmd`):
**`DesktopEditors.exe` nhận URL `http://`/`https://` (và scheme đăng ký `oo-office:`, xem `defines.h` `APP_PROTOCOL`) làm tham số dòng lệnh và
mở thành tab soạn thảo**; tham số `--view=`/`--review=` chỉ dành cho tệp trên máy. BlueDrive dùng cách này. Nếu một bản ONLYOFFICE sau này
bỏ hành vi đó, BlueDrive vẫn còn lối "Cùng soạn thảo trên trình duyệt".

### 1.4. Cài đặt im lặng
Bộ cài Windows chính thức (Inno Setup, `package/inno` trong `desktop-apps`): `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` (tham số chuẩn Inno);
bản MSI: `msiexec /i … /qn`. Phần tải, kiểm chữ ký và cài kèm thuộc Sync-Phase 9 (bộ cài BlueDrive Desktop).

## 2. Cách BlueDrive tích hợp

| Phần | Thực hiện |
|---|---|
| Kiểm tra provider | `GET /api/v1/onlyoffice-desktop/status` (ẩn danh) → `{ provider: "bluedrive", name, ok }`. |
| Gói provider | `desktop/onlyoffice-provider/bluedrive/` (`config.template.json`, 4 biểu tượng SVG). `entryPage` = `<máy chủ>/oo-desktop`, `extraLogout` = `<máy chủ>/oo-desktop/logout`, `editorPage` = `regex:/files/[0-9a-fA-F-]+/edit`. Sinh theo máy chủ bằng `desktop/onlyoffice-provider/build.ps1 -Server …` hoặc do BlueDrive Desktop (Cài đặt → Giới thiệu → "Cài BlueDrive vào ONLYOFFICE"; tự cài lại khi khởi động nếu ONLYOFFICE cập nhật làm mất và thư mục ghi được, nếu không thì thông báo). |
| Trang vào (`/oo-desktop`) | Đăng nhập BlueDrive như thường, rồi `portal:login` → BlueDrive hiện trong "Đám mây đã kết nối", chuyển tới Tài liệu của tôi. Trong Desktop Editors, thanh đầu trang web bị ẩn (`html.bd-oo-desktop`); mở tệp Office gửi `open:document` để thành tab soạn thảo của ứng dụng. |
| Đăng xuất (`/oo-desktop/logout`) | Hủy phiên web và `portal:logout`. |
| Không đăng nhập lại (SSO) | BlueDrive Desktop (đã có phiên thiết bị) gọi `POST /api/v1/onlyoffice-desktop/tickets {fileId}` → vé dùng một lần, 2 phút, lưu dạng băm (bảng mã OAuth, client `onlyoffice-desktop`), chỉ cấp khi người dùng xem được tệp. ONLYOFFICE mở `<máy chủ>/oo-desktop/open?ticket=…`; trang đổi vé (`POST …/tickets/redeem`, giới hạn tần suất) lấy phiên web, gọi `portal:login`, mở `/files/<id>/edit`. Nhật ký: đăng nhập `source = onlyoffice_desktop`, phiên "ONLYOFFICE Desktop Editors". |
| Quyền, phiên bản, lưu | Dùng nguyên tích hợp ONLYOFFICE web: cấu hình soạn thảo theo ACL (chỉ xem nếu không có quyền sửa), callback lưu tạo phiên bản mới; BlueDrive Desktop đồng bộ phiên bản đó xuống máy như mọi thay đổi khác. |
| Menu chuột phải | "Cùng soạn thảo bằng ONLYOFFICE" (mở `DesktopEditors.exe "<liên kết vé>"`; chưa có ONLYOFFICE thì mở trình duyệt) và "Cùng soạn thảo trên trình duyệt", chỉ với `.docx .xlsx .pptx .odt .ods .odp .pdf .doc .xls .ppt .rtf .csv`. |
| Giấy phép | ONLYOFFICE Desktop Editors © Ascensio System SIA, AGPL v3: BlueDrive không sửa ONLYOFFICE, chỉ thêm cấu hình provider; ghi nguồn và giấy phép ở Cài đặt → Giới thiệu. |

## 3. Kiểm thử đã có
- IT `DesktopClientTests.OnlyOffice_desktop_ticket_opens_the_editor_without_signing_in_again_once`: status ẩn danh, vé chỉ cho người xem được tệp,
  phiên đổi được mở cấu hình soạn thảo, vé dùng một lần.
- E2E `web/e2e/oo-desktop.spec.ts` (giả lập `window.AscDesktopEditor`): mở bằng vé trong hồ sơ trình duyệt chưa đăng nhập → tới trang soạn thảo,
  `portal:login` đúng provider/người dùng, giao diện gọn; trang vào gọi `portal:login`.
- `DesktopTests.OnlyOffice_provider_is_generated_…`: sinh `config.json` theo máy chủ, trạng thái Missing → Ready → Outdated, cài vào thư mục ONLYOFFICE.

## 4. Còn lại (chưa làm)
- Kiểm thử thật với ONLYOFFICE Desktop Editors trên 2 máy Windows + 1 người trên web (máy phát triển chưa cài ONLYOFFICE).
- Cài kèm ONLYOFFICE trong bộ cài (tùy chọn mặc định bật, kiểm chữ ký, cài im lặng, gỡ provider khi gỡ BlueDrive) — Sync-Phase 9.
- Tùy chọn "nhấp đúp tệp Office → luôn mở chế độ cùng soạn", hiện "ai đang chỉnh sửa" trong flyout/Explorer và cảnh báo khóa mềm khi mở bản
  trên máy bằng Word trong lúc có phiên cùng soạn: cần máy chủ theo dõi người đang mở tài liệu (callback trạng thái 1 của Document Server)
  và cột trạng thái tùy chỉnh của Explorer (cần gói MSIX) — chưa làm.
