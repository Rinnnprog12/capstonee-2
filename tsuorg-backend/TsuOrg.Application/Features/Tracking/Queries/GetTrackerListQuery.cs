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
    int PageSize = 50);

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
    IReadOnlyList<TimelineStepDto> Steps);

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
            .Where(d => d.Status != DocumentStatus.Draft);

        baseQuery = DocumentVisibility.ApplyRoleScope(baseQuery, q.RequestingUserId, q.RequestingRole);
        baseQuery = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
            _db, baseQuery, q.RequestingUserId, q.RequestingRole, ct);

        // Materialize scoped set (timeline projection is in-memory / pure).
        // Acceptable for org-scoped MVP volumes; paginate after filter.
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
                d.CurrentStage,
                d.CreatedAt,
                d.UpdatedAt,
            })
            .ToListAsync(ct);

        var tabCounts = Enum.GetValues<TrackerTab>()
            .Select(tab => new TrackerTabCountDto(
                tab.ToString(),
                scoped.Count(d => DocumentTimelineProjector.MatchesTab(tab, d.Status, d.CurrentStage))))
            .ToList();

        var filtered = scoped
            .Where(d => DocumentTimelineProjector.MatchesTab(q.Tab, d.Status, d.CurrentStage))
            .ToList();

        var total = filtered.Count;
        var pageItems = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d =>
            {
                var timeline = DocumentTimelineProjector.Project(d.Status, d.CurrentStage);
                var title = string.IsNullOrWhiteSpace(d.Title) ? d.TypeCode : d.Title;
                return new TrackerDocumentCardDto(
                    d.Id,
                    d.DocumentNumber,
                    title,
                    d.OrgName,
                    d.TypeCode,
                    d.Status,
                    d.CurrentStage,
                    d.CreatedAt,
                    d.UpdatedAt,
                    timeline.CurrentIndex,
                    timeline.OverallStatus,
                    timeline.OverallTone,
                    timeline.Steps);
            })
            .ToList();

        return new TrackerListResult(pageItems, total, page, pageSize, tabCounts);
    }
}
