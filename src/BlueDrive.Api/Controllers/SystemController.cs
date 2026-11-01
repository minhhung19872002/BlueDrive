using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace BlueDrive.Api.Controllers;

/// <summary>Thông tin phiên bản hệ thống.</summary>
[ApiController]
[Route("api/v1/system")]
public class SystemController : ControllerBase
{
    /// <summary>Lấy phiên bản đang chạy (assembly version + git sha).</summary>
    [HttpGet("info")]
    public IActionResult Info()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        var parts = info.Split('+', 2);
        return Ok(new
        {
            name = "BlueDrive",
            version = parts[0],
            gitSha = parts.Length > 1 ? parts[1] : Environment.GetEnvironmentVariable("GIT_SHA") ?? "dev",
            environment = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().EnvironmentName,
            serverTimeUtc = DateTime.UtcNow,
        });
    }
}
