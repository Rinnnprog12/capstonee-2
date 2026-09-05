using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking.Queries;

/// <summary>
/// Figure 21 — SOU Admin Dashboard. Global (not personal) metrics from
/// officer submissions + persisted CALSV results. ML never writes these
/// aggregates; the API reads Documents / Organizations / AIValidationResults.
/// </summary>
public sealed record GetSouDashboardQuery;

public sealed record SouStatusBreakdownDto(
    int ApprovedCount,
    int UnderReviewCount,
    int ReturnedCount,
    int ApprovedPercent,
    int UnderReviewPercent,
    int ReturnedPercent);

public sealed record RecentValidationDto(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string DocumentTypeName,
    string DocumentClass,
    int ScorePercent,
    DateTimeOffset ProcessedAt);

public sealed record SouDashboardDto(
    int TotalDocuments,
    int SubmittedThisMonth,
    int ApprovalRate,
    int ActiveOrgs,
    IReadOnlyList<TypeCountDto> ByType,
    SouStatusBreakdownDto StatusBreakdown,
    IReadOnlyList<OrgComplianceDto> OrgCompliance,
    IReadOnlyList<RecentValidationDto> RecentValidations);

public sealed class GetSouDashboardHandler
{
    /// <summary>Paper Figure 21 bars: Activity / Accomp. / Forms.</summary>
    private static readonly (string Code, string Label)[] TypeBuckets =
    [
        ("SF08", "Activity"),
        ("ACCOMPLISHMENT", "Accomp."),
        ("ACCREDITATION", "Forms"),
    ];

    private readonly IApplicationDbContext _db;

    public GetSouDashboardHandler(IApplicationDbContext db) => _db = db;

    public async Task<SouDashboardDto> HandleAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var docs = await _db.Documents
            .AsNoTracking()
            .Include(d => d.DocumentType)
            .Include(d => d.Organization)
            .Where(d => d.Status != DocumentStatus.Draft)
            .ToListAsync(ct);

        var total = docs.Count;
        var submittedThisMonth = docs.Count(d => SubmittedAt(d) >= monthStart);

        var approvedCount = docs.Count(IsApproved);
        var returnedCount = docs.Count(IsReturned);
        var underReviewCount = total - approvedCount - returnedCount;
        if (underReviewCount < 0) underReviewCount = 0;

        var approvalRate = total == 0
            ? 0
            : (int)Math.Round(100.0 * approvedCount / total);

        var percents = DistributePercents(approvedCount, underReviewCount, returnedCount);

        var activeOrgs = await _db.Organizations
            .AsNoTracking()
            .CountAsync(o => o.Status == "Active", ct);

        var byType = TypeBuckets
            .Select(b => new TypeCountDto(
                b.Code,
                b.Label,
                docs.Count(d => string.Equals(d.DocumentType?.Code, b.Code, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var orgCompliance = docs
            .Where(d => d.Organization is not null)
            .GroupBy(d => d.OrganizationId)
            .Select(g =>
            {
                var org = g.First().Organization!;
                var submissions = g.Count();
                var orgApproved = g.Count(IsApproved);
                return new OrgComplianceDto(
                    org.Id,
                    org.Name,
                    string.IsNullOrWhiteSpace(org.Acronym) ? null : org.Acronym,
                    org.College,
                    submissions,
                    orgApproved,
                    submissions == 0 ? 0 : (int)Math.Round(100.0 * orgApproved / submissions));
            })
            .OrderByDescending(o => o.Submissions)
            .ThenBy(o => o.Name)
            .Take(8)
            .ToList();

        var recentValidations = await LoadRecentValidationsAsync(ct);

        return new SouDashboardDto(
            TotalDocuments: total,
            SubmittedThisMonth: submittedThisMonth,
            ApprovalRate: approvalRate,
            ActiveOrgs: activeOrgs,
            ByType: byType,
            StatusBreakdown: new SouStatusBreakdownDto(
                approvedCount,
                underReviewCount,
                returnedCount,
                percents[0],
                percents[1],
                percents[2]),
            OrgCompliance: orgCompliance,
            RecentValidations: recentValidations);
    }

    private async Task<IReadOnlyList<RecentValidationDto>> LoadRecentValidationsAsync(CancellationToken ct)
    {
        var latestRows = await _db.AIValidationResults
            .AsNoTracking()
            .OrderByDescending(v => v.ProcessedAt)
            .Take(40)
            .ToListAsync(ct);

        var latest = latestRows
            .GroupBy(v => v.DocumentId)
            .Select(g => g.First())
            .Take(8)
            .ToList();

        if (latest.Count == 0)
            return [];

        var ids = latest.Select(v => v.DocumentId).ToList();
        var docs = await _db.Documents
            .AsNoTracking()
            .Include(d => d.DocumentType)
            .Include(d => d.Organization)
            .Where(d => ids.Contains(d.Id) && d.Status != DocumentStatus.Draft)
            .ToDictionaryAsync(d => d.Id, ct);

        return latest
            .Where(v => docs.ContainsKey(v.DocumentId))
            .Select(v =>
            {
                var d = docs[v.DocumentId];
                return new RecentValidationDto(
                    d.Id,
                    d.DocumentNumber,
                    string.IsNullOrWhiteSpace(d.Title) ? (d.DocumentType?.Name ?? d.DocumentNumber) : d.Title,
                    d.Organization?.Name ?? "—",
                    d.DocumentType?.Name ?? "Unknown",
                    v.DocumentClass,
                    ScoreNormalizer.ToPercent(v.Confidence),
                    v.ProcessedAt);
            })
            .ToList();
    }

    private static DateTimeOffset SubmittedAt(Document d) =>
        d.MetadataLockedAt ?? d.CreatedAt;

    private static bool IsApproved(Document d) =>
        d.Status is DocumentStatus.Approved or DocumentStatus.Archived;

    private static bool IsReturned(Document d) =>
        d.Status is DocumentStatus.Returned or DocumentStatus.Rejected;

    /// <summary>Largest-remainder so Approved + Under review + Returned = 100 (or 0 if empty).</summary>
    private static int[] DistributePercents(params int[] counts)
    {
        var total = counts.Sum();
        if (total == 0)
            return new int[counts.Length];

        var raw = counts.Select(c => 100.0 * c / total).ToArray();
        var floors = raw.Select(r => (int)Math.Floor(r)).ToArray();
        var remainder = 100 - floors.Sum();
        var order = raw
            .Select((r, i) => (i, frac: r - floors[i]))
            .OrderByDescending(x => x.frac)
            .ThenBy(x => x.i)
            .ToList();

        for (var k = 0; k < remainder; k++)
            floors[order[k].i]++;

        return floors;
    }
}
