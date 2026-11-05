using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlueDrive.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BlueDrive.Infrastructure.Office;

public sealed class OnlyOfficeOptions
{
    /// <summary>Browser-facing Document Server URL (e.g. https://drive.example.gov.vn/office).</summary>
    public string PublicUrl { get; set; } = "http://localhost/office";
    /// <summary>Document Server URL reachable from api/worker containers (e.g. http://onlyoffice).</summary>
    public string InternalUrl { get; set; } = "http://onlyoffice";
    /// <summary>API URL reachable from the Document Server (e.g. http://api:8080).</summary>
    public string CallbackBaseUrl { get; set; } = "http://api:8080";
    public string JwtSecret { get; set; } = "";
}

/// <summary>ONLYOFFICE Document Server integration (Document Editor API + Conversion API), JWT-signed both ways.</summary>
public sealed class OnlyOfficeProvider(IOptions<OnlyOfficeOptions> options, IHttpClientFactory http, ILogger<OnlyOfficeProvider> logger) : IOfficeEditorProvider
{
    private readonly OnlyOfficeOptions _o = options.Value;
    private static readonly HashSet<string> Editable = new(StringComparer.OrdinalIgnoreCase) { "docx", "xlsx", "pptx", "txt", "odt", "ods", "odp", "csv", "rtf" };
    private static readonly HashSet<string> Viewable = new(StringComparer.OrdinalIgnoreCase) { "doc", "xls", "ppt", "pdf", "epub", "docx", "xlsx", "pptx", "txt", "odt", "ods", "odp", "csv", "rtf", "docm", "xlsm", "pptm" };

    public string CallbackBaseUrl => _o.CallbackBaseUrl.TrimEnd('/');
    public bool CanEdit(string extension) => Editable.Contains(extension);
    public bool CanView(string extension) => Viewable.Contains(extension);

    private static string DocumentType(string ext) => ext.ToLowerInvariant() switch
    {
        "xlsx" or "xlsm" or "xls" or "ods" or "csv" => "cell",
        "pptx" or "pptm" or "ppt" or "odp" => "slide",
        "pdf" => "pdf",
        _ => "word",
    };

