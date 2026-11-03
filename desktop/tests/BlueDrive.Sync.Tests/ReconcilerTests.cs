using BlueDrive.Sync.Core;
using FluentAssertions;
using L = BlueDrive.Sync.Core.LocalDelta;
using R = BlueDrive.Sync.Core.RemoteDelta;
using A = BlueDrive.Sync.Core.SyncAction;

namespace BlueDrive.Sync.Tests;

/// <summary>Every row of the decision table (spec 6.3, desktop/docs/sync-algorithm.md).</summary>
public class ReconcilerTests
{
    private static SyncDecision D(L l, R r, bool lMoved = false, bool rMoved = false, bool same = false, bool sameLoc = false, bool canEdit = true,
        bool folder = false, bool keeps = false) => Reconciler.Decide(new ItemFacts(folder, l, lMoved, r, rMoved, same, sameLoc, canEdit, keeps));

    [Theory]
    // Local         Remote        expected
    [InlineData(L.Unchanged, R.Unchanged, A.None)]
    [InlineData(L.Modified, R.Unchanged, A.UploadVersion)]
    [InlineData(L.Unchanged, R.Modified, A.Download)]
    [InlineData(L.Modified, R.Modified, A.Conflict)]
    [InlineData(L.New, R.Absent, A.UploadNew)]
    [InlineData(L.Absent, R.New, A.CreateLocal)]
    [InlineData(L.New, R.New, A.Conflict)]
    [InlineData(L.Deleted, R.Unchanged, A.DeleteRemote | A.ForgetBase)]
    [InlineData(L.Deleted, R.Modified, A.RestoreLocal)]
    [InlineData(L.Modified, R.Deleted, A.ReuploadAsNew)]
    [InlineData(L.Unchanged, R.Deleted, A.DeleteLocal | A.ForgetBase)]
    [InlineData(L.Deleted, R.Deleted, A.ForgetBase)]
    [InlineData(L.Unchanged, R.AccessRevoked, A.DeleteLocal | A.ForgetBase)]
    [InlineData(L.Modified, R.AccessRevoked, A.Quarantine | A.ForgetBase)]
    [InlineData(L.Deleted, R.AccessRevoked, A.ForgetBase)]
    public void Content_rows(L local, R remote, A expected) => D(local, remote).Actions.Should().Be(expected);

    [Fact]
    public void Both_modified_with_the_same_hash_converge() => D(L.Modified, R.Modified, same: true).Actions.Should().Be(A.Converge);

    [Fact]
    public void Both_created_with_the_same_content_are_linked() => D(L.New, R.New, same: true).Actions.Should().Be(A.Converge);

    [Fact]
    public void Both_created_folders_are_merged() => D(L.New, R.New, folder: true).Actions.Should().Be(A.Converge);

    [Fact]
    public void Local_rename_is_pushed_without_content() => D(L.Unchanged, R.Unchanged, lMoved: true).Actions.Should().Be(A.MoveRemote);

    [Fact]
    public void Remote_rename_is_applied_locally() => D(L.Unchanged, R.Unchanged, rMoved: true).Actions.Should().Be(A.MoveLocal);

    [Fact]
    public void Competing_renames_prefer_the_server() => D(L.Unchanged, R.Unchanged, lMoved: true, rMoved: true).Actions.Should().Be(A.MoveLocal);

    [Fact]
    public void Same_rename_on_both_sides_converges() =>
        D(L.Unchanged, R.Unchanged, lMoved: true, rMoved: true, sameLoc: true).Actions.Should().Be(A.Converge);

    [Fact]
    public void Edit_and_rename_combine() => D(L.Modified, R.Unchanged, rMoved: true).Actions.Should().Be(A.UploadVersion | A.MoveLocal);

    [Fact]
    public void Deleted_locally_but_moved_on_the_server_is_restored() => D(L.Deleted, R.Unchanged, rMoved: true).Actions.Should().Be(A.RestoreLocal);

    [Fact]
    public void Deleted_folder_with_new_server_children_is_restored() =>
        D(L.Deleted, R.Unchanged, folder: true, keeps: true).Actions.Should().Be(A.RestoreLocal);

    [Fact]
    public void Deleted_empty_folder_goes_to_the_server_trash() =>
        D(L.Deleted, R.Unchanged, folder: true).Actions.Should().Be(A.DeleteRemote | A.ForgetBase);

    [Fact]
    public void Edit_without_edit_right_is_quarantined_and_the_server_copy_restored()
    {
        D(L.Modified, R.Unchanged, canEdit: false).Actions.Should().Be(A.Quarantine | A.Download);
        D(L.Modified, R.Modified, canEdit: false).Actions.Should().Be(A.Quarantine | A.Download);
    }

