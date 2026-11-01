using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Features.Storage;
using BlueDrive.Application.Features.Workspaces;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using SkiaSharp;

namespace BlueDrive.Infrastructure.Persistence.Seed;

/// <summary>
/// Demo data for trial operation (only when Seed:Demo=true and not seeded yet): 5 users, 2 group workspaces and
/// sample files of every kind (image, video, audio, Word, Excel, PowerPoint, PDF, text).
/// </summary>
public sealed class DemoDataSeeder(
    AppDbContext db, IConfiguration config, IPasswordService passwords, ISettingsService settings, IFileContentService content,
    IFileTypeInspector types, IOfficeEditorProvider office, IBackgroundJobs jobs, ILogger<DemoDataSeeder> logger) : IDataSeeder
{
    public const string DemoPassword = "Demo@12345";
    public int Order => 10;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!config.GetValue("Seed:Demo", false) || await db.Users.AnyAsync(u => u.Username == "canbo1", ct)) return;
        var depts = await db.Organizations.Where(o => o.ParentId != null).OrderBy(o => o.SortOrder).ToListAsync(ct);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Code, r => r.Id, ct);
        (string User, string Name, string Position, string Role, int Dept)[] people =
        [
            ("canbo1", "Nguyễn Thị Hoa", "Chuyên viên truyền thông", Perm.Roles.Editor, 1),
            ("canbo2", "Trần Văn Nam", "Biên tập viên", Perm.Roles.Editor, 1),
            ("canbo3", "Lê Thu Hằng", "Trưởng phòng", Perm.Roles.Approver, 1),
            ("canbo4", "Phạm Minh Đức", "Chuyên viên", Perm.Roles.Viewer, 2),
            ("canbo5", "Hoàng Lan Anh", "Kỹ thuật viên", Perm.Roles.Editor, 2),
        ];
        var users = new List<User>();
        foreach (var p in people)
        {
            var u = new User { Username = p.User, FullName = p.Name, Position = p.Position, OrganizationId = depts.ElementAtOrDefault(p.Dept)?.Id ?? depts.First().Id };
            await passwords.SetPasswordAsync(u, DemoPassword, mustChange: true, ct);
            u.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = roles[p.Role] });
            db.Users.Add(u);
            await WorkspaceProvisioning.EnsurePersonalWorkspaceAsync(db, settings, u, ct);
            users.Add(u);
        }
        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var org = new Workspace { Name = "Phòng Truyền thông - Tư liệu chung", Description = "Tư liệu truyền thông dùng chung của phòng", Type = WorkspaceType.Organization,
            OrganizationId = depts.ElementAtOrDefault(1)?.Id, OwnerId = users[2].Id, QuotaBytes = 50 * WorkspaceProvisioning.Gb };
        var campaign = new Workspace { Name = "Chiến dịch Ngày Dân số Việt Nam 26/12", Description = "Ấn phẩm, video, hình ảnh của chiến dịch", Type = WorkspaceType.Group,
            OwnerId = users[0].Id, QuotaBytes = 50 * WorkspaceProvisioning.Gb };
        org.Members.AddRange([
            new WorkspaceMember { WorkspaceId = org.Id, UserId = users[2].Id, MemberRole = WorkspaceMemberRole.Owner, AddedAt = now },
            new WorkspaceMember { WorkspaceId = org.Id, UserId = users[0].Id, MemberRole = WorkspaceMemberRole.Editor, AddedAt = now },
            new WorkspaceMember { WorkspaceId = org.Id, UserId = users[1].Id, MemberRole = WorkspaceMemberRole.Uploader, AddedAt = now },
            new WorkspaceMember { WorkspaceId = org.Id, UserId = users[3].Id, MemberRole = WorkspaceMemberRole.Viewer, AddedAt = now },
        ]);
        campaign.Members.AddRange([
            new WorkspaceMember { WorkspaceId = campaign.Id, UserId = users[0].Id, MemberRole = WorkspaceMemberRole.Owner, AddedAt = now },
            new WorkspaceMember { WorkspaceId = campaign.Id, UserId = users[1].Id, MemberRole = WorkspaceMemberRole.Editor, AddedAt = now },
            new WorkspaceMember { WorkspaceId = campaign.Id, UserId = users[4].Id, MemberRole = WorkspaceMemberRole.Editor, AddedAt = now },
            new WorkspaceMember { WorkspaceId = campaign.Id, UserId = users[2].Id, MemberRole = WorkspaceMemberRole.Approver, AddedAt = now },
        ]);
        db.Workspaces.AddRange(org, campaign);
        await db.SaveChangesAsync(ct);

        var topic = await db.CatalogItems.Where(i => i.Catalog!.Code == "chu_de").OrderBy(i => i.SortOrder).Select(i => i.Id).FirstAsync(ct);
        var camp = await db.CatalogItems.Where(i => i.Catalog!.Code == "chien_dich").OrderBy(i => i.SortOrder).Select(i => i.Id).FirstAsync(ct);
        var images = Folder(campaign, null, "Hình ảnh");
        var videos = Folder(campaign, null, "Video - Âm thanh");
        var docs = Folder(org, null, "Văn bản, kế hoạch");
        var publications = Folder(org, null, "Ấn phẩm điện tử");
        await db.SaveChangesAsync(ct);

        await AddAsync(campaign, images, "Áp phích Ngày Dân số Việt Nam.jpg", Poster("NGÀY DÂN SỐ VIỆT NAM 26/12", new SKColor(11, 83, 148)), users[0], [camp, topic], ["áp phích", "dân số"], ct);
        await AddAsync(campaign, images, "Infographic Già hóa dân số.png", Poster("GIÀ HÓA DÂN SỐ", new SKColor(0, 128, 96), SKEncodedImageFormat.Png), users[4], [topic], ["infographic"], ct);
        await AddAsync(campaign, videos, "Video tuyên truyền mẫu.mp4", Resource("video-mau.mp4"), users[1], [camp], ["video"], ct);
        await AddAsync(campaign, videos, "Spot phát thanh mẫu.mp3", Resource("am-thanh-mau.mp3"), users[1], [camp], ["âm thanh"], ct);
        await AddAsync(org, docs, "Kế hoạch truyền thông dân số năm 2026.docx", office.BlankTemplate("docx")!, users[2], [topic], ["kế hoạch"], ct);
        await AddAsync(org, docs, "Bảng theo dõi sản phẩm truyền thông.xlsx", office.BlankTemplate("xlsx")!, users[0], [], ["thống kê"], ct);
        await AddAsync(org, docs, "Bài trình bày tập huấn.pptx", office.BlankTemplate("pptx")!, users[0], [topic], ["tập huấn"], ct);
        await AddAsync(org, publications, "Bản tin Dân số và Phát triển.pdf", Pdf(), users[2], [topic], ["bản tin", "ấn phẩm"], ct);
        await AddAsync(org, docs, "Ghi chú họp giao ban.txt", "Ghi chú họp giao ban tuần: rà soát tiến độ sản phẩm truyền thông về dân số.\n"u8.ToArray(), users[0], [], [], ct);
        foreach (var u in users.Where(u => u.Username is "canbo1" or "canbo5"))
        {
            var personal = await db.Workspaces.FirstAsync(w => w.OwnerId == u.Id && w.Type == WorkspaceType.Personal, ct);
            await AddAsync(personal, null, $"Ảnh hoạt động - {u.FullName}.jpg", Poster(u.FullName.ToUpperInvariant(), new SKColor(120, 60, 160)), u, [], ["cá nhân"], ct);
        }
        logger.LogInformation("Đã nạp dữ liệu mẫu: 5 người dùng (mật khẩu {Password}), 2 nhóm tài nguyên và tệp mẫu", DemoPassword);
    }

    private Folder Folder(Workspace ws, Folder? parent, string name)
    {
        var f = new Folder { WorkspaceId = ws.Id, ParentId = parent?.Id, Name = name };
        f.Path = StorageNames.FolderPath(parent, f.Id);
        db.Folders.Add(f);
        return f;
    }

    private async Task AddAsync(Workspace ws, Folder? folder, string name, byte[] bytes, User owner, Guid[] catalogItems, string[] tags, CancellationToken ct)
    {
        StoredContent stored;
        await using (var ms = new MemoryStream(bytes)) stored = await content.SaveAsync(ms, "files", ct);
        var info = types.FromName(name);
        var file = new StoredFile
        {
            WorkspaceId = ws.Id, FolderId = folder?.Id, Name = name, Extension = info.Extension, MimeType = info.MimeType, MediaType = info.MediaType,
            SizeBytes = stored.SizeBytes, ChecksumSha256 = stored.Sha256, Tags = [.. tags], ApprovalStatus = ApprovalStatus.Approved, CreatedBy = owner.Id,
            UploaderOrganizationId = owner.OrganizationId, Title = Path.GetFileNameWithoutExtension(name),
        };
        var v = new FileVersion { FileId = file.Id, VersionNo = 1, StorageKey = stored.StorageKey, SizeBytes = stored.SizeBytes, Checksum = stored.Sha256,
            CreatedBy = owner.Id, CreatedAt = DateTime.UtcNow, ChangeNote = "Dữ liệu mẫu", IsEncrypted = stored.IsEncrypted, WrappedKey = stored.WrappedKey };
        file.CurrentVersionId = v.Id;
        file.Versions.Add(v);
        foreach (var c in catalogItems) file.CatalogItems.Add(new FileCatalogItem { FileId = file.Id, CatalogItemId = c });
        db.Files.Add(file);
        ws.UsedBytes += stored.SizeBytes;
        if (ws.Type == WorkspaceType.Personal) owner.UsedBytes += stored.SizeBytes;
        await db.SaveChangesAsync(ct);
        jobs.EnqueueFileProcessing(file.Id, v.Id);
    }

    private static byte[] Poster(string text, SKColor color, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bmp = new SKBitmap(1280, 720);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(color);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            PdfFonts.EnsureRegistered();
            using var stream = typeof(PdfFonts).Assembly.GetManifestResourceStream(typeof(PdfFonts).Assembly.GetManifestResourceNames().First(n => n.EndsWith("BeVietnamPro-Bold.ttf")));
            using var typeface = SKTypeface.FromStream(stream);
            using var font = new SKFont(typeface, 64);
            canvas.DrawText(text, 640, 380, SKTextAlign.Center, font, paint);
            using var small = new SKFont(typeface, 28);
            canvas.DrawText("Cục Dân số - Bộ Y tế", 640, 450, SKTextAlign.Center, small, paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        return image.Encode(format, 85).ToArray();
    }

    private static byte[] Pdf()
    {
        PdfFonts.EnsureRegistered();
        return Document.Create(d => d.Page(p =>
        {
            p.Margin(40);
            p.DefaultTextStyle(t => t.FontFamily(TableExporter.FontFamily).FontSize(12));
            p.Header().Text("BẢN TIN DÂN SỐ VÀ PHÁT TRIỂN").Bold().FontSize(20).FontColor("#0b5394");
            p.Content().PaddingTop(20).Column(c =>
            {
                c.Item().Text("Số đặc biệt chào mừng Ngày Dân số Việt Nam 26/12.").Italic();
                c.Item().PaddingTop(10).Text("Công tác dân số chuyển trọng tâm từ kế hoạch hóa gia đình sang dân số và phát triển: duy trì mức sinh thay thế, giảm mất cân bằng giới tính khi sinh, thích ứng với già hóa dân số và nâng cao chất lượng dân số.");
            });
            p.Footer().AlignCenter().Text("Tài liệu mẫu - BlueDrive");
        })).GeneratePdf();
    }

    private static byte[] Resource(string name)
    {
        var asm = typeof(DemoDataSeeder).Assembly;
        using var s = asm.GetManifestResourceStream(asm.GetManifestResourceNames().First(n => n.EndsWith(name)))!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
