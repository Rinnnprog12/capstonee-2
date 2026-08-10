using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Documents.Queries;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Document Management &amp; Archiving (DMA) — DMA-01 / DMA-02.
/// - Centralized repository per organization (role-scoped)
/// - Version control for document revision
/// - Categorization by type / event / academic year
/// </summary>
[ApiController]
[Route("api/v1/archive")]
[Authorize]
public sealed class ArchiveController : ControllerBase
{
    private readonly IApplicationDbContext      _db;
    private readonly GetDocumentDownloadHandler _download;

    public ArchiveController(IApplicationDbContext db, GetDocumentDownloadHandler download)
    {
        _db       = db;
        _download = download;
    }

    /// <summary>
    /// Paginated archive — filter by organization, document type, event keyword,
    /// academic year, and status. Approved + Archived records only.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid?   organizationId,
        [FromQuery] string? documentTypeCode,
        [FromQuery] string? eventKeyword,
        [FromQuery] Guid?   academicYearId,
        [FromQuery] string? status,
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 20,
        CancellationToken   ct       = default)
    {
        page     = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var q = await ScopedArchiveQueryAsync(ct);

        if (organizationId.HasValue)
            q = q.Where(d => d.OrganizationId == organizationId.Value);

        if (!string.IsNullOrWhiteSpace(documentTypeCode))
            q = q.Where(d => d.DocumentType!.Code == documentTypeCode);

        if (!string.IsNullOrWhiteSpace(eventKeyword))
            q = q.Where(d => EF.Functions.Like(d.Title, $"%{eventKeyword}%"));

        if (academicYearId.HasValue)
            q = q.Where(d => d.AcademicYearId == academicYearId.Value);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DocumentStatus>(status, true, out var st)
            && st is DocumentStatus.Archived or DocumentStatus.Approved)
            q = q.Where(d => d.Status == st);

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(d => d.UpdatedAt ?? d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.DocumentNumber,
                d.Title,
                Organization = new { d.Organization!.Id, d.Organization.Name, d.Organization.Acronym },
                DocumentType = new { d.DocumentType!.Code, d.DocumentType.Name },
                AcademicYear = d.AcademicYear != null ? d.AcademicYear.Label : null,
                AcademicYearId = d.AcademicYearId,
                Status       = d.Status.ToString(),
                d.PrimaryFileName,
                HasPrimaryFile = !string.IsNullOrEmpty(d.PrimaryFileBlobPath),
                d.CreatedAt,
                d.UpdatedAt,
            })
            .ToListAsync(ct);

        return Ok(new { items, totalCount = total, page, pageSize });
    }

    /// <summary>
    /// Returns counts grouped by DocumentType for the archive filter UX.
    /// </summary>
    [HttpGet("category-counts")]
    public async Task<IActionResult> CategoryCounts(
        [FromQuery] Guid?  organizationId,
        [FromQuery] Guid?  academicYearId,
        CancellationToken  ct = default)
    {
        var q = await ScopedArchiveQueryAsync(ct);

        if (organizationId.HasValue) q = q.Where(d => d.OrganizationId == organizationId.Value);
        if (academicYearId.HasValue)  q = q.Where(d => d.AcademicYearId == academicYearId.Value);

        var byType = await q
            .GroupBy(d => new { d.DocumentType!.Code, d.DocumentType.Name })
            .Select(g => new { g.Key.Code, g.Key.Name, Count = g.Count() })
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var byStatus = await q
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
            .ToListAsync(ct);

        return Ok(new { byType, byStatus });
    }

    /// <summary>All revision versions for a document (newest first).</summary>
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
                FileName     = System.IO.Path.GetFileName(v.BlobPath),
                v.ChangeSummary,
                v.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(versions);
    }

    /// <summary>Download a specific version (returns SAS URL, 15-min TTL).</summary>
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

    /// <summary>
    /// Download the primary file for an archived/approved document (SAS URL, 15-min TTL).
    /// </summary>
    [HttpGet("~/api/v1/documents/{documentId:guid}/download")]
    public async Task<IActionResult> Download(Guid documentId, CancellationToken ct = default)
    {
        try
        {
            await EnsureArchiveAccessAsync(documentId, ct);
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

    private async Task<IQueryable<Domain.Entities.Document>> ScopedArchiveQueryAsync(CancellationToken ct)
    {
        var q = _db.Documents
            .AsNoTracking()
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .Include(d => d.AcademicYear)
            .Where(d => d.Status == DocumentStatus.Archived || d.Status == DocumentStatus.Approved);

        return await DocumentVisibility.ApplyArchiveScopeAsync(
            _db, q, GetUserId(), GetRole(), ct);
    }

    private async Task EnsureArchiveAccessAsync(Guid documentId, CancellationToken ct)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new NotFoundException("Document", documentId);

        await DocumentVisibility.EnsureCanViewArchiveDocumentAsync(
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
