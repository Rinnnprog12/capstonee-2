using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Archive.Queries;

// ─── DMA Repository (Figure 13) ──────────────────────────────────────────────
// Role-scoped Approved/Archived documents, filterable by type / event / AY.
// Folder counts drive the repository category UX.

public sealed record GetArchiveQuery(
    Guid RequestingUserId,
    string RequestingRole,
    Guid? OrganizationId = null,
    string? DocumentTypeCode = null,
    string? EventKeyword = null,
    Guid? AcademicYearId = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 20);

public sealed record ArchiveOrgDto(Guid Id, string Name, string? Acronym);

public sealed record ArchiveTypeDto(string Code, string Name);

public sealed record ArchiveDocumentDto(
    Guid Id,
    string DocumentNumber,
    string Title,
    ArchiveOrgDto Organization,
    ArchiveTypeDto DocumentType,
    string? AcademicYear,
    Guid AcademicYearId,
    string Status,
    string? PrimaryFileName,
    bool HasPrimaryFile,
    int VersionCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record ArchiveListResult(
    IReadOnlyList<ArchiveDocumentDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record GetArchiveCategoryCountsQuery(
    Guid RequestingUserId,
    string RequestingRole,
    Guid? OrganizationId = null,
    Guid? AcademicYearId = null);

public sealed record ArchiveTypeCountDto(string Code, string Name, int Count);

public sealed record ArchiveStatusCountDto(string Status, int Count);

public sealed record ArchiveCategoryCountsDto(
    IReadOnlyList<ArchiveTypeCountDto> ByType,
    IReadOnlyList<ArchiveStatusCountDto> ByStatus);

public sealed class GetArchiveHandler
{
    private readonly IApplicationDbContext _db;

    public GetArchiveHandler(IApplicationDbContext db) => _db = db;

    public async Task<ArchiveListResult> HandleAsync(GetArchiveQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 100);

        var query = await BuildScopedQueryAsync(q.RequestingUserId, q.RequestingRole, ct);
        query = ApplyFilters(query, q.OrganizationId, q.DocumentTypeCode, q.EventKeyword, q.AcademicYearId, q.Status);

        var total = await query.CountAsync(ct);

        var pageDocs = await query
            .OrderByDescending(d => d.UpdatedAt ?? d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.DocumentNumber,
                d.Title,
                OrgId = d.Organization!.Id,
                OrgName = d.Organization.Name,
                OrgAcronym = d.Organization.Acronym,
                TypeCode = d.DocumentType!.Code,
                TypeName = d.DocumentType.Name,
                AcademicYear = d.AcademicYear != null ? d.AcademicYear.Label : null,
                d.AcademicYearId,
                Status = d.Status.ToString(),
                d.PrimaryFileName,
                HasPrimaryFile = !string.IsNullOrEmpty(d.PrimaryFileBlobPath),
                d.CreatedAt,
                d.UpdatedAt,
            })
            .ToListAsync(ct);

        var ids = pageDocs.Select(d => d.Id).ToList();
        var versionCounts = ids.Count == 0
            ? new Dictionary<Guid, int>()
            : await _db.DocumentVersions.AsNoTracking()
                .Where(v => ids.Contains(v.DocumentId))
                .GroupBy(v => v.DocumentId)
                .Select(g => new { DocumentId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.DocumentId, x => x.Count, ct);

        var items = pageDocs.Select(d => new ArchiveDocumentDto(
            d.Id,
            d.DocumentNumber,
            string.IsNullOrWhiteSpace(d.Title) ? d.DocumentNumber : d.Title,
            new ArchiveOrgDto(d.OrgId, d.OrgName, string.IsNullOrWhiteSpace(d.OrgAcronym) ? null : d.OrgAcronym),
            new ArchiveTypeDto(d.TypeCode, d.TypeName),
            d.AcademicYear,
            d.AcademicYearId,
            d.Status,
            d.PrimaryFileName,
            d.HasPrimaryFile,
            versionCounts.GetValueOrDefault(d.Id),
            d.CreatedAt,
            d.UpdatedAt)).ToList();

        return new ArchiveListResult(items, total, page, pageSize);
    }

    public async Task<ArchiveCategoryCountsDto> HandleCountsAsync(
        GetArchiveCategoryCountsQuery q, CancellationToken ct = default)
    {
        var query = await BuildScopedQueryAsync(q.RequestingUserId, q.RequestingRole, ct);

        if (q.OrganizationId.HasValue)
            query = query.Where(d => d.OrganizationId == q.OrganizationId.Value);
        if (q.AcademicYearId.HasValue)
            query = query.Where(d => d.AcademicYearId == q.AcademicYearId.Value);

        var byType = await query
            .GroupBy(d => new { d.DocumentType!.Code, d.DocumentType.Name })
            .Select(g => new ArchiveTypeCountDto(g.Key.Code, g.Key.Name, g.Count()))
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var byStatus = await query
            .GroupBy(d => d.Status)
            .Select(g => new ArchiveStatusCountDto(g.Key.ToString(), g.Count()))
            .ToListAsync(ct);

        return new ArchiveCategoryCountsDto(byType, byStatus);
    }

    private async Task<IQueryable<Domain.Entities.Document>> BuildScopedQueryAsync(
        Guid userId, string role, CancellationToken ct)
    {
        var q = _db.Documents
            .AsNoTracking()
            .Where(d => d.Status == DocumentStatus.Archived || d.Status == DocumentStatus.Approved);

        return await DocumentVisibility.ApplyArchiveScopeAsync(_db, q, userId, role, ct);
    }

    private static IQueryable<Domain.Entities.Document> ApplyFilters(
        IQueryable<Domain.Entities.Document> q,
        Guid? organizationId,
        string? documentTypeCode,
        string? eventKeyword,
        Guid? academicYearId,
        string? status)
    {
        if (organizationId.HasValue)
            q = q.Where(d => d.OrganizationId == organizationId.Value);

        if (!string.IsNullOrWhiteSpace(documentTypeCode))
            q = q.Where(d => d.DocumentType!.Code == documentTypeCode);

        if (!string.IsNullOrWhiteSpace(eventKeyword))
            q = q.Where(d => EF.Functions.Like(d.Title!, $"%{eventKeyword.Trim()}%"));

        if (academicYearId.HasValue)
            q = q.Where(d => d.AcademicYearId == academicYearId.Value);

        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<DocumentStatus>(status, true, out var st)
            && st is DocumentStatus.Archived or DocumentStatus.Approved)
            q = q.Where(d => d.Status == st);

        return q;
    }
}
