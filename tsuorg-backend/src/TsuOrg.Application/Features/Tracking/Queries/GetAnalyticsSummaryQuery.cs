using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking.Queries;

// ─── DTM-03: Analytics summary ───────────────────────────────────────────────

public sealed record GetAnalyticsSummaryQuery(Guid? OrganizationId = null, Guid? AcademicYearId = null);

public sealed record AnalyticsSummaryDto(
    int TotalDocuments,
    int PendingReview,
    int Flagged,
    int Returned,
    int Approved,
    int Rejected,
    int Archived,
    IReadOnlyList<StatusCountDto> ByStatus,
    IReadOnlyList<AnalyticsTypeCountDto> ByDocumentType);

public sealed record StatusCountDto(string Status, int Count);
public sealed record AnalyticsTypeCountDto(string DocumentTypeCode, int Count);

public sealed class GetAnalyticsSummaryHandler
{
    private readonly IApplicationDbContext _db;

    public GetAnalyticsSummaryHandler(IApplicationDbContext db) => _db = db;

    public async Task<AnalyticsSummaryDto> HandleAsync(
        GetAnalyticsSummaryQuery q, CancellationToken ct = default)
    {
        var query = _db.Documents.AsNoTracking().AsQueryable();

        if (q.OrganizationId.HasValue)
            query = query.Where(d => d.OrganizationId == q.OrganizationId.Value);

        if (q.AcademicYearId.HasValue)
            query = query.Where(d => d.AcademicYearId == q.AcademicYearId.Value);

        var byStatus = await query
            .GroupBy(d => d.Status)
            .Select(g => new StatusCountDto(g.Key.ToString(), g.Count()))
            .ToListAsync(ct);

        var byType = await query
            .Include(d => d.DocumentType)
            .GroupBy(d => d.DocumentType!.Code)
            .Select(g => new AnalyticsTypeCountDto(g.Key, g.Count()))
            .ToListAsync(ct);

        int Count(DocumentStatus s) =>
            byStatus.FirstOrDefault(x => x.Status == s.ToString())?.Count ?? 0;

        return new AnalyticsSummaryDto(
            TotalDocuments: byStatus.Sum(x => x.Count),
            PendingReview:  Count(DocumentStatus.UnderReview)
                            + Count(DocumentStatus.Validating)
                            + Count(DocumentStatus.Submitted),
            Flagged:        Count(DocumentStatus.Flagged),
            Returned:       Count(DocumentStatus.Returned),
            Approved:       Count(DocumentStatus.Approved),
            Rejected:       Count(DocumentStatus.Rejected),
            Archived:       Count(DocumentStatus.Archived),
            ByStatus:       byStatus,
            ByDocumentType: byType);
    }
}
