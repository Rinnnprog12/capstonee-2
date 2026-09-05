namespace TsuOrg.Application.Common;

/// <summary>
/// Abstracts Azure Blob Storage (or Azurite in dev).
/// All file bytes live in blob — DB stores only paths and metadata.
/// </summary>
public interface IBlobService
{
    /// <summary>Upload raw bytes; returns the blob path stored in DB.</summary>
    Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default);

    /// <summary>Generate a time-limited read URL (SAS) for the CALSV engine or downloads.</summary>
    Task<string> GetReadUrlAsync(string blobPath, TimeSpan expiry, CancellationToken ct = default);

    /// <summary>Delete a blob (e.g. on document version cleanup).</summary>
    Task DeleteAsync(string blobPath, CancellationToken ct = default);

    /// <summary>Download blob bytes.</summary>
    Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default);

    /// <summary>Download blob bytes with stored content type (for authenticated media proxies).</summary>
    Task<(Stream Content, string ContentType)> DownloadWithContentTypeAsync(
        string blobPath, CancellationToken ct = default);
}
