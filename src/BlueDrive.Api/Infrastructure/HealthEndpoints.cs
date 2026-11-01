using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlueDrive.Api.Infrastructure;

public static class HealthEndpoints
{
    public static void MapBlueDriveHealthChecks(this WebApplication app)
    {
        var options = new HealthCheckOptions { ResponseWriter = WriteJson };
        app.MapHealthChecks("/health", options);
        app.MapHealthChecks("/api/health", options);
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    }

    private static Task WriteJson(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = e.Value.Duration.TotalMilliseconds,
                data = e.Value.Data,
            }),
        };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
