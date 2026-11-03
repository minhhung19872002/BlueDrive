namespace BlueDrive.Application.Common;

/// <summary>Business error mapped to a ProblemDetails response with a Vietnamese message.</summary>
public class AppException(string message, int statusCode = 400, IDictionary<string, string[]>? errors = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public IDictionary<string, string[]>? Errors { get; } = errors;
}

public sealed class NotFoundException(string message = "Không tìm thấy dữ liệu yêu cầu.") : AppException(message, 404);

public sealed class ForbiddenException(string message = "Bạn không có quyền thực hiện thao tác này.") : AppException(message, 403);

public sealed class ConflictException(string message) : AppException(message, 409);

public sealed class UnauthorizedException(string message = "Phiên đăng nhập không hợp lệ hoặc đã hết hạn.") : AppException(message, 401);

public sealed class RequestValidationException(IDictionary<string, string[]> errors)
    : AppException("Dữ liệu nhập không hợp lệ.", 400, errors);

/// <summary>412: If-Match does not match the item's current ETag (desktop sync). <see cref="Current"/> is returned to the client.</summary>
public sealed class PreconditionFailedException(object? current) : AppException("Tài liệu đã thay đổi trên máy chủ kể từ lần đồng bộ trước.", 412)
{
    public object? Current { get; } = current;
}
