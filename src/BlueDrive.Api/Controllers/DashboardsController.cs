using BlueDrive.Api.Infrastructure;
using BlueDrive.Application.Features.Dashboards;
using BlueDrive.Application.Security;
using Microsoft.AspNetCore.Mvc;

namespace BlueDrive.Api.Controllers;

/// <summary>Dashboard cá nhân (HSMT #63–66), nhóm (#67–70) và quản trị.</summary>
[Route("api/v1/dashboards")]
public class DashboardsController : ApiControllerBase
{
    [HttpGet("personal")]
    public Task<PersonalDashboardDto> Personal() => Mediator.Send(new GetPersonalDashboardQuery());

    [HttpGet("group")]
    public Task<GroupDashboardDto> Group() => Mediator.Send(new GetGroupDashboardQuery());

    [HttpGet("admin"), HasPermission(Perm.DashboardAdminView)]
    public Task<AdminDashboardDto> Admin() => Mediator.Send(new GetAdminDashboardQuery());
}
