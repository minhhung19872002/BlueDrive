using BlueDrive.Domain.Entities;
using BlueDrive.Domain.Enums;

namespace BlueDrive.Application.Abstractions;

/// <summary>Result of storing a new blob (plaintext metrics + how it is stored).</summary>
public sealed record StoredContent(string StorageKey, long SizeBytes, string Sha256, bool IsEncrypted, string? WrappedKey);

/// <summary>
/// Content layer on top of <see cref="IFileStorage"/>: per-blob AES-256-GCM envelope encryption (when enabled),
/// checksums and seekable plaintext streams (HTTP Range works on encrypted files).
/// </summary>
public interface IFileContentService
{
    Task<StoredContent> SaveAsync(Stream plaintext, string keyPrefix, CancellationToken ct = default);
    /// <summary>Seekable plaintext stream with a known Length.</summary>
    Task<Stream> OpenReadAsync(FileVersion version, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string storageKey, bool isEncrypted, string? wrappedKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
    bool EncryptionEnabled { get; }
}

public sealed record FileTypeInfo(string Extension, string MimeType, MediaType MediaType);

/// <summary>Classifies files and verifies magic bytes against the claimed extension.</summary>
public interface IFileTypeInspector
{
    FileTypeInfo FromName(string fileName);
    /// <summary>Returns a Vietnamese error message when content does not match the extension or is executable; otherwise null.</summary>
    string? ValidateContent(string fileName, ReadOnlySpan<byte> header);
    bool IsBlockedExtension(string extension);
}

/// <summary>Malware scanner settings the upload path needs (the scan itself runs in the Worker).</summary>
public interface IMalwareScanPolicy
{
    bool Enabled { get; }
    /// <summary>Largest file the scanner accepts (ClamAV StreamMaxLength); larger files are not sent to it.</summary>
    long MaxScanBytes { get; }

    /// <summary>Status of a freshly stored version, before the Worker picks it up.</summary>
    ScanStatus InitialStatus(long sizeBytes) => !Enabled ? ScanStatus.Skipped : sizeBytes > MaxScanBytes ? ScanStatus.TooLarge : ScanStatus.Pending;
}

/// <summary>Background work dispatched to the Worker (thumbnails, scanning...).</summary>
public interface IBackgroundJobs
{
    void EnqueueFileProcessing(Guid fileId, Guid versionId);
}
