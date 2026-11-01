using System.Security.Claims;
using System.Threading.RateLimiting;
using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Security;
using BlueDrive.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BlueDrive.Api.Infrastructure;

public static class SecuritySetup
{
    public const string AuthRateLimit = "auth";
    public const string PublicRateLimit = "public";

    public static IServiceCollection AddBlueDriveSecurity(this IServiceCollection services, IConfiguration config)
    {
        var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = jwt.SigningKey(),
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "preferred_username",
            };
            o.Events = new JwtBearerEvents
            {
                // WebSockets cannot send headers: accept ?access_token= for SignalR only (media uses signed short-lived URLs).
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"].ToString();
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        ctx.Token = token;
                    return Task.CompletedTask;
                },
                OnTokenValidated = async ctx =>
                {
                    var p = ctx.Principal!;
                    if (!Guid.TryParse(p.FindFirstValue("sub"), out var uid) || !Guid.TryParse(p.FindFirstValue("sid"), out var sid)
                        || !int.TryParse(p.FindFirstValue("stamp"), out var stamp))
                    {
                        ctx.Fail("Token không hợp lệ");
                        return;
                    }
                    var validator = ctx.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
                    if (!await validator.IsValidAsync(uid, sid, stamp, ctx.HttpContext.RequestAborted)) ctx.Fail("Phiên đăng nhập đã bị thu hồi hoặc hết hạn");
                },
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(ProblemFactory.Create(401, "Phiên đăng nhập không hợp lệ hoặc đã hết hạn. Vui lòng đăng nhập lại."),
                        (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
                },
                OnForbidden = async ctx =>
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(ProblemFactory.Create(403, "Bạn không có quyền thực hiện chức năng này."),
                        (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
                },
            };
        });
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        services.AddAuthorization();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.ContentType = "application/problem+json";
                await ctx.HttpContext.Response.WriteAsJsonAsync(ProblemFactory.Create(429, "Bạn thao tác quá nhanh. Vui lòng thử lại sau ít phút."), ct);
            };
            var disabled = config.GetValue("RateLimit:Disabled", false);
            o.AddPolicy(AuthRateLimit, ctx => disabled ? RateLimitPartition.GetNoLimiter("off")
                : RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:AuthPerMinute", 30), Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy(PublicRateLimit, ctx => disabled ? RateLimitPartition.GetNoLimiter("off")
                : RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:PublicPerMinute", 120), Window = TimeSpan.FromMinutes(1) }));
        });

        services.Configure<MvcOptions>(o => o.Filters.Add<PasswordChangeRequiredFilter>());
        return services;
    }
}

/// <summary>[HasPermission(Perm.OrgView)] → policy "perm:org.view" resolved dynamically from the permissions table.</summary>
public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute($"{PermissionPolicyProvider.Prefix}{permission}");

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal)) return await base.GetPolicyAsync(policyName);
        var perms = policyName[Prefix.Length..].Split('|');
        return new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser()
            .AddRequirements(perms.Select(p => new PermissionRequirement(p)).ToArray()).Build();
    }
}

public sealed class PermissionHandler(IPermissionService perms) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (Guid.TryParse(context.User.FindFirstValue("sub"), out var uid) && await perms.HasAsync(uid, requirement.Permission))
            context.Succeed(requirement);
    }
}

/// <summary>Marks endpoints usable while a password change is pending (first login / expired password).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhenPasswordChangeRequiredAttribute : Attribute;

/// <summary>Server-side enforcement of "bắt buộc đổi mật khẩu lần đầu".</summary>
public sealed class PasswordChangeRequiredFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var mustChange = ctx.HttpContext.User.FindFirstValue("pwd_change") == "1";
        var allowed = ctx.ActionDescriptor.EndpointMetadata.OfType<AllowWhenPasswordChangeRequiredAttribute>().Any()
                      || ctx.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any();
        if (mustChange && !allowed)
        {
            ctx.Result = new ObjectResult(ProblemFactory.Create(403, "Bạn cần đổi mật khẩu trước khi tiếp tục sử dụng hệ thống.")) { StatusCode = 403 };
            return Task.CompletedTask;
        }
        return next();
    }
}
