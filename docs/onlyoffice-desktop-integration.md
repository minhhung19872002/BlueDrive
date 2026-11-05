# Cùng soạn thảo bằng ONLYOFFICE Desktop Editors

Tài liệu Bước 0 của Sync-Phase 8b: tóm tắt tài liệu chính thức và mã nguồn ONLYOFFICE, cách BlueDrive tích hợp, và những gì còn lại.
Tham khảo ngày 03/11/2026 (tài liệu API Desktop Editors hiện hành; mã nguồn nhánh `master` của `ONLYOFFICE/desktop-apps`,
`ONLYOFFICE/desktop-sdk`). **Phiên bản đã kiểm chứng: ONLYOFFICE Desktop Editors 10.0.0.169 (Windows x64)**, cài bằng chính BlueDrive Desktop
(Sync-Phase 9): tải bộ cài chính thức, kiểm chữ ký Ascensio System SIA, cài im lặng, chép provider; mở tài liệu bằng liên kết vé →
tab soạn thảo, chế độ chỉnh sửa, đã đăng nhập (ảnh `desktop/docs/images/onlyoffice-desktop-coedit.png`).

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
Bộ cài Windows chính thức (Inno Setup, `package/inno` trong `desktop-apps`): `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` (tham số chuẩn Inno, đã chạy thử với 10.0.0.169);
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

## 4. Kiểm thử trên máy thật và phần làm thêm
- 3 người cùng sửa 1 tệp (web + ONLYOFFICE Desktop Editors trên 2 máy Windows 11, máy chủ thật, 04/11/2026): Đạt — xem
  `docs/kich-ban-kiem-thu-desktop.md`, phần "Kết quả chạy trên 2 máy Windows".
- Mất mạng giữa phiên (04/11/2026, ONLYOFFICE Desktop 10.0 trên Windows 11, máy chủ thật; mạng của ONLYOFFICE đi qua một proxy cục bộ được
  tắt/bật giữa phiên): gõ một dòng → cắt mạng → ONLYOFFICE báo "Connection is lost — Trying to connect" và khóa sửa (gõ thêm không vào
  tài liệu) → Document Server lưu ngay phiên bản 6 có dòng đã gõ → có mạng lại: ONLYOFFICE báo "Đã thay đổi phiên bản… sẽ được tải lại",
  mở lại thấy đủ nội dung, gõ tiếp, đóng → phiên bản 7 có cả dòng trước khi mất mạng và dòng sau khi có mạng. Không mất nội dung.
- Người chỉ có quyền xem mở ở chế độ chỉ xem: vé `POST /api/v1/onlyoffice-desktop/tickets` trả `canEdit = false`, cấu hình soạn thảo
  `mode = view` (IT `DesktopClientTests.OnlyOffice_desktop_ticket_opens_the_editor_without_signing_in_again_once`).
- Cài kèm ONLYOFFICE: đã làm ở Sync-Phase 9 (`OnlyOfficeSetup`, tùy chọn ở trình hướng dẫn mặc định bật, chính sách `InstallOnlyOffice`,
  gỡ provider và hỏi gỡ ONLYOFFICE khi gỡ BlueDrive).
- Đã làm sau đó: hiện "ai đang chỉnh sửa" trong flyout/Explorer (`OfficeEditingTracker`, `GET /api/v1/sync/editing`), cảnh báo khóa mềm khi
  mở bản trên máy bằng Word trong lúc có phiên cùng soạn, và tùy chọn **Cài đặt → Đồng bộ → "Khi nhấp đúp tệp Office trong BlueDrive"**
  (`OfficeDoubleClick`): Windows không có liên kết tệp theo thư mục, nên BlueDrive thêm verb "Mở bằng BlueDrive" có `AppliesTo` = các thư mục
  BlueDrive vào ProgID mà loại tệp đang mở bằng (Word.Document.12, ASC.Docx.12…, trong HKCU) và đặt lên đầu thứ tự verb. Trong thư mục
  BlueDrive nhấp đúp chạy `BlueDrive.exe --open`: có mạng → phiên cùng soạn (ONLYOFFICE Desktop Editors, không có thì trình duyệt); mất mạng /
  máy chủ lỗi / tệp chưa lên máy chủ → mở bản trên máy bằng verb "open" của ứng dụng mặc định. Ngoài thư mục BlueDrive verb không áp dụng,
  ứng dụng mặc định giữ nguyên. Tắt tùy chọn (hoặc gỡ BlueDrive) trả lại đúng thứ tự verb cũ (`office-open.json`). Đã chạy thử trên Windows 11:
  tệp .docx trong BlueDrive mở trong ONLYOFFICE 10.0 ở chế độ cùng soạn, khi máy chủ không truy cập được thì mở bằng Word; tệp .docx ngoài
  BlueDrive vẫn mở bằng Word.
