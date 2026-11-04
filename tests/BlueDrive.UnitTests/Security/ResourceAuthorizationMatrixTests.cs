using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace BlueDrive.UnitTests.Security;

/// <summary>
/// Access matrix of spec §4.4: owner → workspace member role → direct ACL → inherited ACL (break inheritance) → shares → lock,
/// plus approval visibility and expiry.
/// </summary>
public class ResourceAuthorizationMatrixTests
{
    private sealed class World
    {
        public required AppDbContext Db;
        public required ResourceAuthorizationService Auth;
        public Guid Owner, Admin, Editor, Viewer, Uploader, Approver, GroupMember, OrgMember, Outsider, NoRole, ReadOnlyEditor;
        public Guid Workspace, F1, F2, F3, File1, File2, File3, FileRoot;
    }

    private static World Build()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = new Organization { Code = "ORG", Name = "Phòng A" };
        org.Path = $"/{org.Id}/";
        db.Organizations.Add(org);
        User U(string name, Guid? orgId = null)
        {
            var u = new User { Username = name, FullName = name, OrganizationId = orgId };
            db.Users.Add(u);
            return u;
        }
        var owner = U("owner"); var admin = U("admin"); var editor = U("editor"); var viewer = U("viewer"); var uploader = U("uploader");
        var approver = U("approver"); var gm = U("groupmember"); var om = U("orgmember", org.Id); var outsider = U("outsider"); var noRole = U("norole");
        var readOnlyEditor = U("readonlyeditor");
        var group = new UserGroup { Name = "Nhóm biên tập" };
        group.Members.Add(new UserGroupMember { GroupId = group.Id, UserId = gm.Id });
        db.UserGroups.Add(group);

        var ws = new Workspace { Name = "Nhóm truyền thông", OwnerId = owner.Id, Type = WorkspaceType.Group };
        foreach (var (u, r) in new[] { (owner, WorkspaceMemberRole.Owner), (editor, WorkspaceMemberRole.Editor), (viewer, WorkspaceMemberRole.Viewer),
                     (uploader, WorkspaceMemberRole.Uploader), (approver, WorkspaceMemberRole.Approver), (readOnlyEditor, WorkspaceMemberRole.Editor) })
            ws.Members.Add(new WorkspaceMember { WorkspaceId = ws.Id, UserId = u.Id, MemberRole = r });
        db.Workspaces.Add(ws);

        Folder F(string name, Folder? parent, bool brk = false)
        {
            var f = new Folder { WorkspaceId = ws.Id, ParentId = parent?.Id, Name = name, BreakInheritance = brk };
            f.Path = $"{parent?.Path ?? "/"}{f.Id}/";
            db.Folders.Add(f);
            return f;
        }
        var f1 = F("F1", null); var f2 = F("F2", f1); var f3 = F("F3-rieng", f1, brk: true);
        StoredFile File(string name, Folder? folder, Guid createdBy, ApprovalStatus status = ApprovalStatus.Approved)
        {
            var file = new StoredFile { WorkspaceId = ws.Id, FolderId = folder?.Id, Name = name, CreatedBy = createdBy, ApprovalStatus = status };
            db.Files.Add(file);
            return file;
        }
        var file1 = File("a.docx", f2, editor.Id);
        var file2 = File("b.docx", f2, uploader.Id, ApprovalStatus.Pending);
        var file3 = File("c.docx", f3, editor.Id);
        var fileRoot = File("root.docx", null, editor.Id);

        db.ResourcePermissions.Add(new ResourcePermission { ResourceType = ResourceType.Folder, ResourceId = f1.Id, PrincipalType = PrincipalType.Group, PrincipalId = group.Id, AccessLevel = AccessLevel.Download, Inherit = true });
        db.ResourcePermissions.Add(new ResourcePermission { ResourceType = ResourceType.Workspace, ResourceId = ws.Id, PrincipalType = PrincipalType.Organization, PrincipalId = org.Id, AccessLevel = AccessLevel.View });
        db.ResourcePermissions.Add(new ResourcePermission { ResourceType = ResourceType.File, ResourceId = file3.Id, PrincipalType = PrincipalType.User, PrincipalId = outsider.Id, AccessLevel = AccessLevel.View, ExpiresAt = DateTime.UtcNow.AddDays(-1) });
        db.Shares.Add(new Share { ResourceType = ResourceType.File, ResourceId = file1.Id, SharedBy = editor.Id, SharedWithType = PrincipalType.User, SharedWithId = outsider.Id, AccessLevel = AccessLevel.Edit });
        db.SaveChanges();