    [Fact]
    public void Every_decision_explains_itself_in_vietnamese()
    {
        foreach (var l in Enum.GetValues<L>())
            foreach (var r in Enum.GetValues<R>())
            {
                var d = D(l, r);
                if (d.Actions != A.None || (l, r) is (L.Unchanged, R.Unchanged)) d.Reason.Should().NotBeNullOrWhiteSpace($"{l}/{r}");
            }
    }

    [Fact]
    public void Deletion_never_beats_a_change_on_the_other_side()
    {
        foreach (var moved in new[] { false, true })
        {
            D(L.Deleted, R.Modified, rMoved: moved).Has(A.DeleteRemote).Should().BeFalse();
            D(L.Modified, R.Deleted, lMoved: moved).Has(A.DeleteLocal).Should().BeFalse();
        }
    }
}

public class NameMapperTests
{
    [Theory]
    [InlineData("Báo cáo: quý 1?.docx", "Báo cáo꞉ quý 1？.docx")]
    [InlineData("a<b>c|d*e\"f.txt", "a＜b＞c｜d＊e＂f.txt")]
    [InlineData("thư mục/con", "thư mục／con")]
    [InlineData("kết thúc.", "kết thúc．")]
    [InlineData("khoảng trắng ", "khoảng trắng ")]
    [InlineData("CON", "CON ")]
    [InlineData("nul.txt", "nul .txt")]
    [InlineData("bình thường.pdf", "bình thường.pdf")]
    public void Server_names_become_valid_windows_names(string remote, string local) => NameMapper.ToLocal(remote).Should().Be(local);

    [Fact]
    public void Vietnamese_is_normalized_to_nfc()
    {
        var nfd = "Dân số".Normalize(System.Text.NormalizationForm.FormD);
        NameMapper.ToLocal(nfd).Should().Be("Dân số".Normalize(System.Text.NormalizationForm.FormC));
    }

    [Fact]
    public void Conflict_copy_name_follows_the_spec() =>
        NameMapper.ConflictName("Hợp đồng.docx", "Trần Thị B", "PC-KE-TOAN", new DateTime(2026, 11, 3, 14, 5, 0))
            .Should().Be("Hợp đồng (Bản xung đột - Trần Thị B - PC-KE-TOAN - 2026-11-03 1405).docx");

    [Fact]
    public void Next_free_name_is_case_insensitive()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Báo cáo.docx", "báo cáo (1).docx" };
        NameMapper.NextFree("Báo cáo.docx", taken.Contains).Should().Be("Báo cáo (2).docx");
    }

    [Fact]
    public void Case_twin_gets_a_marker() =>
        NameMapper.CaseCollisionName("a.docx").Should().Be("a (trùng tên chữ hoa-thường).docx");

    [Theory]
    [InlineData("~$Báo cáo.docx", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("desktop.ini", true)]
    [InlineData(".~lock.bang.xlsx#", true)]
    [InlineData("tai-ve.crdownload", true)]
    [InlineData("x.tmp", true)]
    [InlineData("Báo cáo.docx", false)]
    public void Default_ignore_list(string name, bool ignored) => IgnoreRules.IsIgnored(name).Should().Be(ignored);
}

public class SyncStateDbTests
{
    [Fact]
    public void Base_items_operations_and_activity_round_trip_and_survive_reopening()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bd-state-{Guid.NewGuid():N}.db");
        try
        {
            var mtime = new DateTime(2026, 11, 3, 1, 2, 3, DateTimeKind.Utc);
            using (var db = new SyncStateDb(path))
            {
                db.UpsertRoot("ws:1", "Tài liệu của tôi", true);
                db.SetCursor("ws:1", 42);
                var item = new BaseItem { RootId = "ws:1", RemoteId = "r1", Name = "a.txt", RemoteName = "a.txt", Hash = "h", LocalMtime = mtime, LocalFileId = "7" };
                db.SaveItem(item);
                item.LocalId.Should().BeGreaterThan(0);
                db.Enqueue("ws:1", "upload_new", "a.txt", null);
                db.UpsertRemote("ws:1", new RemoteItem("r1", null, "a.txt", false, 3, "h", 1, mtime, true, false));
                db.Log("Đã tải lên", "a.txt");
            }
            using (var db = new SyncStateDb(path))
            {
                db.GetRoot("ws:1")!.Cursor.Should().Be(42);
                db.Items("ws:1").Should().ContainSingle().Which.LocalMtime.Should().Be(mtime);
                db.PendingOperations("ws:1").Should().ContainSingle(o => o.Type == "upload_new");
                db.RemoteTree("ws:1")["r1"].CanDownload.Should().BeFalse();
                db.Activity().Should().ContainSingle(a => a.Path == "a.txt");
            }
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }
}