    private SigningCredentials Credentials()
    {
        if (Encoding.UTF8.GetByteCount(_o.JwtSecret) < 32) throw new InvalidOperationException("OnlyOffice:JwtSecret (ONLYOFFICE_JWT_SECRET) phải có tối thiểu 32 ký tự.");
        return new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.JwtSecret)), SecurityAlgorithms.HmacSha256);
    }

    private string Sign(JsonObject payload)
        => new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(payload.ToJsonString(), Credentials());

    public OfficeEditorConfig BuildConfig(OfficeEditorRequest r)
    {
        var edit = r.CanEdit && CanEdit(r.Extension);
        // Spec 11.8: "Người nhận xét" opens the document to comment only (no changes to the content).
        var comment = !edit && r.CanComment && CanEdit(r.Extension);
        var config = new JsonObject
        {
            ["documentType"] = DocumentType(r.Extension),
            ["type"] = r.Mobile ? "mobile" : r.Embedded ? "embedded" : "desktop",
            ["width"] = "100%",
            ["height"] = "100%",
            ["document"] = new JsonObject
            {
                ["fileType"] = r.Extension.ToLowerInvariant(),
                // Same key for everyone on the same version → real-time co-editing; a new version gets a new key.
                ["key"] = $"{r.FileId:N}-{r.VersionId:N}",
                ["title"] = r.FileName,
                ["url"] = r.DocumentUrl,
                ["permissions"] = new JsonObject
                {
                    ["edit"] = edit,
                    ["download"] = r.CanDownload,
                    ["print"] = r.CanDownload && r.CanPrint,
                    ["copy"] = r.CanCopy,
                    ["comment"] = edit || comment,
                    ["review"] = edit,
                },
            },
            ["editorConfig"] = new JsonObject
            {
                ["mode"] = edit || comment ? "edit" : "view",
                ["lang"] = "vi",
                ["region"] = "vi-VN",
                ["user"] = new JsonObject { ["id"] = r.GuestId ?? r.UserId?.ToString() ?? "anonymous", ["name"] = r.UserName },
                ["customization"] = new JsonObject { ["forcesave"] = true, ["autosave"] = true, ["compactHeader"] = r.Embedded, ["hideRightMenu"] = false, ["uiTheme"] = "theme-light" },
            },
        };
        if ((edit || comment) && r.CallbackUrl is not null) config["editorConfig"]!["callbackUrl"] = r.CallbackUrl;
        config["token"] = Sign(JsonNode.Parse(config.ToJsonString())!.AsObject());
        return new OfficeEditorConfig("onlyoffice", $"{_o.PublicUrl.TrimEnd('/')}/web-apps/apps/api/documents/api.js",
            JsonSerializer.Deserialize<JsonElement>(config.ToJsonString()), edit ? "edit" : comment ? "comment" : "view");
    }

    public OfficeCallback? ParseCallback(string body, string? authorizationHeader)
    {
        try
        {
            var json = JsonNode.Parse(body)?.AsObject();
            var token = json?["token"]?.GetValue<string>() ?? authorizationHeader?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(token)) return null;
            var result = new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = false, RequireExpirationTime = false,
                IssuerSigningKey = Credentials().Key,
            }).GetAwaiter().GetResult();
            if (!result.IsValid) return null;
            // Header-token variant wraps the body under "payload".
            var payload = JsonNode.Parse(((JsonWebToken)result.SecurityToken).EncodedPayload is { } p ? Base64UrlEncoder.Decode(p) : "{}")!.AsObject();
            if (payload["payload"] is JsonObject inner) payload = inner;
            var users = payload["users"]?.AsArray().Select(u => u!.GetValue<string>()).ToList() ?? [];
            return new OfficeCallback(payload["status"]?.GetValue<int>() ?? 0, payload["url"]?.GetValue<string>(), users, payload["key"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(ex, "Callback ONLYOFFICE không hợp lệ");
            return null;
        }
    }

    /// <summary>Document Server returns public URLs; rewrite them to the internal address before downloading.</summary>
    private string Internalize(string url)
    {
        var pub = _o.PublicUrl.TrimEnd('/');
        if (url.StartsWith(pub, StringComparison.OrdinalIgnoreCase)) return _o.InternalUrl.TrimEnd('/') + url[pub.Length..];
        var u = new Uri(url);
        return $"{_o.InternalUrl.TrimEnd('/')}{u.PathAndQuery}";
    }

    public async Task<Stream> DownloadAsync(string url, CancellationToken ct)
    {
        var client = http.CreateClient("onlyoffice");
        var res = await client.GetAsync(Internalize(url), HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStreamAsync(ct);
    }

    public async Task<byte[]?> RenderThumbnailAsync(string sourceUrl, string extension, string key, CancellationToken ct)
    {
        if (!CanView(extension)) return null;
        var body = new JsonObject
        {
            ["async"] = false,
            ["filetype"] = extension.ToLowerInvariant(),
            ["key"] = key,
            ["outputtype"] = "png",
            ["thumbnail"] = new JsonObject { ["aspect"] = 1, ["first"] = true, ["width"] = 640, ["height"] = 640 },
            ["url"] = sourceUrl,
        };
        body["token"] = Sign(JsonNode.Parse(body.ToJsonString())!.AsObject());
        var client = http.CreateClient("onlyoffice");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_o.InternalUrl.TrimEnd('/')}/converter") { Content = JsonContent.Create(body) };
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        var fileUrl = json?["fileUrl"]?.GetValue<string>();
        if (string.IsNullOrEmpty(fileUrl))
        {
            logger.LogWarning("ONLYOFFICE không tạo được ảnh xem trước: {Body}", json?.ToJsonString());
            return null;
        }
        await using var s = await DownloadAsync(fileUrl, ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public byte[]? BlankTemplate(string extension)
    {
        var name = typeof(OnlyOfficeProvider).Assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($"blank.{extension.ToLowerInvariant()}", StringComparison.OrdinalIgnoreCase));
        if (name is null) return extension.Equals("txt", StringComparison.OrdinalIgnoreCase) ? [0xEF, 0xBB, 0xBF] : null;
        using var s = typeof(OnlyOfficeProvider).Assembly.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
