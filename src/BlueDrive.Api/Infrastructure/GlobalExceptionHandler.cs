using BlueDrive.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BlueDrive.Api.Infrastructure;

/// <summary>Maps exceptions to ProblemDetails with Vietnamese titles; never leaks stack traces in production.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (ex)
        {
            case AppException app:
                problem = ProblemFactory.Create(app.StatusCode, app.Message, app.Errors);
                break;
            case OperationCanceledException when ctx.RequestAborted.IsCancellationRequested:
                ctx.Response.StatusCode = 499;
                return true;
            case Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException:
                problem = ProblemFactory.Create(409, "Dữ liệu đã bị thay đổi bởi người khác. Vui lòng tải lại và thử lại.");
                break;
            case BadHttpRequestException bad:
                problem = ProblemFactory.Create(bad.StatusCode, "Yêu cầu không hợp lệ.");
                break;
            default:
                logger.LogError(ex, "Lỗi không xử lý được khi gọi {Path}", ctx.Request.Path);
                problem = ProblemFactory.Create(500, "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau hoặc liên hệ quản trị viên.");
                if (env.IsDevelopment() || env.IsEnvironment("Testing")) problem.Extensions["debug"] = ex.ToString();
                break;
        }
        problem.Extensions["traceId"] = ctx.TraceIdentifier;
        ctx.Response.StatusCode = problem.Status ?? 500;
        await ctx.Response.WriteAsJsonAsync(problem, problem.GetType(), (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ct);
        return true;
    }
}

public static class ProblemFactory
{
    private static readonly Dictionary<int, string> Titles = new()
    {
        [400] = "Dữ liệu không hợp lệ",
        [401] = "Chưa xác thực",
        [403] = "Không có quyền truy cập",
        [404] = "Không tìm thấy",
        [409] = "Xung đột dữ liệu",
        [413] = "Dữ liệu quá lớn",
        [423] = "Tài nguyên đang bị khóa",
        [429] = "Quá nhiều yêu cầu",
        [500] = "Lỗi hệ thống",
    };

    public static ProblemDetails Create(int status, string detail, IDictionary<string, string[]>? errors = null)
    {
        var p = errors is { Count: > 0 } ? new ValidationProblemDetails(errors) : new ProblemDetails();
        // Surface the concrete messages so clients can show them directly.
        if (errors is { Count: > 0 } && status == 400) detail = string.Join(" ", errors.Values.SelectMany(v => v).Distinct());
        p.Status = status;
        p.Title = Titles.GetValueOrDefault(status, "Lỗi");
        p.Detail = detail;
        return p;
    }

    public static IActionResult FromModelState(ActionContext ctx)
    {
        var errors = ctx.ModelState.Where(kv => kv.Value?.Errors.Count > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value!.Errors.Select(e => Translate(e, kv.Key)).ToArray());
        return new BadRequestObjectResult(Create(400, "Dữ liệu nhập không hợp lệ.", errors)) { ContentTypes = { "application/problem+json" } };
    }

    private static string Translate(ModelError e, string key) =>
        string.IsNullOrEmpty(e.ErrorMessage) || e.ErrorMessage.Contains("JSON", StringComparison.OrdinalIgnoreCase) || e.ErrorMessage.Contains("required", StringComparison.OrdinalIgnoreCase)
            ? $"Giá trị của trường '{key}' không hợp lệ hoặc bị thiếu."
            : e.ErrorMessage;
}
