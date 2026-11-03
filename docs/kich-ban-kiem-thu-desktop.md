# Kịch bản kiểm thử BlueDrive Desktop

Kịch bản thủ công / E2E theo mục 12.4 của đặc tả đồng bộ desktop. Mỗi kịch bản có bước làm, kết quả mong đợi, kiểm thử tự động tương ứng
(chạy trong `scripts/ci.ps1` / GitHub Actions) và lần kiểm chứng trên máy thật.

Ký hiệu kết quả: **Đạt** — đã chạy và đúng; **Tự động** — có kiểm thử tự động trên API thật hoặc đĩa thật, chưa chạy tay trên 2 máy vật lý.

Chuẩn bị: máy chủ BlueDrive (stack Docker hoặc https://bluedrive.bluestar.com.vn), 2 máy Windows 11 cài BlueDrive Desktop (máy A, máy B) cùng
tài khoản `bien_tap`, trình duyệt đăng nhập tài khoản đó; tài khoản thứ hai để chia sẻ.

| # | Kịch bản | Bước | Kết quả mong đợi | Kiểm thử tự động | Kết quả |
|---|---|---|---|---|---|
| 1 | Sửa Word đồng thời từ 2 máy | Mở cùng một tệp `.docx` trên A và B (tệp trên máy), sửa khác nhau, lưu gần như cùng lúc | Một bản lên máy chủ thành phiên bản mới; máy còn lại nhận bản xung đột `… (Bản xung đột - <Họ tên> - <Máy> - <ngày giờ>).docx`; không mất nội dung; Cài đặt → Vấn đề có mục xung đột | `EngineScenarioTests.Concurrent_edits_keep_both_versions_with_a_conflict_copy`; IT `DesktopClientTests.Two_computers_sync_create_edit_rename_conflict_and_delete_through_the_real_api`; FsCheck 10 000 chuỗi | Tự động |
| 1b | Cùng soạn bằng ONLYOFFICE | Chuột phải tệp → BlueDrive → Cùng soạn thảo bằng ONLYOFFICE | ONLYOFFICE Desktop Editors mở tab soạn thảo, đã đăng nhập, chế độ chỉnh sửa | IT `OnlyOffice_desktop_ticket_…`; E2E `oo-desktop.spec.ts` | Đạt (ONLYOFFICE 10.0.0.169, 03/11/2026) |
| 2 | Đổi tên thư mục lớn | Đổi tên thư mục có 1 000 tệp trên máy A | Trên web và máy B chỉ là một lần đổi tên (không tải lại nội dung), dưới 10 giây | `EngineScenarioTests.Local_creates_and_renames_reach_the_other_computer_without_reuploading`; `PhysicalDiskTests.Scan_reports_relative_paths_and_a_file_id_that_survives_rename_and_move` | Tự động |
| 3 | Di chuyển thư mục giữa nhóm | Kéo một thư mục từ `Nhóm\A` sang `Nhóm\B` | Thư mục chuyển nhóm trên máy chủ (cùng mục, đổi vị trí); không có quyền ở nhóm đích → thư mục quay về chỗ cũ và có thông báo | `EngineScenarioTests.Remote_rename_and_move_are_applied_locally`; IT `SyncTests.Batch_creates_trees_and_reports_conflicts_per_operation` | Tự động |
| 4 | Xóa 500 tệp | Chọn 500 tệp trong thư mục BlueDrive → Delete | BlueDrive tạm dừng xóa trên máy chủ và hỏi "Bạn vừa xóa 500 tệp. Xóa cả trên BlueDrive?"; Không → tệp được khôi phục; Có → vào thùng rác máy chủ | `EngineScenarioTests.Mass_delete_waits_for_the_user` | Tự động |
| 5 | Rút mạng giữa lúc tải lên video 2 GB | Chép video 2 GB vào thư mục, rút mạng ở ~50%, cắm lại (hoặc tắt máy rồi bật) | Tiếp tục từ vị trí đã gửi (tus), mã băm khớp, không tạo bản trùng | IT `DesktopClientTests.An_interrupted_upload_continues_after_a_restart_without_sending_the_start_again`; `LargeFileTests`; `bao-cao-hieu-nang.md` | Tự động |
| 6 | Hết dung lượng | Đặt hạn mức nhỏ cho người dùng, chép tệp vượt hạn mức | Thông báo "Hết dung lượng BlueDrive"; tệp ở Cài đặt → Vấn đề; thay đổi giữ trên máy, tự tải lên khi có chỗ | IT `DesktopClientTests.Out_of_quota_keeps_the_file_on_the_computer_and_uploads_it_once_there_is_space` | Tự động |
| 7 | Thu hồi quyền khi đang mở tệp | Người chia sẻ hủy chia sẻ trong lúc người nhận đang mở tệp | Sau khi đóng tệp, mục được gỡ khỏi máy (tệp đã sửa chưa lên được chuyển vào `_Không đồng bộ được`), nhật ký "access_revoked" | IT `SyncTests.Shared_root_lists_shares_flags_view_only_and_reports_access_revoked`; `EngineScenarioTests.Edits_without_edit_right_are_kept_aside_and_the_server_copy_restored` | Tự động |
| 8 | Xóa từ xa (remote wipe) | Quản trị → Thiết bị → Xóa dữ liệu từ xa máy A | Máy A dừng đồng bộ, xóa thư mục BlueDrive và mã đăng nhập (lệnh có chữ ký HMAC), xác nhận về máy chủ; nhật ký có `deviceId` | IT `SyncAgentTests.Revoked_device_stops_and_a_signed_remote_wipe_deletes_the_local_copy`, `SyncTests.Remote_wipe_is_signed_with_the_device_secret_and_ends_the_device` | Tự động |
| 9 | Cài lại máy, trỏ vào thư mục cũ | Gỡ BlueDrive (giữ tệp), cài lại, chọn lại đúng thư mục cũ | Không tải lại tệp đã có (so mã băm), không tạo bản trùng; tệp sửa khi chưa có ứng dụng được tải lên | `EngineScenarioTests.First_sync_downloads_the_tree_and_a_second_pass_is_quiet`; `PackagingTests.Uninstall_keeps_the_files_by_default_…` | Tự động |
| 10 | Tên tệp có `:` và tiếng Việt có dấu | Trên web tạo `Báo cáo: quý 1.docx`; trên macOS tạo tên NFD | Windows: `Báo cáo꞉ quý 1.docx` (ký tự thay thế), đổi tên trên máy giữ đúng tên gốc trên máy chủ; tiếng Việt luôn NFC | `EngineScenarioTests.Names_invalid_on_windows_and_case_twins_are_mapped`; `UnixDiskTests.Decomposed_names_from_the_disk_are_reported_in_nfc` (CI macOS) | Tự động |
| 11 | Đường dẫn > 260 ký tự | Tạo cây thư mục lồng sâu, tổng > 260 ký tự | Đồng bộ bình thường hai chiều | `PhysicalDiskTests.Paths_longer_than_260_characters_work` | Tự động |
| 12 | Hai tệp chỉ khác hoa thường | Trên web có `Kehoach.docx` và `KeHoach.docx` cùng thư mục | Trên Windows một tệp giữ tên, tệp kia đổi thành `KeHoach (2).docx`; không ghi đè nhau | `EngineScenarioTests.Names_invalid_on_windows_and_case_twins_are_mapped`; `PhysicalDiskTests.Case_only_rename_of_a_folder_works_…` | Tự động |
| 13 | Tệp theo yêu cầu | Mở tệp chỉ có trên mạng; "Luôn giữ trên thiết bị"; "Giải phóng dung lượng" | Tải khi mở (biểu tượng đổi), giữ/giải phóng đúng | `FilesOnDemandTests` (Cloud Files thật) | Đạt (máy dev Windows 11, nhiều lần chạy) |
| 14 | 100 000 tệp | Tài khoản có 100 000 tệp / 5 000 thư mục | Lần đầu < 5 phút, quét lại < 30 giây, RAM < 200 MB | `bluedrive-perf scale` | Đạt (210 giây / 4,5 giây / 105 MB) |
| 15 | Web ↔ máy | Tạo thư mục trên web; lưu tệp Word trên máy | Xuất hiện trên máy < 5 giây; có trên web < 10 giây | `bluedrive-perf latency`; IT `SyncTests.Sync_hub_pushes_changes_available_within_seconds` | Đạt (0,73 giây / 3,25 giây) |
| 16 | Rút mạng, sửa 50 tệp, cắm lại | Tắt mạng, sửa 50 tệp, bật mạng | Cả 50 tệp lên máy chủ đúng, không bản trùng | FsCheck (ngắt mạng ngẫu nhiên); `EngineScenarioTests.Lost_responses_and_crashes_converge_without_duplicates` | Tự động |
| 17 | Cài đặt, cập nhật, gỡ | `BlueDriveSetup.exe --silent`, phát hành bản mới, gỡ | Cài theo người dùng; tải bản mới nền và cài khi thoát; gỡ sạch (sync root, menu, khởi động cùng Windows, mã đăng nhập) | `PackagingTests` | Đạt (1.0.0 → 1.0.1, 03/11/2026) |
| 18 | Chia sẻ từ Explorer | Chuột phải → BlueDrive → Chia sẻ… → Liên kết có mật khẩu | Tạo liên kết có mật khẩu, tự sao chép | IT `DesktopClientTests.Explorer_context_menu_commands_work_against_the_api` | Tự động |
| 19 | Máy chủ nội bộ với ghim chứng thư | Đặt `CertificatePins` (chính sách) bằng mã SPKI của máy chủ; thử với chứng thư khác | Kết nối đúng máy chủ; chứng thư khác (dù hợp lệ) bị từ chối | `ClientTests.Certificate_pinning_accepts_only_the_pinned_server_key` | Tự động |

Còn phải chạy tay trên 2 máy Windows vật lý trước nghiệm thu: các mục **Tự động** ở trên (kiểm thử tự động đã dùng API thật và đĩa thật, nhưng
một máy / thiết bị giả lập). Ghi kết quả vào cột cuối kèm ngày và người chạy.

## Kết quả nghiệm thu tự động — mục 15, hai thiết bị

Chạy ngày 03/11/2026 bằng `bluedrive-perf acceptance` (`desktop/tools/BlueDrive.Sync.Perf`): hai BlueDrive Desktop độc lập ("MAY-A", "MAY-B": hai
lần đăng nhập thiết bị PKCE, hai thư mục đồng bộ trên đĩa thật, tác tử đầy đủ có SignalR) với máy chủ BlueDrive thật (stack Docker trên
cùng máy). Chạy lại: `dotnet desktop/tools/BlueDrive.Sync.Perf/bin/Release/net10.0-windows10.0.19041.0/bluedrive-perf.dll acceptance --server <máy chủ> --user <quản trị> --password <mật khẩu>`.
Hai lần chạy liên tiếp đều đạt; số liệu lần 2:

| Tiêu chí (mục 15) | Kết quả | Đo được |
|---|---|---|
| Sửa/thêm/đổi tên/di chuyển/xóa trên máy → web < 10 giây | Đạt | thêm 3,2 s; sửa 3,1 s; đổi tên 0,1 s; di chuyển 3,1 s; xóa 3,2 s (gồm 3 s chờ thư mục yên) |
| Web → máy < 5 giây | Đạt | 0,32 s |
| Sửa đồng thời 2 nơi → bản xung đột đúng tên, không mất nội dung | Đạt | `biên bản (Bản xung đột - <Họ tên> - MAY-B - 2026-11-03 2007).txt`, cả hai nội dung còn |
| Mất mạng, sửa 50 tệp, có mạng lại → lên máy chủ đủ | Đạt | 0 tệp lên khi ngoại tuyến; đủ 50 sau 2,6 s |
| Xóa hơn 100 tệp → hỏi xác nhận | Đạt | "Bạn vừa xóa 120 tệp. Xóa cả trên BlueDrive?", máy chủ còn đủ 120 |
| "Chia sẻ…" tạo liên kết có mật khẩu | Đạt | liên kết `/s/…` có mật khẩu |
| Tệp chỉ quyền Xem không tải xuống máy | Đạt | tệp "Xem + Tải xuống" có trên máy, tệp "Chỉ xem" không |
| Thu hồi thiết bị; nhật ký có device_id | Đạt | máy B dừng sau 3,1 s ("Cần đăng nhập lại"); nhật ký có device_id của máy A |
| Tệp theo yêu cầu: mở tự tải, "Luôn giữ", "Giải phóng dung lượng" | Đạt | `FilesOnDemandTests` (Cloud Files thật, Windows 11) |
| Simulation 10 000 chuỗi; kill-test không mất dữ liệu | Đạt | `SimulationTests`, IT `SyncAgentTests.Killing_the_agent_in_the_middle_loses_nothing_and_duplicates_nothing` |
| UI, thông báo, tài liệu tiếng Việt; không TODO/stub | Đạt | |

**Lỗi tìm ra khi nghiệm thu và đã sửa:** người dùng xóa nhiều tệp lần lượt (như Explorer) trong lúc máy chủ đẩy một thay đổi xuống → lượt
đồng bộ chạy giữa chừng, thấy < 100 tệp bị xóa và xóa luôn trên máy chủ không hỏi (tái hiện: 71/120 tệp bị xóa). Nay mọi lượt đợi thư mục
yên (3 giây, tối đa 30 giây) khi đang có thay đổi trên máy — IT `SyncAgentTests.Deleting_many_files_asks_first_even_when_the_server_pushes_a_change_meanwhile`.

Chưa thay thế được: chạy trên 2 máy Windows vật lý khác nhau (mạng LAN/Internet thật, Word thật). Khi nghiệm thu với đơn vị, chạy lệnh trên
từ 2 máy vào máy chủ thật, cùng các kịch bản tay ở bảng trên.
