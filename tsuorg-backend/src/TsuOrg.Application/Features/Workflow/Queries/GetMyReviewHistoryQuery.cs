using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Workflow.Queries;

// ─── Review History (Figures 19–20) ──────────────────────────────────────────
// Archival record of the caller's own evaluation decisions.

/// <summary>Figure 19 tabs — All | Approved | Returned (includes Reject).</summary>
public enum ReviewHistoryTab
{
    All = 0,
    Approved = 1,
    Returned = 2,
}

public sealed record GetMyReviewHistoryQuery(
    Guid RequestingUserId,
    ReviewHistoryTab Tab = ReviewHistoryTab.All,
    int Page = 1,
    int PageSize = 50);

public sealed record ReviewHistoryItemDto(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string DocumentTypeCode,
    string Action,
    string? Comments,
    DateTimeOffset DecidedAt,
    int? Score);

public sealed record ReviewHistoryResult(
    IReadOnlyList<ReviewHistoryItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int AllCount,
    int ApprovedCount,
    int ReturnedCount);

public sealed class GetMyReviewHistoryHandler
{
    private readonly IApplicationDbContext _db;

    public GetMyReviewHistoryHandler(IApplicationDbContext db) => _db = db;

    public async Task<ReviewHistoryResult> HandleAsync(
        GetMyReviewHistoryQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 100);

        var decisions = await _db.ApprovalHistories
            .AsNoTracking()
            .Where(h => h.ActorUserId == q.RequestingUserId)
            .OrderByDescending(h => h.DecidedAt)
            .Select(h => new
            {
                h.Action,
                h.Comments,
                h.DecidedAt,
                DocumentId = h.Workflow!.DocumentId,
                DocumentNumber = h.Workflow.Document!.DocumentNumber,
                h.Workflow.Document.Title,
                OrgName = h.Workflow.Document.Organization!.Name,
                TypeCode = h.Workflow.Document.DocumentType!.Code,
                TypeName = h.Workflow.Document.DocumentType!.Name,
            })
            .ToListAsync(ct);

        var approvedCount = decisions.Count(d => d.Action == ApprovalAction.Approve);
        var returnedCount = decisions.Count - approvedCount;

        var filtered = q.Tab switch
        {
            ReviewHistoryTab.Approved => decisions.Where(d => d.Action == ApprovalAction.Approve).ToList(),
            ReviewHistoryTab.Returned => decisions.Where(d => d.Action != ApprovalAction.Approve).ToList(),
            _ => decisions,
        };

        var pageSource = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var docIds = pageSource.Select(d => d.DocumentId).Distinct().ToList();
        var scores = await _db.AIValidationResults
            .AsNoTracking()
            .Where(v => docIds.Contains(v.DocumentId))
            .GroupBy(v => v.DocumentId)
            .Select(g => g.OrderByDescending(v => v.ProcessedAt).First())
            .ToListAsync(ct);
        var scoreMap = scores.ToDictionary(
            v => v.DocumentId,
            v => (int)Math.Round(v.Confidence > 1m ? v.Confidence : v.Confidence * 100));

        var items = pageSource
            .Select(d => new ReviewHistoryItemDto(
                d.DocumentId,
                d.DocumentNumber,
                string.IsNullOrWhiteSpace(d.Title) ? d.TypeName : d.Title,
                d.OrgName,
                d.TypeCode,
                d.Action.ToString(),
                d.Comments,
                d.DecidedAt,
                scoreMap.TryGetValue(d.DocumentId, out var s) ? s : null))
            .ToList();

        return new ReviewHistoryResult(
            items, filtered.Count, page, pageSize,
            decisions.Count, approvedCount, returnedCount);
    }
}