        var perms = Substitute.For<IPermissionService>();
        perms.GetPermissionsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var id = ci.Arg<Guid>();
            // "Chỉ xem"-like roles (outsider, read-only editor) lack doc.edit: membership rights are capped, shares are not.
            IReadOnlySet<string> set = id == admin.Id ? new HashSet<string> { Perm.DocView, Perm.DocAdmin }
                : id == noRole.Id ? new HashSet<string>()
                : id == outsider.Id || id == readOnlyEditor.Id ? new HashSet<string> { Perm.DocView }
                : new HashSet<string> { Perm.DocView, Perm.DocEdit };
            return Task.FromResult(set);
        });
        return new World
        {
            Db = db, Auth = new ResourceAuthorizationService(db, perms, TimeProvider.System),
            Owner = owner.Id, Admin = admin.Id, Editor = editor.Id, Viewer = viewer.Id, Uploader = uploader.Id, Approver = approver.Id,
            GroupMember = gm.Id, OrgMember = om.Id, Outsider = outsider.Id, NoRole = noRole.Id, ReadOnlyEditor = readOnlyEditor.Id,
            Workspace = ws.Id, F1 = f1.Id, F2 = f2.Id, F3 = f3.Id, File1 = file1.Id, File2 = file2.Id, File3 = file3.Id, FileRoot = fileRoot.Id,
        };
    }

    public static TheoryData<string, string, AccessLevel, string> Matrix => new()
    {
        // user, resource, expected level, expected source prefix
        { "Owner", "File1", AccessLevel.Manage, "owner" },
        { "Admin", "File3", AccessLevel.Manage, "admin" },
        { "Editor", "File1", AccessLevel.Edit, "member" },
        { "Viewer", "File1", AccessLevel.View, "member" },
        { "Uploader", "File1", AccessLevel.Download, "member" },
        { "Uploader", "File2", AccessLevel.Edit, "member:uploader_own" },
        { "Approver", "File2", AccessLevel.Download, "member" },
        { "Viewer", "File2", AccessLevel.None, "none" },
        { "GroupMember", "File1", AccessLevel.Download, "acl_inherited" },
        { "GroupMember", "F2", AccessLevel.Download, "acl_inherited" },
        { "GroupMember", "FileRoot", AccessLevel.None, "none" },
        { "GroupMember", "File3", AccessLevel.None, "none" },
        { "OrgMember", "File1", AccessLevel.View, "acl_inherited" },
        { "OrgMember", "FileRoot", AccessLevel.View, "acl_inherited" },
        { "OrgMember", "File3", AccessLevel.None, "none" },
        { "Viewer", "File3", AccessLevel.None, "none" },
        { "Editor", "File3", AccessLevel.None, "none" },
        { "Outsider", "File1", AccessLevel.Edit, "share" },
        { "Outsider", "File3", AccessLevel.None, "none" },
        { "Outsider", "F2", AccessLevel.None, "none" },
        { "NoRole", "File1", AccessLevel.None, "none" },
        // Editor member whose role has no doc.edit: view + download only.
        { "ReadOnlyEditor", "File1", AccessLevel.Download, "member" },
        { "ReadOnlyEditor", "F2", AccessLevel.Download, "member" },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Effective_access_matches_the_matrix(string user, string resource, AccessLevel expected, string source)
    {
        var w = Build();
        var uid = (Guid)typeof(World).GetField(user)!.GetValue(w)!;
        var rid = (Guid)typeof(World).GetField(resource)!.GetValue(w)!;
        var type = resource.StartsWith("File") ? ResourceType.File : ResourceType.Folder;
        var a = await w.Auth.GetAccessAsync(uid, type, rid);
        a.Level.Should().Be(expected);
        a.Source.Should().StartWith(source);
    }

    [Fact]
    public async Task Owner_and_managers_keep_access_below_break_inheritance()
    {
        var w = Build();
        (await w.Auth.GetAccessAsync(w.Owner, ResourceType.File, w.File3)).Level.Should().Be(AccessLevel.Manage);
    }

    [Fact]
    public async Task Role_without_doc_edit_caps_membership_but_not_explicit_grants()
    {
        var w = Build();
        var member = await w.Auth.GetAccessAsync(w.ReadOnlyEditor, ResourceType.File, w.File1);
        member.CanEdit.Should().BeFalse();
        member.CanDownload.Should().BeTrue();
        var edit = () => w.Auth.EnsureAsync(w.ReadOnlyEditor, ResourceType.File, w.File1, AccessLevel.Edit);
        (await edit.Should().ThrowAsync<Application.Common.AppException>()).Which.StatusCode.Should().Be(403);

        // An explicit "Chỉnh sửa" share is a deliberate exception (commit 3155a67): it is not capped by the role.
        w.Db.Shares.Add(new Share { ResourceType = ResourceType.File, ResourceId = w.File1, SharedBy = w.Editor, SharedWithType = PrincipalType.User, SharedWithId = w.ReadOnlyEditor, AccessLevel = AccessLevel.Edit });
        await w.Db.SaveChangesAsync();
        (await w.Auth.GetAccessAsync(w.ReadOnlyEditor, ResourceType.File, w.File1)).Level.Should().Be(AccessLevel.Edit);
        (await w.Auth.GetAccessAsync(w.Outsider, ResourceType.File, w.File1)).Level.Should().Be(AccessLevel.Edit);
    }

    [Fact]
    public async Task Owner_whose_role_lost_doc_edit_keeps_view_and_download()
    {
        var w = Build();
        var ws = await w.Db.Workspaces.FindAsync(w.Workspace);
        ws!.OwnerId = w.ReadOnlyEditor;
        await w.Db.SaveChangesAsync();
        var a = await w.Auth.GetAccessAsync(w.ReadOnlyEditor, ResourceType.File, w.File3);
        a.Level.Should().Be(AccessLevel.Download, "break inheritance still lets the owner see, but no longer edit");
        a.CanApprove.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false, AccessLevel.Manage, AccessLevel.Manage)]
    [InlineData(false, false, AccessLevel.Manage, AccessLevel.Download)]
    [InlineData(false, false, AccessLevel.Edit, AccessLevel.Download)]
    [InlineData(false, false, AccessLevel.Download, AccessLevel.Download)]
    [InlineData(false, false, AccessLevel.View, AccessLevel.View)]
    [InlineData(false, true, AccessLevel.Manage, AccessLevel.Manage)]
    public void CapByRole_limits_edit_and_manage_to_doc_edit_roles(bool canEdit, bool isAdmin, AccessLevel level, AccessLevel expected)
        => new PrincipalSet(Guid.NewGuid(), [], [], [], isAdmin, true, canEdit).CapByRole(level).Should().Be(expected);

    [Fact]
    public async Task Locked_workspace_caps_writes_but_keeps_reads()
    {
        var w = Build();
        var ws = await w.Db.Workspaces.FindAsync(w.Workspace);
        ws!.IsLocked = true;
        await w.Db.SaveChangesAsync();
        var a = await w.Auth.GetAccessAsync(w.Editor, ResourceType.File, w.File1);
        a.CanView.Should().BeTrue();
        a.CanEdit.Should().BeFalse();
        var act = () => w.Auth.EnsureAsync(w.Editor, ResourceType.File, w.File1, AccessLevel.Edit);
        (await act.Should().ThrowAsync<Application.Common.AppException>()).Which.StatusCode.Should().Be(423);
        var upload = () => w.Auth.EnsureCanUploadAsync(w.Owner, w.Workspace, w.F1);
        (await upload.Should().ThrowAsync<Application.Common.AppException>()).Which.StatusCode.Should().Be(423);
    }

    [Fact]
    public async Task Upload_rights_follow_member_roles()
    {
        var w = Build();
        (await w.Auth.GetAccessAsync(w.Uploader, ResourceType.Folder, w.F2)).CanUpload.Should().BeTrue();
        (await w.Auth.GetAccessAsync(w.Editor, ResourceType.Folder, w.F2)).CanUpload.Should().BeTrue();
        (await w.Auth.GetAccessAsync(w.Viewer, ResourceType.Folder, w.F2)).CanUpload.Should().BeFalse();
        (await w.Auth.GetAccessAsync(w.GroupMember, ResourceType.Folder, w.F2)).CanUpload.Should().BeFalse();
        (await w.Auth.GetAccessAsync(w.Approver, ResourceType.File, w.File2)).CanApprove.Should().BeTrue();
    }

    [Fact]
    public async Task Expired_grants_are_ignored_and_deleted_items_are_invisible()
    {
        var w = Build();
        (await w.Auth.GetAccessAsync(w.Outsider, ResourceType.File, w.File3)).Level.Should().Be(AccessLevel.None);
        var f = await w.Db.Files.FindAsync(w.File1);
        f!.IsDeleted = true;
        await w.Db.SaveChangesAsync();
        (await w.Auth.GetAccessAsync(w.Owner, ResourceType.File, w.File1)).Level.Should().Be(AccessLevel.None);
    }
}
