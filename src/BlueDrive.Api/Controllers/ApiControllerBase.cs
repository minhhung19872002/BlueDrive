using System.Security.Claims;
using BlueDrive.Application.Reporting;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueDrive.Api.Controllers;

[ApiController]
[Authorize]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    private ISender? _sender;
    protected ISender Mediator => _sender ??= HttpContext.RequestServices.GetRequiredService<ISender>();

    /// <summary>Display name used on exported reports ("Người xuất").</summary>
    protected string ExportedBy => User.FindFirstValue("name") ?? User.Identity?.Name ?? "Hệ thống";

    protected FileContentResult FileResult(ExportedFile file) => File(file.Content, file.ContentType, file.FileName);
}
