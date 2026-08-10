using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;

namespace TsuOrg.Application.Features.Documents.Queries;

// ─── Query / Result ──────────────────────────────────────────────────────────

public sealed record GetDocumentDownloadQuery(Guid DocumentId, Guid? VersionId = null);

public sealed record DocumentDownloadDto(string FileName, string SasUrl, string ContentType);

// ─── Handler ─────────────────────────────────────────────────────────────────

public sealed class GetDocumentDownloadHandler
{
    private readonly IApplicationDbContext _db;
    private readonly IBlobService          _blob;

    public GetDocumentDownloadHandler(IApplicationDbContext db, IBlobService blob)
    {
        _db   = db;
        _blob = blob;
    }

    public async Task<DocumentDownloadDto> HandleAsync(
        GetDocumentDownloadQuery q, CancellationToken ct = default)
    {
        // If a version is requested, resolve that blob path
        if (q.VersionId.HasValue)
        {
            var version = await _db.DocumentVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == q.VersionId.Value && v.DocumentId == q.DocumentId, ct)
                ?? throw new NotFoundException("DocumentVersion", q.VersionId.Value);

            var vSas = await _blob.GetReadUrlAsync(version.BlobPath, TimeSpan.FromMinutes(15), ct);
            return new DocumentDownloadDto(
                System.IO.Path.GetFileName(version.BlobPath),
                vSas,
                InferContentType(version.BlobPath));
        }

        // Otherwise return the primary file of the latest live document
        var doc = await _db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == q.DocumentId, ct)
            ?? throw new NotFoundException("Document", q.DocumentId);

        if (string.IsNullOrEmpty(doc.PrimaryFileBlobPath))
            throw new BusinessRuleException("Primary file has not been uploaded yet.");

        var sas = await _blob.GetReadUrlAsync(doc.PrimaryFileBlobPath, TimeSpan.FromMinutes(15), ct);
        return new DocumentDownloadDto(
            System.IO.Path.GetFileName(doc.PrimaryFileBlobPath),
            sas,
            InferContentType(doc.PrimaryFileBlobPath));
    }

    private static string InferContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf"  => "application/pdf",
            ".jpg"  => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png"  => "image/png",
            ".tiff" => "image/tiff",
            _       => "application/octet-stream",
        };
    }
}
