namespace BlueDrive.Api.Infrastructure;

/// <summary>Defensive headers on every API response (the SPA/nginx and Caddy add their own for static content).</summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseBlueDriveSecurityHeaders(this IApplicationBuilder app) => app.Use((ctx, next) =>
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["X-Frame-Options"] = "SAMEORIGIN";
            h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            // JSON responses never need to load anything; content streams set their own (sandboxed) CSP.
            if (!h.ContainsKey("Content-Security-Policy") && ctx.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h.Remove("Server");
            h.Remove("X-Powered-By");
            return Task.CompletedTask;
        });
        return next(ctx);
    });
}
