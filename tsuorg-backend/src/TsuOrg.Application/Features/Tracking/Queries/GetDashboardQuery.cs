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
    IReadOnlyList<RecentDocDto>  RecentDocuments,
    IReadOnlyList<ReviewedDocDto> ReviewedDocuments,
    // Figure 21 (SOU Admin) additions
    int SubmittedThisMonth,
    int ActiveOrgs,
    IReadOnlyList<OrgComplianceDto> OrgCompliance);

public sealed record StageCountDto(string Stage, int Count);
public sealed record TypeCountDto(string TypeCode, string TypeName, int Count);
public sealed record RecentDocDto(
    Guid   DocumentId,
    string DocumentNumber,
    string Title,
    string DocumentTypeName,
    string Status,
    string Stage,
    DateTimeOffset SubmittedAt,
    int?   Score);

/// <summary>Figure 14 "Recently reviewed" — the caller's own past decisions.</summary>
public sealed record ReviewedDocDto(
    Guid   DocumentId,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string Action,
    DateTimeOffset DecidedAt,
    int?   Score);

/// <summary>Figure 21 "Organization compliance snapshot" — per-org lifecycle stats.</summary>
public sealed record OrgComplianceDto(
    Guid   OrganizationId,
    string Name,
    string? Acronym,
    string? College,
    int    Submissions,
    int    Approved,
    int    CompliancePercent);

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
            .Include(d => d.Organization)
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

        // Full lifecycle within org/college scope (no stage filter yet).
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

        int complianceRate = totalSubmitted == 0
            ? 0
            : (int)Math.Round(100.0 * approved / totalSubmitted);

        int approvedThisMonth = lifecycle.Count(d =>
            d.Status == DocumentStatus.Approved
            && d.UpdatedAt >= monthStart);

        int pendingCount = underReview;
        int returnedCount = returned;
        int totalReviewed = totalSubmitted;
        var reviewedDocs = new List<ReviewedDocDto>();

        // Figure 14 (Adviser / Dean): pending = UnderReview at THEIR stage in their org/college.
        // Approved/Returned/Total reviewed come from the caller's ApprovalHistory decisions.
        // Personal review KPIs are Adviser/Dean only (Figure 14).
        // SOU Home (Figure 21) uses GetSouDashboardHandler — global, not personal.
        bool isReviewer = q.CallerRole is AppRole.Adviser or AppRole.Dean;
        if (isReviewer)
        {
            var myStage = q.CallerRole switch
            {
                AppRole.Adviser => WorkflowStage.Adviser,
                AppRole.Dean => WorkflowStage.Dean,
                AppRole.SouStaff => WorkflowStage.Sou,
                _ => (WorkflowStage?)null,
            };

            pendingCount = myStage is null
                ? lifecycle.Count(d => d.Status == DocumentStatus.UnderReview)
                : lifecycle.Count(d =>
                    d.Status == DocumentStatus.UnderReview
                    && ((d.Workflow != null && d.Workflow.CurrentStage == myStage)
                        || (d.Workflow == null && d.CurrentStage == myStage)));

            var decisions = await _db.ApprovalHistories
                .Include(h => h.Workflow)!.ThenInclude(w => w!.Document)!.ThenInclude(d => d!.Organization)
                .AsNoTracking()
                .Where(h => h.ActorUserId == q.UserId)
                .OrderByDescending(h => h.DecidedAt)
                .ToListAsync(ct);

            approvedThisMonth = decisions.Count(h =>
                h.Action == ApprovalAction.Approve && h.DecidedAt >= monthStart);
            returnedCount = decisions.Count(h =>
                h.Action is ApprovalAction.Return or ApprovalAction.Reject);
            totalReviewed = decisions.Count;

            var reviewedDocIds = decisions
                .Take(8)
                .Select(h => h.Workflow?.DocumentId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            var reviewedScores = await _db.AIValidationResults
                .AsNoTracking()
                .Where(v => reviewedDocIds.Contains(v.DocumentId))
                .GroupBy(v => v.DocumentId)
                .Select(g => g.OrderByDescending(v => v.ProcessedAt).First())
                .ToListAsync(ct);
            var reviewedScoreMap = reviewedScores.ToDictionary(v => v.DocumentId);

            reviewedDocs = decisions
                .Take(8)
                .Where(h => h.Workflow?.Document is not null)
                .Select(h =>
                {
                    var doc = h.Workflow!.Document!;
                    reviewedScoreMap.TryGetValue(doc.Id, out var v);
                    return new ReviewedDocDto(
                        doc.Id,
                        doc.DocumentNumber,
                        string.IsNullOrWhiteSpace(doc.Title) ? doc.DocumentNumber : doc.Title,
                        doc.Organization?.Name ?? "—",
                        h.Action.ToString(),
                        h.DecidedAt,
                        ScoreNormalizer.ToPercentOrNull(v?.Confidence));
                })
                .ToList();
        }

        var byStage = lifecycle
            .GroupBy(d => (d.Workflow?.CurrentStage ?? d.CurrentStage).ToString())
            .Select(g => new StageCountDto(g.Key, g.Count()))
            .ToList();

        var byType = lifecycle
            .GroupBy(d => new { Code = d.DocumentType?.Code ?? "?", Name = d.DocumentType?.Name ?? "Unknown" })
            .Select(g => new TypeCountDto(g.Key.Code, g.Key.Name, g.Count()))
            .ToList();

        int submittedThisMonth = lifecycle.Count(d => d.CreatedAt >= monthStart);

        int activeOrgs = lifecycle
            .Select(d => d.OrganizationId)
            .Distinct()
            .Count();

        var orgCompliance = lifecycle
            .Where(d => d.Organization is not null)
            .GroupBy(d => d.OrganizationId)
            .Select(g =>
            {
                var org = g.First().Organization!;
                var submissions = g.Count();
                var orgApproved = g.Count(d => d.Status is DocumentStatus.Approved or DocumentStatus.Archived);
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
            .ToList();

        var recentSource = lifecycle
            .OrderByDescending(d => d.CreatedAt)
            .Take(10)
            .ToList();

        var recentIds = recentSource.Select(d => d.Id).ToList();
        var recentScores = await _db.AIValidationResults
            .AsNoTracking()
            .Where(v => recentIds.Contains(v.DocumentId))
            .GroupBy(v => v.DocumentId)
            .Select(g => g.OrderByDescending(v => v.ProcessedAt).First())
            .ToListAsync(ct);
        var recentScoreMap = recentScores.ToDictionary(v => v.DocumentId);

        var recent = recentSource
            .Select(d =>
            {
                recentScoreMap.TryGetValue(d.Id, out var v);
                return new RecentDocDto(
                    d.Id,
                    d.DocumentNumber,
                    string.IsNullOrWhiteSpace(d.Title)
                        ? (d.DocumentType?.Name ?? "Untitled")
                        : d.Title,
                    d.DocumentType?.Name ?? "Unknown",
                    d.Status.ToString(),
                    (d.Workflow?.CurrentStage ?? d.CurrentStage).ToString(),
                    d.CreatedAt,
                    ScoreNormalizer.ToPercentOrNull(v?.Confidence));
            })
            .ToList();

        return new DashboardDto(
            TotalSubmitted: totalSubmitted,
            UnderReview: underReview,
            Approved: approved,
            Returned: returned,
            OutstandingDocs: outstanding,
            ComplianceRate: complianceRate,
            ItemsFlagged: flagged,
            PendingCount: pendingCount,
            ApprovedThisMonth: approvedThisMonth,
            ReturnedCount: returnedCount,
            TotalDocuments: totalReviewed,
            ByStage: byStage,
            ByType: byType,
            RecentDocuments: recent,
            ReviewedDocuments: reviewedDocs,
            SubmittedThisMonth: submittedThisMonth,
            ActiveOrgs: activeOrgs,
            OrgCompliance: orgCompliance);
    }

}
