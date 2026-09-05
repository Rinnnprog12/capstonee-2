using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Organizations.Queries;

// ─── SOU Organizations Management (Figure 26) ────────────────────────────────
// Stats are data-driven from Document + AIValidationResult (upload → CALSV → workflow).

public enum OrganizationsAdminTab
{
    Active = 0,
    Inactive = 1,
}

public sealed record GetOrganizationsAdminQuery(
    OrganizationsAdminTab Tab = OrganizationsAdminTab.Active);

public sealed record OrganizationAdminDto(
    Guid Id,
    string Name,
    string? Acronym,
    string? College,
    string? AdviserName,
    string? PresidentName,
    /// <summary>Active | Flagged | Inactive</summary>
    string HealthStatus,
    int Submissions,
    int Approved,
    int Returned,
    /// <summary>Avg latest CALSV confidence (0–100). 0 when no ML results yet.</summary>
    int CompliancePercent);

public sealed record OrganizationsAdminResult(
    IReadOnlyList<OrganizationAdminDto> Items,
    int ActiveCount,
    int InactiveCount);

public sealed class GetOrganizationsAdminHandler
{
    private readonly IApplicationDbContext _db;

    public GetOrganizationsAdminHandler(IApplicationDbContext db) => _db = db;

    public async Task<OrganizationsAdminResult> HandleAsync(
        GetOrganizationsAdminQuery q, CancellationToken ct = default)
    {
        var flaggedThreshold = await ResolveFlaggedThresholdAsync(ct);
        var warnLowCompliance = await IsLowComplianceWarningEnabledAsync(ct);

        var orgs = await _db.Organizations
            .AsNoTracking()
            .Include(o => o.PrimaryAdviser)
            .Include(o => o.Officer)
            .Include(o => o.CollegeRef)
            .OrderBy(o => o.College)
            .ThenBy(o => o.Name)
            .ToListAsync(ct);

        var stats = await _db.Documents
            .AsNoTracking()
            .Where(d => d.Status != DocumentStatus.Draft)
            .GroupBy(d => d.OrganizationId)
            .Select(g => new OrgLifecycleStats(
                g.Key,
                g.Count(),
                g.Count(d => d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Archived),
                g.Count(d => d.Status == DocumentStatus.Returned || d.Status == DocumentStatus.Rejected)))
            .ToListAsync(ct);
        var statMap = stats.ToDictionary(s => s.OrgId);

        var mlByOrg = await LoadMlComplianceByOrgAsync(ct);

        var all = orgs.Select(o =>
        {
            statMap.TryGetValue(o.Id, out var s);
            var submissions = s?.Submissions ?? 0;
            var approved = s?.Approved ?? 0;
            var returned = s?.Returned ?? 0;
            var hasMl = mlByOrg.TryGetValue(o.Id, out var compliance);
            if (!hasMl) compliance = 0;

            var isActive = string.Equals(o.Status, "Active", StringComparison.OrdinalIgnoreCase);
            var health = !isActive ? "Inactive"
                : warnLowCompliance && hasMl && compliance < flaggedThreshold ? "Flagged"
                : "Active";

            return new OrganizationAdminDto(
                o.Id,
                o.Name,
                string.IsNullOrWhiteSpace(o.Acronym) ? null : o.Acronym,
                o.CollegeRef?.Code ?? o.College,
                o.PrimaryAdviser?.FullName,
                o.Officer?.FullName,
                health,
                submissions,
                approved,
                returned,
                compliance);
        }).ToList();

        var activeItems = all.Where(o => o.HealthStatus != "Inactive").ToList();
        var inactiveItems = all.Where(o => o.HealthStatus == "Inactive").ToList();

        return new OrganizationsAdminResult(
            q.Tab == OrganizationsAdminTab.Inactive ? inactiveItems : activeItems,
            activeItems.Count,
            inactiveItems.Count);
    }

    private async Task<int> ResolveFlaggedThresholdAsync(CancellationToken ct)
    {
        var raw = await _db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key == "compliance.min_score")
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        return int.TryParse(raw, out var score) ? Math.Clamp(score, 0, 100) : 75;
    }

    private async Task<bool> IsLowComplianceWarningEnabledAsync(CancellationToken ct)
    {
        var raw = await _db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key == "notify.low_compliance")
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        return !bool.TryParse(raw, out var enabled) || enabled;
    }

    /// <summary>
    /// Per-org average of each document's latest AIValidationResult confidence.
    /// </summary>
    private async Task<Dictionary<Guid, int>> LoadMlComplianceByOrgAsync(CancellationToken ct)
    {
        var rows = await (
            from v in _db.AIValidationResults.AsNoTracking()
            join d in _db.Documents.AsNoTracking() on v.DocumentId equals d.Id
            where d.Status != DocumentStatus.Draft
            select new { d.OrganizationId, v.DocumentId, v.Confidence, v.ProcessedAt }
        ).ToListAsync(ct);

        if (rows.Count == 0)
            return new Dictionary<Guid, int>();

        return rows
            .GroupBy(r => r.DocumentId)
            .Select(g => g.OrderByDescending(r => r.ProcessedAt).First())
            .GroupBy(r => r.OrganizationId)
            .ToDictionary(
                g => g.Key,
                g => (int)Math.Round(g.Average(r => (double)ScoreNormalizer.ToPercent(r.Confidence))));
    }

    private sealed record OrgLifecycleStats(Guid OrgId, int Submissions, int Approved, int Returned);
}
