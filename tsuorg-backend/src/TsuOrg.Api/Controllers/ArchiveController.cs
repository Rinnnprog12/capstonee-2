using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Archive.Queries;
using TsuOrg.Application.Features.Documents.Queries;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Document Management &amp; Archiving (DMA) — Figure 13 repository + DMA-01 / DMA-02.
/// </summary>
[ApiController]
[Route("api/v1/archive")]
[Authorize]
public sealed class ArchiveController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly GetArchiveHandler _archive;
    private readonly GetDocumentDownloadHandler _download;

    public ArchiveController(
        IApplicationDbContext db,
        GetArchiveHandler archive,
        GetDocumentDownloadHandler download)
    {
        _db = db;
        _archive = archive;
        _download = download;
    }

    /// <summary>
    /// Paginated repository — Approved/Archived, role-scoped, filterable by org/type/event/AY.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? organizationId,
        [FromQuery] string? documentTypeCode,
        [FromQuery] string? eventKeyword,
        [FromQuery] Guid? academicYearId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _archive.HandleAsync(new GetArchiveQuery(
            GetUserId(),
            GetRole(),
            organizationId,
            documentTypeCode,
            eventKeyword,
            academicYearId,
            status,
            page,
            pageSize), ct);
        return Ok(result);
    }

    /// <summary>Folder counts by document type / status for repository category UX.</summary>
    [HttpGet("category-counts")]
    public async Task<IActionResult> CategoryCounts(
        [FromQuery] Guid? organizationId,
        [FromQuery] Guid? academicYearId,
        CancellationToken ct = default)
    {
        var result = await _archive.HandleCountsAsync(new GetArchiveCategoryCountsQuery(
            GetUserId(),
            GetRole(),
            organizationId,
            academicYearId), ct);
        return Ok(result);
    }

    /// <summary>All revision versions for an archived/approved document (newest first).</summary>
    [HttpGet("~/api/v1/documents/{documentId:guid}/versions")]
    public async Task<IActionResult> Versions(Guid documentId, CancellationToken ct = default)
    {
        try
        {
            await EnsureArchiveAccessAsync(documentId, ct);
        }
        catch (ForbiddenException)
        {
            return Forbid();
        }
        catch (NotFoundException)
        {
            return NotFound(Problem($"Document '{documentId}' not found."));
        }

        var versions = await _db.DocumentVersions
            .AsNoTracking()
            .Where(v => v.DocumentId == documentId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new
            {
                v.Id,
                v.VersionNumber,
                FileName = System.IO.Path.GetFileName(v.BlobPath),
                v.ChangeSummary,
                v.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(versions);
    }

    [HttpGet("~/api/v1/documents/{documentId:guid}/versions/{versionId:guid}/download")]
    public async Task<IActionResult> DownloadVersion(
        Guid documentId, Guid versionId, CancellationToken ct = default)
    {
        try
        {
            await EnsureArchiveAccessAsync(documentId, ct);
            var dto = await _download.HandleAsync(
                new GetDocumentDownloadQuery(documentId, versionId), ct);
            return Ok(dto);
        }
        catch (ForbiddenException)
        {
            return Forbid();
        }
        catch (Exception ex) when (ex is NotFoundException or BusinessRuleException)
        {
            return NotFound(Problem(ex.Message));
        }
    }

    [HttpGet("~/api/v1/documents/{documentId:guid}/download")]
    public async Task<IActionResult> Download(Guid documentId, CancellationToken ct = default)
    {
        try
        {
            var doc = await _db.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId, ct)
                ?? throw new NotFoundException("Document", documentId);

            await DocumentVisibility.EnsureCanDownloadDocumentAsync(
                _db, doc, GetUserId(), GetRole(), ct);

            var dto = await _download.HandleAsync(
                new GetDocumentDownloadQuery(documentId), ct);
            return Ok(dto);
        }
        catch (ForbiddenException)
        {
            return Forbid();
        }
        catch (Exception ex) when (ex is NotFoundException or BusinessRuleException)
        {
            return NotFound(Problem(ex.Message));
        }
    }

    [HttpGet("~/api/v1/documents/{documentId:guid}/attachments/{attachmentId:guid}/download")]
    public async Task<IActionResult> DownloadAttachment(
        Guid documentId, Guid attachmentId, CancellationToken ct = default)
    {
        try
        {
            var doc = await _db.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId, ct)
                ?? throw new NotFoundException("Document", documentId);

            await DocumentVisibility.EnsureCanDownloadDocumentAsync(
                _db, doc, GetUserId(), GetRole(), ct);

            var dto = await _download.HandleAsync(
                new GetDocumentDownloadQuery(documentId, AttachmentId: attachmentId), ct);
            return Ok(dto);
        }
        catch (ForbiddenException)
        {
            return Forbid();
        }
        catch (Exception ex) when (ex is NotFoundException or BusinessRuleException)
        {
            return NotFound(Problem(ex.Message));
        }
    }

    private async Task EnsureArchiveAccessAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new NotFoundException("Document", documentId);

        // Live review packages: allow download/version via general visibility.
        if (doc.Status is DocumentStatus.Archived or DocumentStatus.Approved)
        {
            await DocumentVisibility.EnsureCanViewArchiveDocumentAsync(
                _db, doc, GetUserId(), GetRole(), ct);
            return;
        }

        await DocumentVisibility.EnsureCanDownloadDocumentAsync(
            _db, doc, GetUserId(), GetRole(), ct);
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private string GetRole() =>
        User.FindFirstValue(ClaimTypes.Role) ?? "OrgOfficer";
}
