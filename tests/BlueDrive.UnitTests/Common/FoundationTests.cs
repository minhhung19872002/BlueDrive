using BlueDrive.Application.Common;
using BlueDrive.Application.Security;
using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;
using BlueDrive.Infrastructure.Auditing;
using BlueDrive.Infrastructure.Persistence;
using FluentAssertions;

namespace BlueDrive.UnitTests.Common;

public class FoundationTests
{
    [Theory]
    [InlineData("Dân số", "dan so")]
    [InlineData("ĐẠI HỘI Kế hoạch hóa gia đình", "dai hoi ke hoach hoa gia dinh")]
    [InlineData("", "")]
    public void Unaccent_strips_vietnamese_diacritics(string input, string expected)
        => TextUtil.Unaccent(input).Should().Be(expected);

    [Theory]
    [InlineData("Báo cáo Đầu năm.pdf", "Bao cao Dau nam.pdf")]
    [InlineData("menu-ngu-canh-631368.txt", "menu-ngu-canh-631368.txt")]
    [InlineData("a\"b\\c\td😀.zip", "a_b_c_d__.zip")]
    [InlineData("  ", "download")]
    public void Ascii_file_name_keeps_case_and_replaces_unsafe_characters(string input, string expected)
        => TextUtil.AsciiFileName(input).Should().Be(expected);

    [Theory]
    [InlineData(OrganizationType.PhongBan, "phong_ban")]
    [InlineData(TicketPriority.KhongKhanCap, "khong_khan_cap")]
    [InlineData(AuditAction.LoginFailed, "login_failed")]
    public void Enums_are_persisted_as_snake_case(Enum value, string expected)
        => EnumNames.ToSnake(value.ToString()).Should().Be(expected);

    [Fact]
    public void Snake_case_converter_round_trips()
    {
        var conv = new SnakeCaseEnumConverter<WorkspaceMemberRole>();
        var stored = (string)conv.ConvertToProvider(WorkspaceMemberRole.Approver)!;
        stored.Should().Be("approver");
        conv.ConvertFromProvider(stored).Should().Be(WorkspaceMemberRole.Approver);
    }

    [Fact]
    public void Permission_catalog_is_consistent()
    {
        Perm.All.Select(p => p.Code).Should().OnlyHaveUniqueItems();
        Perm.All.Select(p => p.Action).Distinct().Should().BeSubsetOf(Perm.Actions.All.Select(a => a.Code));
        Perm.DefaultRolePermissions[Perm.Roles.Admin].Should().HaveCount(Perm.All.Count);
        foreach (var codes in Perm.DefaultRolePermissions.Values)
            codes.Should().BeSubsetOf(Perm.All.Select(p => p.Code));
    }

    [Fact]
    public void Audit_hash_chain_is_deterministic_and_detects_tampering()
    {
        var log = new AuditLog
        {
            UserId = Guid.Parse("0199a000-0000-7000-8000-000000000001"),
            Username = "admin",
            Action = AuditAction.Upload,
            ResourceName = "Tờ rơi dân số.pdf",
            Detail = """{"b":1,"a":"x"}""",
            OccurredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PrevHash = "abc",
        };
        var h1 = AuditLogger.ComputeHash(log);
        // jsonb normalizes key order/whitespace - the hash must not change.
        log.Detail = """{ "a": "x", "b": 1 }""";
        AuditLogger.ComputeHash(log).Should().Be(h1);

        log.ResourceName = "Tờ rơi khác.pdf";
        AuditLogger.ComputeHash(log).Should().NotBe(h1);
    }
}
