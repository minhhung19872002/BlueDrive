# Kịch bản kiểm thử BlueDrive Desktop

Kịch bản thủ công / E2E theo mục 12.4 của đặc tả đồng bộ desktop. Mỗi kịch bản có bước làm, kết quả mong đợi, kiểm thử tự động tương ứng
(chạy trong `scripts/ci.ps1` / GitHub Actions) và lần kiểm chứng trên máy thật.

Ký hiệu kết quả: **Đạt** — đã chạy và đúng; **Tự động** — có kiểm thử tự động trên API thật hoặc đĩa thật, chưa chạy tay trên 2 máy vật lý.

Chuẩn bị: máy chủ BlueDrive (stack Docker hoặc https://bluedrive.bluestar.com.vn), 2 máy Windows 11 cài BlueDrive Desktop (máy A, máy B) cùng
tài khoản `bien_tap`, trình duyệt đăng nhập tài khoản đó; tài khoản thứ hai để chia sẻ.

| # | Kịch bản | Bước | Kết quả mong đợi | Kiểm thử tự động | Kết quả |
|---|---|---|---|---|---|
| 1 | Sửa Word đồng thời từ 2 máy | Mở cùng một tệp `.docx` trên A và B (tệp trên máy), sửa khác nhau, lưu gần như cùng lúc | Một bản lên máy chủ thành phiên bản mới; máy còn lại nhận bản xung đột `… (Bản xung đột - <Họ tên> - <Máy> - <ngày giờ>).docx`; không mất nội dung; Cài đặt → Vấn đề có mục xung đột | `EngineScenarioTests.Concurrent_edits_keep_both_versions_with_a_conflict_copy`; IT `DesktopClientTests.Two_computers_sync_create_edit_rename_conflict_and_delete_through_the_real_api`; FsCheck 10 000 chuỗi | Đạt (2 máy, 04/11/2026) |
| 1b | Cùng soạn bằng ONLYOFFICE | Chuột phải tệp → BlueDrive → Cùng soạn thảo bằng ONLYOFFICE | ONLYOFFICE Desktop Editors mở tab soạn thảo, đã đăng nhập, chế độ chỉnh sửa | IT `OnlyOffice_desktop_ticket_…`; E2E `oo-desktop.spec.ts` | Đạt (ONLYOFFICE 10.0.0.169, 03/11/2026) |
| 2 | Đổi tên thư mục lớn | Đổi tên thư mục có 1 000 tệp trên máy A | Trên web và máy B chỉ là một lần đổi tên (không tải lại nội dung), dưới 10 giây | `EngineScenarioTests.Local_creates_and_renames_reach_the_other_computer_without_reuploading`; `PhysicalDiskTests.Scan_reports_relative_paths_and_a_file_id_that_survives_rename_and_move`; `FilesOnDemandTests.Only_closing_a_changed_file_asks_for_a_sync` | Đạt (2 máy, sau khi sửa: 18,5 s → 3,9 s) |
| 3 | Di chuyển thư mục giữa nhóm | Kéo một thư mục từ `Nhóm\A` sang `Nhóm\B` | Thư mục chuyển nhóm trên máy chủ (cùng mục, đổi vị trí); không có quyền ở nhóm đích → thư mục quay về chỗ cũ và có thông báo | `EngineScenarioTests.Remote_rename_and_move_are_applied_locally`; IT `SyncTests.Batch_creates_trees_and_reports_conflicts_per_operation`; IT `SyncAgentTests.A_folder_dragged_to_another_group_is_moved_there_on_the_server` | Đạt (máy A, prod, sau khi sửa) |
| 4 | Xóa 500 tệp | Chọn 500 tệp trong thư mục BlueDrive → Delete | BlueDrive tạm dừng xóa trên máy chủ và hỏi "Bạn vừa xóa 500 tệp. Xóa cả trên BlueDrive?"; Không → tệp được khôi phục; Có → vào thùng rác máy chủ | `EngineScenarioTests.Mass_delete_waits_for_the_user` | Đạt (2 máy: 150 tệp → khôi phục; 120 tệp → thùng rác) |
| 5 | Rút mạng giữa lúc tải lên video 2 GB | Chép video 2 GB vào thư mục, rút mạng ở ~50%, cắm lại (hoặc tắt máy rồi bật) | Tiếp tục từ vị trí đã gửi (tus), mã băm khớp, không tạo bản trùng | IT `DesktopClientTests.An_interrupted_upload_continues_after_a_restart_without_sending_the_start_again`; `LargeFileTests`; `bao-cao-hieu-nang.md` | Đạt (2 máy, tệp 150 MB: 80 MB trước khi rút, 73 MB sau) |
| 6 | Hết dung lượng | Đặt hạn mức nhỏ cho người dùng, chép tệp vượt hạn mức | Thông báo "Hết dung lượng BlueDrive"; tệp ở Cài đặt → Vấn đề; thay đổi giữ trên máy, tự tải lên khi có chỗ | IT `DesktopClientTests.Out_of_quota_keeps_the_file_on_the_computer_and_uploads_it_once_there_is_space` | Đạt (2 máy) |
| 7 | Thu hồi quyền khi đang mở tệp | Người chia sẻ hủy chia sẻ trong lúc người nhận đang mở tệp | Sau khi đóng tệp, mục được gỡ khỏi máy (tệp đã sửa chưa lên được chuyển vào `_Không đồng bộ được`), nhật ký "access_revoked" | IT `SyncTests.Shared_root_lists_shares_flags_view_only_and_reports_access_revoked`; `EngineScenarioTests.Edits_without_edit_right_are_kept_aside_and_the_server_copy_restored`; IT `SyncAgentTests.A_share_taken_back_while_a_file_is_open_waits_quietly_and_keeps_the_edit` | Đạt (2 máy, sau khi sửa) |
| 8 | Xóa từ xa (remote wipe) | Quản trị → Thiết bị → Xóa dữ liệu từ xa máy A | Máy A dừng đồng bộ, xóa thư mục BlueDrive và mã đăng nhập (lệnh có chữ ký HMAC), xác nhận về máy chủ; nhật ký có `deviceId` | IT `SyncAgentTests.Revoked_device_stops_and_a_signed_remote_wipe_deletes_the_local_copy`, `SyncTests.Remote_wipe_is_signed_with_the_device_secret_and_ends_the_device` | Đạt (máy B, prod: yêu cầu → xác nhận 3 s) |
| 9 | Cài lại máy, trỏ vào thư mục cũ | Gỡ BlueDrive (giữ tệp), cài lại, chọn lại đúng thư mục cũ | Không tải lại tệp đã có (so mã băm), không tạo bản trùng; tệp sửa khi chưa có ứng dụng được tải lên | `EngineScenarioTests.First_sync_downloads_the_tree_and_a_second_pass_is_quiet`; `PackagingTests.Uninstall_keeps_the_files_by_default_…` | Đạt (2 máy, sau khi sửa) |
| 10 | Tên tệp có `:` và tiếng Việt có dấu | Trên web tạo `Báo cáo: quý 1.docx`; trên macOS tạo tên NFD | Windows: `Báo cáo꞉ quý 1.docx` (ký tự thay thế), đổi tên trên máy giữ đúng tên gốc trên máy chủ; tiếng Việt luôn NFC | `EngineScenarioTests.Names_invalid_on_windows_and_case_twins_are_mapped`; `UnixDiskTests.Decomposed_names_from_the_disk_are_reported_in_nfc` (CI macOS) | Đạt (2 máy: tên NFD về máy là NFC; máy chủ không nhận `:` khi tải lên và đổi tên, nên tên có `:` không xuất hiện) |
| 11 | Đường dẫn > 260 ký tự | Tạo cây thư mục lồng sâu, tổng > 260 ký tự | Đồng bộ bình thường hai chiều | `PhysicalDiskTests.Paths_longer_than_260_characters_work` | Đạt (2 máy, 427 ký tự) |
| 12 | Hai tệp chỉ khác hoa thường | Trên web có `Kehoach.docx` và `KeHoach.docx` cùng thư mục | Trên Windows một tệp giữ tên, tệp kia đổi thành `KeHoach (trùng tên chữ hoa-thường).docx`; không ghi đè nhau | `EngineScenarioTests.Names_invalid_on_windows_and_case_twins_are_mapped`; `PhysicalDiskTests.Case_only_rename_of_a_folder_works_…` | Đạt (2 máy) |
| 13 | Tệp theo yêu cầu | Mở tệp chỉ có trên mạng; "Luôn giữ trên thiết bị"; "Giải phóng dung lượng" | Tải khi mở (biểu tượng đổi), giữ/giải phóng đúng | `FilesOnDemandTests` (Cloud Files thật) | Đạt (máy dev Windows 11, nhiều lần chạy) |
| 14 | 100 000 tệp | Tài khoản có 100 000 tệp / 5 000 thư mục | Lần đầu < 5 phút, quét lại < 30 giây, RAM < 200 MB | `bluedrive-perf scale` | Đạt (210 giây / 4,5 giây / 105 MB) |
| 15 | Web ↔ máy | Tạo thư mục trên web; lưu tệp Word trên máy | Xuất hiện trên máy < 5 giây; có trên web < 10 giây | `bluedrive-perf latency`; IT `SyncTests.Sync_hub_pushes_changes_available_within_seconds` | Đạt (0,73 giây / 3,25 giây) |
| 16 | Rút mạng, sửa 50 tệp, cắm lại | Tắt mạng, sửa 50 tệp, bật mạng | Cả 50 tệp lên máy chủ đúng, không bản trùng | FsCheck (ngắt mạng ngẫu nhiên); `EngineScenarioTests.Lost_responses_and_crashes_converge_without_duplicates` | Đạt (2 máy: 0/50 khi mất mạng, 50/50 sau 49 s) |
| 17 | Cài đặt, cập nhật, gỡ | `BlueDriveSetup.exe --silent`, phát hành bản mới, gỡ | Cài theo người dùng; tải bản mới nền và cài khi thoát; gỡ sạch (sync root, menu, khởi động cùng Windows, mã đăng nhập) | `PackagingTests` | Đạt (1.0.0 → 1.0.1, 03/11/2026) |
| 18 | Chia sẻ từ Explorer | Chuột phải → BlueDrive → Chia sẻ… → Liên kết (Hạn chế hoặc ai có liên kết) | Tạo liên kết, tự sao chép; "Hạn chế": phải đăng nhập và có quyền | IT `DesktopClientTests.Explorer_context_menu_commands_work_against_the_api` | Đạt (máy A, prod, 04/11/2026 với liên kết có mật khẩu: không mật khẩu / sai → 403, đúng → mở; mật khẩu liên kết bỏ theo quyết định ngày 04/11/2026, thay bằng "Hạn chế") |
| 19 | Máy chủ nội bộ với ghim chứng thư | Đặt `CertificatePins` (chính sách) bằng mã SPKI của máy chủ; thử với chứng thư khác | Kết nối đúng máy chủ; chứng thư khác (dù hợp lệ) bị từ chối | `ClientTests.Certificate_pinning_accepts_only_the_pinned_server_key` | Đạt (máy A, prod: mã sai → không kết nối, "Ngoại tuyến"; đúng mã SPKI → đồng bộ) |
| 20 | Nhấp đúp tệp Office → cùng soạn | Cài đặt → Đồng bộ → "Luôn mở chế độ cùng soạn"; nhấp đúp `.docx` trong BlueDrive; tắt mạng rồi nhấp đúp lại; nhấp đúp `.docx` ngoài BlueDrive | Có mạng: mở trong ONLYOFFICE Desktop Editors (chế độ cùng soạn); mất mạng: mở bản trên máy bằng Word; ngoài BlueDrive: Word như thường; tắt tùy chọn trả lại như cũ | `PackagingTests.Double_click_on_office_documents_can_open_co_editing_only_inside_bluedrive_folders`; chạy tay 03–04/11/2026 trên Windows 11 (ONLYOFFICE 10.0, Microsoft Word): nhấp đúp chuột thật trong Explorer — trong BlueDrive mở ONLYOFFICE cùng soạn, ngoài BlueDrive mở Word | Đạt (một máy) |

Các mục **Tự động** ở trên có kiểm thử tự động trên API thật và đĩa thật (một máy / thiết bị giả lập). Ngày 04/11/2026 đã chạy tay trên 2 máy
Windows với máy chủ thật (kết quả ở phần dưới, cột cuối ghi "2 máy" / "máy A"); mọi mục trong bảng đã chạy tay.

## Kết quả chạy trên 2 máy Windows — 04/11/2026, máy chủ thật

Máy chủ https://bluedrive.bluestar.com.vn. **Máy A**: máy Windows 11 thật (LAPTOP-KGTEG72G), tài khoản `bientap1`. **Máy B**: máy Windows 11 Pro
24H2 riêng (VirtualBox, mạng bridged qua Wi-Fi, IP riêng trên LAN), tài khoản `bientap2`. Người thứ ba trên web: `bientap3`. Thư mục
"Kiểm thử 2 máy" của `bientap1` chia sẻ quyền Chỉnh sửa cho `bientap2`, `bientap3` (trên B nằm ở `Được chia sẻ với tôi\Nguyễn Văn Biên Tập 1\`).
BlueDrive Desktop 1.0.3 → 1.0.6 (các bản sửa bên dưới). Thời gian lấy từ nhật ký hai máy (đồng hồ lệch nhau vài giây).

| Tiêu chí | Kết quả | Đo được |
|---|---|---|
| Cài trên Windows 11 sạch (máy B mới cài 24H2), đăng nhập bằng trình duyệt | Đạt | Thư mục "BlueDrive - <đơn vị>" có ở khung trái Explorer, cây Tài liệu của tôi / Nhóm / Được chia sẻ với tôi, mọi tệp ở dạng chỉ có trên mạng (☁); mở tệp thì tự tải, "Luôn giữ" / "Giải phóng dung lượng" đổi biểu tượng đúng |
| Tạo / sửa Word, Excel, PowerPoint trên A → B và B → A | Đạt | A → B 7–8 s (tạo 11:32:43,6 → B có tệp 11:32:50,7); B → A 7–10 s; nội dung Word/Excel/PowerPoint sửa trên B có đủ trên A |
| Sửa đồng thời cùng tệp Word trên A và B (cách nhau 0,7 s) | Đạt | B lên trước; A giữ bản của mình thành `Báo cáo (Bản xung đột - Nguyễn Văn Biên Tập 1 - LAPTOP-KGTEG72G - 2026-11-04 1135).docx`, có thông báo; bản xung đột cũng về B; không mất nội dung |
| 3 người cùng sửa 1 tệp: web + ONLYOFFICE Desktop trên A và trên B (mục 15) | Đạt | Cả ba thấy chữ và con trỏ của nhau tức thời (biểu tượng cộng tác: 3); đóng → đúng 1 phiên bản mới (3 → 4); `Báo cáo.docx` trên A và B tự cập nhật đủ dòng của cả ba. Excel (ô sửa trên B về A sau 8 s) và PowerPoint (slide sửa trên B về A sau 8 s) cũng mở cùng soạn được |
| Đổi tên thư mục 1 000 tệp | Đạt sau khi sửa | Trước: 18,5 s; sau: 3,9 s trên A, B chỉ nhận 1 thao tác đổi tên, 0 tệp tải lại |
| Xóa 150 / 120 tệp trên B | Đạt | Máy chủ không xóa gì cho tới khi trả lời; "Khôi phục các tệp" lấy lại đủ 150; "Xóa cả trên BlueDrive" → cả hai máy còn 880, thùng rác có 120 tệp, người xóa "Trần Thị Biên Tập 2" |
| Tên tiếng Việt NFD, hai tệp chỉ khác hoa thường, đường dẫn 427 ký tự | Đạt | NFD về máy là NFC; `KeHoach (trùng tên chữ hoa-thường).docx`; tệp ở đường dẫn 427 ký tự tải và đọc được trên B |
| Mất mạng trên B, sửa 50 tệp, có mạng lại | Đạt | A nhận 0/50 khi B mất mạng; 50/50 sau 49 s khi có mạng lại; không có bản trùng |
| Rút mạng giữa lúc tải lên tệp 150 MB | Đạt | Gửi 80,3 MB → rút mạng 20 s → cắm lại → chỉ gửi thêm 72,8 MB (tổng 153 MB); SHA-256 tải về trên A trùng bản gốc trên B |
| Hết dung lượng (hạn mức 1 MB) | Đạt | "Hết dung lượng BlueDrive…", tệp giữ trên máy; trả hạn mức → tự lên máy chủ ở lượt sau (4 phút) |
| Thu hồi chia sẻ khi B đang mở và sửa tệp | Đạt sau khi sửa | Sau khi đóng tệp: thư mục gỡ khỏi B, bản đã sửa ở `_Không đồng bộ được`. Thu hồi thư mục B không sửa: gỡ sạch |
| Gỡ BlueDrive (giữ tệp), sửa tệp, cài lại, chọn thư mục cũ | Đạt sau khi sửa | Gỡ: chỉ còn thư mục `logs`; cài lại mở trình hướng dẫn, thư mục cũ được nhận ("tệp trùng sẽ được liên kết"); hai tệp 150 MB "Đã khớp" (không tải lại); tệp sửa khi chưa có ứng dụng được tải lên |
| Chia sẻ từ Explorer → liên kết | Đạt | `/s/…` trong bộ nhớ tạm. Lúc chạy là liên kết có mật khẩu (không mật khẩu / sai → 403, đúng → mở); sau đó mật khẩu liên kết đã bỏ, thay bằng liên kết "Hạn chế" (đăng nhập và có quyền) |
| Kéo thư mục giữa hai nhóm (`Nhóm\Nhóm truyền thông B` → `Nhóm\Nhóm truyền thông A`) | Đạt sau khi sửa | Cùng thư mục (cùng mã) chuyển sang nhóm A sau 4 s, thùng rác nhóm B trống. Kéo vào "Nhóm chỉ xem" (chỉ có quyền xem) → "Hoàn tác", thư mục về chỗ cũ, máy chủ không đổi |
| Ghim chứng thư (chính sách `CertificatePins`) | Đạt | Mã SPKI sai → không kết nối máy chủ thật; mã đúng (`iNmwmygd…`) → tệp chờ được tải lên |
| Xóa từ xa máy B (Quản trị → Thiết bị) | Đạt | Yêu cầu 13:34:37 → B xác nhận 13:34:40; thư mục BlueDrive và mã đăng nhập trên B bị xóa; thiết bị ở trạng thái "đã xóa" |
| Mất mạng giữa phiên cùng soạn (ONLYOFFICE Desktop, mục 13A) | Đạt | Cắt mạng của ONLYOFFICE giữa phiên: báo "Connection is lost", khóa sửa; phiên bản 6 đã có dòng gõ trước đó; có mạng lại → "Đã thay đổi phiên bản", mở lại, gõ tiếp → phiên bản 7 đủ cả hai dòng |
| Đồng bộ trong mạng LAN | Không dùng | Có chạy thử trên 2 máy (tìm thấy nhau qua UDP 47391, kết nối TCP được) nhưng ở chế độ "Tệp theo yêu cầu" (mặc định) tệp mở/ghim luôn tải từ máy chủ, chỉ đồng bộ đầy đủ mới lấy qua LAN. Theo quyết định ngày 04/11/2026, BlueDrive chỉ đồng bộ với máy chủ: "Đồng bộ trong mạng LAN" giữ tắt (mặc định), không bật bằng chính sách khi triển khai |

**Lỗi tìm ra trên 2 máy và đã sửa** (có kiểm thử tự động, CI xanh):
1. Đổi tên thư mục lớn chậm (18,5 s): mỗi lần Windows Search / phần mềm diệt virus mở rồi đóng một tệp (chỉ đọc) đều đẩy lượt đồng bộ lùi
   3 giây. Nay chỉ lần đóng sau khi tệp bị sửa (không còn "in sync") mới hẹn lượt đồng bộ; BlueDrive bỏ qua các lần đóng của chính nó —
   `FilesOnDemandTests.Only_closing_a_changed_file_asks_for_a_sync`.
2. Thư mục đổi tên vẫn hiện biểu tượng "đang chờ đồng bộ" trong Explorer — `FilesOnDemandTests.Renamed_folders_show_synced_again_on_both_sides`.
3. Thu hồi chia sẻ khi đang mở tệp: mỗi lượt báo lỗi tiếng Anh "Access to the path … is denied" và lùi thời gian thử lại tới 213 s. Nay chờ
   tệp được đóng, không báo lỗi — IT `SyncAgentTests.A_share_taken_back_while_a_file_is_open_waits_quietly_and_keeps_the_edit`.
4. Cập nhật: bản stable mới hơn bản trên máy chủ bị hạ phiên bản khi thoát — `PackagingTests.Only_a_beta_going_back_to_stable_may_install_an_older_version`.
5. Gỡ cài đặt không xóa mã đăng nhập / trạng thái khi máy có ONLYOFFICE do BlueDrive cài (Velopack dừng bước gỡ sau 30 s, trước bước xóa).
   Nay xóa ngay sau khi hủy đăng ký thư mục đồng bộ — `PackagingTests.Uninstall_removes_the_onlyoffice_provider_and_asks_before_removing_onlyoffice`.
6. Trình hướng dẫn: đóng tab trình duyệt khi chưa "Cho phép" thì nút "Đăng nhập bằng trình duyệt" bị khóa 10 phút. Nay bấm lại được (mở lại
   trình duyệt), "Quay lại" hủy lượt đang chờ — `DesktopTests.Wizard_sign_in_can_be_started_again_after_the_browser_tab_was_closed`.
7. Kéo thư mục giữa hai nhóm: tải lên lại ở nhóm đích rồi xóa ở nhóm nguồn (mất mã, phiên bản, chia sẻ; cảnh báo xóa hàng loạt với thư mục lớn)
   dù máy chủ cho chuyển. Nay gọi lệnh chuyển của máy chủ (cùng mục); không có quyền ở nhóm đích → trả về chỗ cũ —
   IT `SyncAgentTests.A_folder_dragged_to_another_group_is_moved_there_on_the_server`.
8. Được thêm vào nhóm mới / được chia sẻ mục mới: máy chỉ thấy ở lượt định kỳ (tới 15 phút). Nay máy chủ báo ngay cho người được cấp quyền
   — IT `SyncTests.A_device_hears_at_once_when_its_user_is_added_to_a_workspace`.
9. Web: thùng rác và "Gần đây" hiện biểu tượng Word cho tệp `.txt` (mọi tệp văn bản) — E2E "thùng rác hiện đúng loại tệp".
10. Kéo thư mục 4 tệp vào BlueDrive, chỉ 3 tệp lên web: tệp `.xlsm` bị máy chủ từ chối (định dạng chưa được phép) nhưng Explorer vẫn hiện
    "đang đồng bộ". Nay Explorer hiện trạng thái "Lỗi đồng bộ — <lý do>" trên tệp (lý do cũng có ở Cài đặt → Vấn đề) —
    `DesktopTests.Problems_about_a_file_name_the_file_shown_as_sync_error_in_explorer`. `.xlsm`/`.docm`/`.pptm` (có macro) giữ mặc định
    không cho tải lên; quản trị viên bật trong "Định dạng tệp cho phép" nếu cần.
11. Mở tệp bằng Word trong lúc người khác sửa trên web: khi phiên cùng soạn lưu bản mới, BlueDrive ghi bản mới xuống đĩa ngay dưới Word;
    Word vẫn hiện bản cũ, lưu trong Word thành phiên bản mới đè nội dung của người kia (không có bản xung đột). Nay tệp đang được chương trình
    giữ mở để ghi thì chờ tới khi đóng; nếu đã lưu thì giữ cả hai bản. Chạy lại trên prod với Word thật: lúc Word mở tệp trên đĩa không đổi; lưu
    và đóng Word → "… (Bản xung đột - Quản trị hệ thống - LAPTOP-KGTEG72G - 2026-11-04 1524).docx" chứa phần sửa trong Word, tên gốc là bản
    trên web — `EngineScenarioTests.A_newer_server_copy_waits_while_word_has_the_file_open_and_a_later_save_keeps_both`,
    `PhysicalDiskTests.A_file_held_open_for_writing_like_word_does_is_seen_as_open`. Cũng từ lần này, nhấp đúp tệp Office trong BlueDrive mặc
    định mở chế độ cùng soạn (`DesktopTests.Office_documents_open_for_co_editing_on_double_click_by_default`).

## Kết quả nghiệm thu tự động — mục 15, hai thiết bị

Chạy ngày 03/11/2026 bằng `bluedrive-perf acceptance` (`desktop/tools/BlueDrive.Sync.Perf`): hai BlueDrive Desktop độc lập ("MAY-A", "MAY-B": hai
lần đăng nhập thiết bị PKCE, hai thư mục đồng bộ trên đĩa thật, tác tử đầy đủ có SignalR) với máy chủ BlueDrive thật (stack Docker trên
cùng máy). Chạy lại: `dotnet desktop/tools/BlueDrive.Sync.Perf/bin/Release/net10.0-windows10.0.22621.0/bluedrive-perf.dll acceptance --server <máy chủ> --user <quản trị> --password <mật khẩu>`.
Hai lần chạy liên tiếp đều đạt; số liệu dưới đây là của lần chạy lưu trong [`desktop/docs/perf/ket-qua.json`](../desktop/docs/perf/ket-qua.json) (mục `acceptance`):

| Tiêu chí (mục 15) | Kết quả | Đo được |
|---|---|---|
| Sửa/thêm/đổi tên/di chuyển/xóa trên máy → web < 10 giây | Đạt | thêm 3,2 s; sửa 0,1 s; đổi tên 3,2 s; di chuyển 0,0 s; xóa 3,1 s (các số ~3 s gồm 3 s chờ thư mục yên; số ~0 s là thay đổi đã lên trong lượt trước đó) |
| Web → máy < 5 giây | Đạt | 0,34 s (đo riêng 10 lượt bằng `bluedrive-perf latency`: trung vị 0,73 s, p95 0,85 s) |
| Sửa đồng thời 2 nơi → bản xung đột đúng tên, không mất nội dung | Đạt | `biên bản (Bản xung đột - <Họ tên> - MAY-B - 2026-11-03 2039).txt`, cả hai nội dung còn |
| Mất mạng, sửa 50 tệp, có mạng lại → lên máy chủ đủ | Đạt | 0 tệp lên khi ngoại tuyến; đủ 50 sau 2,6 s |
| Xóa hơn 100 tệp → hỏi xác nhận | Đạt | "Bạn vừa xóa 120 tệp. Xóa cả trên BlueDrive?", máy chủ còn đủ 120 |
| "Chia sẻ…" tạo liên kết | Đạt | liên kết `/s/…` (lúc chạy có mật khẩu; mật khẩu liên kết đã bỏ ngày 04/11/2026, `bluedrive-perf acceptance` nay kiểm liên kết "Hạn chế") |
| Tệp chỉ quyền Xem không tải xuống máy | Đạt | tệp "Xem + Tải xuống" có trên máy, tệp "Chỉ xem" không |
| Thu hồi thiết bị; nhật ký có device_id | Đạt | máy B dừng sau 3,1 s ("Cần đăng nhập lại"); nhật ký có device_id của máy A |
| Tệp theo yêu cầu: mở tự tải, "Luôn giữ", "Giải phóng dung lượng" | Đạt | `FilesOnDemandTests` (Cloud Files thật, Windows 11) |
| Simulation 10 000 chuỗi; kill-test không mất dữ liệu | Đạt | `SimulationTests` (CI chạy 300 chuỗi; workflow phát hành `desktop-release.yml` chạy 10 000 chuỗi với `SYNC_SIM_RUNS=10000`, kèm tệp 1,5 GB `SYNC_LARGE_FILE_MB=1536`), IT `SyncAgentTests.Killing_the_agent_in_the_middle_loses_nothing_and_duplicates_nothing` |
| UI, thông báo, tài liệu tiếng Việt; không TODO/stub | Đạt | |

**Lỗi tìm ra khi nghiệm thu và đã sửa:** người dùng xóa nhiều tệp lần lượt (như Explorer) trong lúc máy chủ đẩy một thay đổi xuống → lượt
đồng bộ chạy giữa chừng, thấy < 100 tệp bị xóa và xóa luôn trên máy chủ không hỏi (tái hiện: 71/120 tệp bị xóa). Nay (1) mọi lượt đợi thư mục
yên (3 giây, tối đa 30 giây) khi đang có thay đổi trên máy, và (2) một tệp xóa trên máy chỉ bị xóa trên máy chủ khi lượt sau (cách ≥ 3 giây) vẫn thấy
nó bị xóa (sự kiện tệp có thể đến muộn hơn lượt quét, như trên Linux) — IT `SyncAgentTests.Deleting_many_files_asks_first_even_when_the_server_pushes_a_change_meanwhile`, `DeleteSettleTests`.

Lần chạy này dùng một máy; lần chạy trên 2 máy Windows với máy chủ thật (04/11/2026) ở phần "Kết quả chạy trên 2 máy Windows". Khi nghiệm thu
với đơn vị, chạy lại lệnh trên từ 2 máy của đơn vị vào máy chủ của đơn vị.
