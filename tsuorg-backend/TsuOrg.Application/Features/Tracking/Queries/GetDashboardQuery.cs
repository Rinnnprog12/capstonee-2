using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking.Queries;

// ─── Query / Result ─────────────────────────────────────────────────────────

public sealed record GetDashboardQuery(
    Guid     UserId,
    AppRole  CallerRole,
    Guid?    OrgId = null);

/// <summary>
/// Officer / reviewer dashboard metrics (Figure 5 + role dashboards).
/// Lifecycle counts are all-time within the caller's visibility scope.
/// </summary>
public sealed record DashboardDto(
    int TotalSubmitted,
    int UnderReview,
    int Approved,
    int Returned,
    int OutstandingDocs,
    int ComplianceRate,
    int ItemsFlagged,
    // Legacy aliases kept for any earlier clients
    int PendingCount,
    int ApprovedThisMonth,
    int ReturnedCount,
    int TotalDocuments,
    IReadOnlyList<StageCountDto> ByStage,
    IReadOnlyList<TypeCountDto>  ByType,
    IReadOnlyList<RecentDocDto>  RecentDocuments);

public sealed record StageCountDto(string Stage, int Count);
public sealed record TypeCountDto(string TypeCode, string TypeName, int Count);
public sealed record RecentDocDto(
    Guid   DocumentId,
    string DocumentNumber,
    string Title,
    string DocumentTypeName,
    string Status,
    string Stage,
    DateTimeOffset SubmittedAt);

// ─── Handler ─────────────────────────────────────────────────────────────────

public sealed class GetDashboardHandler
{
    private readonly IApplicationDbContext _db;

    public GetDashboardHandler(IApplicationDbContext db) => _db = db;

    public async Task<DashboardDto> HandleAsync(GetDashboardQuery q, CancellationToken ct = default)
    {
        var now        = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var roleName   = q.CallerRole.ToString();

        var docsQuery = _db.Documents
            .Include(d => d.DocumentType)
            .Include(d => d.Workflow)
            .AsNoTracking();

        // Org officers: org membership scope (Figure 5 = org lifecycle overview).
        // Other roles: shared DTM visibility + adviser/dean college/org refinement.
        if (q.CallerRole == AppRole.OrgOfficer)
        {
            docsQuery = await DocumentVisibility.ApplyArchiveScopeAsync(
                _db, docsQuery, q.UserId, roleName, ct);
        }
        else
        {
            docsQuery = DocumentVisibility.ApplyRoleScope(docsQuery, q.UserId, roleName);
            docsQuery = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
                _db, docsQuery, q.UserId, roleName, ct);
        }

        if (q.OrgId.HasValue)
            docsQuery = docsQuery.Where(d => d.OrganizationId == q.OrgId.Value);

        var docs = await docsQuery.ToListAsync(ct);

        // Reviewers: stage-scoped queue (officers keep full org lifecycle).
        var stageFilter = q.CallerRole switch
        {
            AppRole.Adviser  => WorkflowStage.Adviser,
            AppRole.Dean     => WorkflowStage.Dean,
            AppRole.SouStaff => WorkflowStage.Sou,
            _                => (WorkflowStage?)null
        };

        if (stageFilter.HasValue)
        {
            docs = docs.Where(d =>
                (d.Workflow != null && d.Workflow.CurrentStage == stageFilter.Value)
                || (d.Workflow == null && d.CurrentStage == stageFilter.Value)).ToList();
        }

        // Drafts = wizard in-progress; Archived = DMA repository — exclude from Figure 5 lifecycle.
        var lifecycle = docs
            .Where(d => d.Status is not (DocumentStatus.Draft or DocumentStatus.Archived))
            .ToList();

        int totalSubmitted = lifecycle.Count;
        int underReview = lifecycle.Count(d =>
            d.Status is DocumentStatus.UnderReview or DocumentStatus.Validating or DocumentStatus.Submitted);
        int approved = lifecycle.Count(d => d.Status == DocumentStatus.Approved);
        int returned = lifecycle.Count(d => d.Status == DocumentStatus.Returned);
        int flagged = lifecycle.Count(d => d.Status == DocumentStatus.Flagged);
        int outstanding = lifecycle.Count(d =>
            d.Status is DocumentStatus.Submitted
                or DocumentStatus.Validating
                or DocumentStatus.Flagged
                or DocumentStatus.UnderReview
                or DocumentStatus.Returned);

        // Compliance = share of submitted docs that reached approval.
        int complianceRate = totalSubmitted == 0
            ? 0
            : (int)Math.Round(100.0 * approved / totalSubmitted);

        int approvedThisMonth = lifecycle.Count(d =>
            d.Status == DocumentStatus.Approved
            && d.UpdatedAt >= monthStart);

        var byStage = lifecycle
            .GroupBy(d => (d.Workflow?.CurrentStage ?? d.CurrentStage).ToString())
            .Select(g => new StageCountDto(g.Key, g.Count()))
            .ToList();

        var byType = lifecycle
            .GroupBy(d => new { Code = d.DocumentType?.Code ?? "?", Name = d.DocumentType?.Name ?? "Unknown" })
            .Select(g => new TypeCountDto(g.Key.Code, g.Key.Name, g.Count()))
            .ToList();

        var recent = lifecycle
            .OrderByDescending(d => d.CreatedAt)
            .Take(10)
            .Select(d => new RecentDocDto(
                d.Id,
                d.DocumentNumber,
                string.IsNullOrWhiteSpace(d.Title)
                    ? (d.DocumentType?.Name ?? "Untitled")
                    : d.Title,
                d.DocumentType?.Name ?? "Unknown",
                d.Status.ToString(),
                (d.Workflow?.CurrentStage ?? d.CurrentStage).ToString(),
                d.CreatedAt))
            .ToList();

        return new DashboardDto(
            TotalSubmitted: totalSubmitted,
            UnderReview: underReview,
            Approved: approved,
            Returned: returned,
            OutstandingDocs: outstanding,
            ComplianceRate: complianceRate,
            ItemsFlagged: flagged,
            PendingCount: underReview,
            ApprovedThisMonth: approvedThisMonth,
            ReturnedCount: returned,
            TotalDocuments: totalSubmitted,
            ByStage: byStage,
            ByType: byType,
            RecentDocuments: recent);
    }
}
