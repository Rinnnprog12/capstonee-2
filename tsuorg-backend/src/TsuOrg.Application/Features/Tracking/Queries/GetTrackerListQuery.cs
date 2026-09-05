using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking.Queries;

// ─── DTM-01: Tracker list (storyboard Document Tracker) ─────────────────────

public sealed record GetTrackerListQuery(
    Guid RequestingUserId,
    string RequestingRole,
    TrackerTab Tab = TrackerTab.All,
    int Page = 1,
    int PageSize = 50,
    // Figure 24 (SOU global tracker) filters
    string? Search = null,
    string? College = null,
    Guid? OrganizationId = null);

public sealed record TrackerDocumentCardDto(
    Guid Id,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string DocumentTypeCode,
    DocumentStatus Status,
    WorkflowStage CurrentStage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    int CurrentIndex,
    string OverallStatus,
    string OverallTone,
    IReadOnlyList<TimelineStepDto> Steps,
    int? Score);

public sealed record TrackerTabCountDto(string Tab, int Count);

public sealed record TrackerListResult(
    IReadOnlyList<TrackerDocumentCardDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<TrackerTabCountDto> TabCounts);

public sealed class GetTrackerListHandler
{
    private readonly IApplicationDbContext _db;

    public GetTrackerListHandler(IApplicationDbContext db) => _db = db;

    public async Task<TrackerListResult> HandleAsync(
        GetTrackerListQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 100);

        var baseQuery = _db.Documents
            .AsNoTracking()
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .Include(d => d.Workflow)
            .Where(d => d.Status != DocumentStatus.Draft);

        // OrgOfficer = own submissions; Adviser = assigned orgs; Dean = college; SOU = global.
        baseQuery = DocumentVisibility.ApplyRoleScope(baseQuery, q.RequestingUserId, q.RequestingRole);
        baseQuery = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
            _db, baseQuery, q.RequestingUserId, q.RequestingRole, ct);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            baseQuery = baseQuery.Where(d =>
                d.DocumentNumber.Contains(term)
                || d.Title.Contains(term)
                || d.Organization!.Name.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(q.College))
        {
            var college = q.College.Trim();
            baseQuery = baseQuery.Where(d =>
                d.Organization!.College == college
                || d.Organization!.CollegeRef!.Code == college);
        }

        if (q.OrganizationId.HasValue)
            baseQuery = baseQuery.Where(d => d.OrganizationId == q.OrganizationId.Value);

        // Materialize scoped set (timeline projection is in-memory / pure).
        var scoped = await baseQuery
            .OrderByDescending(d => d.UpdatedAt ?? d.CreatedAt)
            .Select(d => new
            {
                d.Id,
                d.DocumentNumber,
                d.Title,
                OrgName = d.Organization!.Name,
                TypeCode = d.DocumentType!.Code,
                d.Status,
                Stage = d.Workflow != null ? d.Workflow.CurrentStage : d.CurrentStage,
                d.CreatedAt,
                d.UpdatedAt,
                SubmittedAt = d.MetadataLockedAt ?? d.CreatedAt,
            })
            .ToListAsync(ct);

        var tabCounts = Enum.GetValues<TrackerTab>()
            .Select(tab => new TrackerTabCountDto(
                tab.ToString(),
                scoped.Count(d => DocumentTimelineProjector.MatchesTab(tab, d.Status, d.Stage))))
            .ToList();

        var filtered = scoped
            .Where(d => DocumentTimelineProjector.MatchesTab(q.Tab, d.Status, d.Stage))
            .ToList();

        var total = filtered.Count;
        var pageSource = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // AI compliance % from latest CALSV row (backend persists ML output).
        var pageIds = pageSource.Select(d => d.Id).ToList();
        var scores = await _db.AIValidationResults
            .AsNoTracking()
            .Where(v => pageIds.Contains(v.DocumentId))
            .GroupBy(v => v.DocumentId)
            .Select(g => g.OrderByDescending(v => v.ProcessedAt).First())
            .ToListAsync(ct);
        var scoreMap = scores.ToDictionary(
            v => v.DocumentId,
            v => (int)Math.Round(v.Confidence > 1m ? v.Confidence : v.Confidence * 100));

        var pageItems = pageSource
            .Select(d =>
            {
                var timeline = DocumentTimelineProjector.Project(d.Status, d.Stage);
                var title = string.IsNullOrWhiteSpace(d.Title) ? d.TypeCode : d.Title;
                return new TrackerDocumentCardDto(
                    d.Id,
                    d.DocumentNumber,
                    title,
                    d.OrgName,
                    d.TypeCode,
                    d.Status,
                    d.Stage,
                    d.SubmittedAt,
                    d.UpdatedAt,
                    timeline.CurrentIndex,
                    timeline.OverallStatus,
                    timeline.OverallTone,
                    timeline.Steps,
                    scoreMap.TryGetValue(d.Id, out var s) ? s : null);
            })
            .ToList();

        return new TrackerListResult(pageItems, total, page, pageSize, tabCounts);
    }
}
