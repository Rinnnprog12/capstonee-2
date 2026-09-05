using TsuOrg.Application.Common;

namespace TsuOrg.Infrastructure.Services;

/// <summary>
/// Stores document bytes on disk so local uploads work without Azurite.
/// </summary>
public sealed class LocalBlobService : IBlobService
{
    private readonly LocalBlobAccess _access;

    public LocalBlobService(LocalBlobAccess access) => _access = access;

    public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var blobName = BuildBlobName(fileName);
        var physical = _access.ResolvePhysicalPath(blobName);
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);

        await using (var fs = new FileStream(physical, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await content.CopyToAsync(fs, ct);
        }

        await File.WriteAllTextAsync(physical + ".contenttype", contentType ?? "application/octet-stream", ct);
        return blobName;
    }

    public Task<string> GetReadUrlAsync(string blobPath, TimeSpan expiry, CancellationToken ct = default)
    {
        _ = _access.ResolvePhysicalPath(blobPath);
        return Task.FromResult(_access.CreateReadUrl(blobPath, expiry));
    }

    public Task DeleteAsync(string blobPath, CancellationToken ct = default)
    {
        var physical = _access.ResolvePhysicalPath(blobPath);
        if (File.Exists(physical))
            File.Delete(physical);
        if (File.Exists(physical + ".contenttype"))
            File.Delete(physical + ".contenttype");
        return Task.CompletedTask;
    }

    public async Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default)
    {
        var (content, _) = await DownloadWithContentTypeAsync(blobPath, ct);
        return content;
    }

    public async Task<(Stream Content, string ContentType)> DownloadWithContentTypeAsync(
        string blobPath, CancellationToken ct = default)
    {
        var physical = _access.ResolvePhysicalPath(blobPath);
        if (!File.Exists(physical))
            throw new FileNotFoundException("Blob not found.", blobPath);

        var contentType = "application/octet-stream";
        var meta = physical + ".contenttype";
        if (File.Exists(meta))
            contentType = (await File.ReadAllTextAsync(meta, ct)).Trim();

        Stream stream = new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return (stream, string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
    }

    private static string BuildBlobName(string fileName)
    {
        var now = DateTime.UtcNow;
        var safe = Path.GetFileName(fileName)
            .Replace(" ", "_")
            .Replace("..", "");
        return $"{now.Year}/{now.Month:D2}/{Guid.NewGuid():N}/{safe}";
    }
}
