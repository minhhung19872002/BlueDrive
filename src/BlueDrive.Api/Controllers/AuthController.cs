using BlueDrive.Api.Infrastructure;
using BlueDrive.Application.Features.Auth;
using BlueDrive.Application.Features.Settings;
using BlueDrive.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BlueDrive.Api.Controllers;

/// <summary>Xác thực: đăng nhập, làm mới token, đăng xuất, đổi/khôi phục mật khẩu (HSMT #16).</summary>
[Route("api/v1/auth")]
public class AuthController : ApiControllerBase
{
    /// <summary>Đăng nhập bằng tên đăng nhập/mật khẩu.</summary>
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting(SecuritySetup.AuthRateLimit)]
    public Task<AuthResult> Login(LoginCommand cmd) => Mediator.Send(cmd);

    /// <summary>Làm mới access token (refresh token xoay vòng, dùng một lần).</summary>
    [HttpPost("refresh"), AllowAnonymous, EnableRateLimiting(SecuritySetup.RefreshRateLimit)]
    public Task<AuthResult> Refresh(RefreshCommand cmd) => Mediator.Send(cmd);

    /// <summary>Đăng xuất phiên hiện tại.</summary>
    [HttpPost("logout"), AllowWhenPasswordChangeRequired]
    public async Task<IActionResult> Logout()
    {
        await Mediator.Send(new LogoutCommand());
        return NoContent();
    }

    /// <summary>Thông tin tài khoản đang đăng nhập và danh sách quyền.</summary>
    [HttpGet("me"), AllowWhenPasswordChangeRequired]
    public Task<UserProfileDto> Me() => Mediator.Send(new GetMeQuery());

    /// <summary>Cập nhật hồ sơ cá nhân.</summary>
    [HttpPut("me")]
    public async Task<IActionResult> UpdateProfile(UpdateProfileCommand cmd)
    {
        await Mediator.Send(cmd);
        return NoContent();
    }

    /// <summary>Đổi mật khẩu (bắt buộc ở lần đăng nhập đầu hoặc khi mật khẩu hết hạn).</summary>
    [HttpPost("change-password"), AllowWhenPasswordChangeRequired]
    public Task<AuthResult> ChangePassword(ChangePasswordCommand cmd) => Mediator.Send(cmd);

    /// <summary>Quên mật khẩu: gửi email chứa liên kết khôi phục (hết hạn sau 30 phút).</summary>
    [HttpPost("forgot-password"), AllowAnonymous, EnableRateLimiting(SecuritySetup.AuthRateLimit)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordCommand cmd)
    {
        await Mediator.Send(cmd);
        return Ok(new { message = "Nếu tài khoản tồn tại và có email, hệ thống đã gửi hướng dẫn khôi phục mật khẩu." });
    }

    /// <summary>Đặt lại mật khẩu bằng token trong email.</summary>
    [HttpPost("reset-password"), AllowAnonymous, EnableRateLimiting(SecuritySetup.AuthRateLimit)]
    public async Task<IActionResult> ResetPassword(ResetPasswordCommand cmd)
    {
        await Mediator.Send(cmd);
        return Ok(new { message = "Đặt lại mật khẩu thành công. Vui lòng đăng nhập bằng mật khẩu mới." });
    }

    /// <summary>Chính sách mật khẩu hiện hành.</summary>
    [HttpGet("password-policy"), AllowAnonymous]
    public Task<PasswordPolicyDto> PasswordPolicy() => Mediator.Send(new GetPasswordPolicyQuery());

    /// <summary>Thông tin hiển thị (tên phần mềm, đơn vị, logo).</summary>
    [HttpGet("branding"), AllowAnonymous]
    public Task<Dictionary<string, string>> Branding() => Mediator.Send(new GetBrandingQuery());
}

/// <summary>Phiên đăng nhập (HSMT #17).</summary>
[Route("api/v1/sessions")]
public class SessionsController : ApiControllerBase
{
    /// <summary>Danh sách phiên của tôi.</summary>
    [HttpGet("mine")]
    public Task<IReadOnlyList<SessionDto>> Mine([FromQuery] bool includeRevoked = false) => Mediator.Send(new ListMySessionsQuery(includeRevoked));

    /// <summary>Đăng xuất mọi thiết bị khác của tôi.</summary>
    [HttpPost("mine/revoke-others")]
    public async Task<IActionResult> RevokeOthers() => Ok(new { revoked = await Mediator.Send(new RevokeMyOtherSessionsCommand()) });

    /// <summary>Toàn bộ phiên trên hệ thống (quản trị).</summary>
    [HttpGet, HasPermission(Perm.SessionView)]
    public Task<Application.Common.PagedResult<SessionDto>> All([FromQuery] ListAllSessionsQuery query) => Mediator.Send(query);

    /// <summary>Thu hồi một phiên (của mình, hoặc của người khác nếu có quyền quản trị phiên).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        await Mediator.Send(new RevokeSessionCommand(id));
        return NoContent();
    }

    /// <summary>Thu hồi toàn bộ quyền truy cập của một người dùng.</summary>
    [HttpPost("users/{userId:guid}/revoke-all"), HasPermission(Perm.SessionAdmin)]
    public async Task<IActionResult> RevokeAll(Guid userId) => Ok(new { revoked = await Mediator.Send(new RevokeUserAccessCommand(userId)) });
}
