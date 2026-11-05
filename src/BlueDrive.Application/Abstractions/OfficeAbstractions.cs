namespace BlueDrive.Application.Abstractions;

public sealed record OfficeEditorRequest(
    Guid FileId, Guid VersionId, string FileName, string Extension, string DocumentUrl, string? CallbackUrl,
    bool CanEdit, bool CanDownload, Guid? UserId, string UserName, bool Embedded, bool Mobile = false, string? GuestId = null,
    bool CanComment = false, bool CanPrint = true, bool CanCopy = true);

/// <summary>What the browser needs to start the editor: script URL + a signed config object.</summary>
/// <param name="Watermark">Spec 11.9: the page lays the viewer's name, account and time over the editor.</param>
public sealed record OfficeEditorConfig(string Provider, string ApiScriptUrl, object Config, string Mode, bool Watermark = false);

public sealed record OfficeCallback(int Status, string? DownloadUrl, IReadOnlyList<string> Users, string? Key);

/// <summary>Online office editor abstraction (ONLYOFFICE today, Collabora possible later).</summary>
public interface IOfficeEditorProvider
{
    bool CanEdit(string extension);
    bool CanView(string extension);
    OfficeEditorConfig BuildConfig(OfficeEditorRequest request);
    /// <summary>Validates the provider signature and parses the save callback; null when invalid.</summary>
    OfficeCallback? ParseCallback(string body, string? authorizationHeader);
    Task<Stream> DownloadAsync(string url, CancellationToken ct);
    /// <summary>First page rendered as PNG via the provider's conversion service; null when unsupported.</summary>
    Task<byte[]?> RenderThumbnailAsync(string sourceUrl, string extension, string key, CancellationToken ct);
    byte[]? BlankTemplate(string extension);
    /// <summary>Base URL the provider uses to reach the API (for document and callback URLs).</summary>
    string CallbackBaseUrl { get; }
}
