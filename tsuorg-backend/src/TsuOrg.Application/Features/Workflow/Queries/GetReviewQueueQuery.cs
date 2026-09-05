using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Workflow.Queries;

// ─── Review Queue (WM) ───────────────────────────────────────────────────────

/// <summary>Figure 15 tabs — All | Flagged (needs human attention) | Cleaned (AI-passed).</summary>
public enum ReviewQueueTab
{
    All = 0,
    Flagged = 1,
    Cleaned = 2,
}

public sealed record GetReviewQueueQuery(
    Guid RequestingUserId,
    AppRole RequestingRole,
    ReviewQueueTab Tab = ReviewQueueTab.All,
    int Page = 1,
    int PageSize = 20);

public sealed record ReviewQueueItemDto(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string DocumentTypeCode,
    string DocumentTypeName,
    DocumentStatus Status,
    WorkflowStage CurrentStage,
    DateTimeOffset SubmittedAt,
    string SubmittedBy,
    string? LatestValidationClass,
    decimal? LatestValidationConfidence,
    bool RequiresHumanReview);

public sealed record ReviewQueueResult(
    IReadOnlyList<ReviewQueueItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int AllCount,
    int FlaggedCount,
    int CleanedCount);

public sealed class GetReviewQueueHandler
{
    private readonly IApplicationDbContext _db;

    public GetReviewQueueHandler(IApplicationDbContext db) => _db = db;

    public async Task<ReviewQueueResult> HandleAsync(
        GetReviewQueueQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 100);
        var roleCode = q.RequestingRole.ToString();

        // Figure 15 inbox = officer-confirmed packages waiting at THIS reviewer's stage.
        var targetStage = q.RequestingRole switch
        {
            AppRole.Adviser => WorkflowStage.Adviser,
            AppRole.Dean => WorkflowStage.Dean,
            AppRole.SouStaff => WorkflowStage.Sou,
            AppRole.SystemAdmin => (WorkflowStage?)null,
            _ => throw new ForbiddenException("Your role does not have a review queue.")
        };

        var query = _db.Documents
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .Include(d => d.SubmittedByUser)
            .Include(d => d.Workflow)
            .AsNoTracking()
            .Where(d => d.Status == DocumentStatus.UnderReview);

        if (targetStage.HasValue)
        {
            var stage = targetStage.Value;
            query = query.Where(d =>
                (d.Workflow != null && d.Workflow.CurrentStage == stage)
                || (d.Workflow == null && d.CurrentStage == stage));
        }
        else
        {
            query = query.Where(d =>
                d.CurrentStage == WorkflowStage.Adviser
                || d.CurrentStage == WorkflowStage.Dean
                || d.CurrentStage == WorkflowStage.Sou);
        }

        // Adviser = assigned orgs; Dean = college; Sou/Admin = no extra filter.
        query = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
            _db, query, q.RequestingUserId, roleCode, ct);

        var scoped = await query
            .OrderBy(d => d.MetadataLockedAt ?? d.UpdatedAt ?? d.CreatedAt)
            .Select(d => new
            {
                d.Id,
                d.DocumentNumber,
                d.Title,
                OrgName = d.Organization!.Name,
                TypeCode = d.DocumentType!.Code,
                TypeName = d.DocumentType!.Name,
                d.Status,
                Stage = d.Workflow != null ? d.Workflow.CurrentStage : d.CurrentStage,
                SubmittedAt = d.MetadataLockedAt ?? d.UpdatedAt ?? d.CreatedAt,
                SubmittedBy = d.SubmittedByUser!.FullName,
            })
            .ToListAsync(ct);

        var docIds = scoped.Select(p => p.Id).ToList();
        var validations = await _db.AIValidationResults
            .AsNoTracking()
            .Where(v => docIds.Contains(v.DocumentId))
            .GroupBy(v => v.DocumentId)
            .Select(g => g.OrderByDescending(v => v.ProcessedAt).First())
            .ToListAsync(ct);
        var valMap = validations.ToDictionary(v => v.DocumentId);

        var all = scoped.Select(p =>
        {
            valMap.TryGetValue(p.Id, out var v);
            return new ReviewQueueItemDto(
                p.Id,
                p.DocumentNumber,
                string.IsNullOrWhiteSpace(p.Title) ? p.TypeName : p.Title,
                p.OrgName,
                p.TypeCode,
                p.TypeName,
                p.Status,
                p.Stage,
                p.SubmittedAt,
                p.SubmittedBy,
                v?.DocumentClass,
                v?.Confidence,
                IsFlagged(v));
        }).ToList();

        var flaggedCount = all.Count(i => i.RequiresHumanReview);
        var cleanedCount = all.Count - flaggedCount;

        var filtered = q.Tab switch
        {
            ReviewQueueTab.Flagged => all.Where(i => i.RequiresHumanReview).ToList(),
            ReviewQueueTab.Cleaned => all.Where(i => !i.RequiresHumanReview).ToList(),
            _ => all,
        };

        var pageItems = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new ReviewQueueResult(
            pageItems, filtered.Count, page, pageSize,
            all.Count, flaggedCount, cleanedCount);
    }

    /// <summary>
    /// Flagged = needs human attention (incomplete/invalid/low-confidence/no CALSV yet).
    /// Cleaned = AI classed as Valid Submission without human-review flag.
    /// </summary>
    private static bool IsFlagged(AIValidationResult? v)
    {
        if (v is null) return true;
        if (v.RequiresHumanReview) return true;
        return !v.DocumentClass.Equals("Valid Submission", StringComparison.OrdinalIgnoreCase);
    }
}

// Re-use paged result from Documents queries namespace via alias if needed —
// define local alias to avoid circular dependency on Documents.Queries
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

// ─── Workflow History ────────────────────────────────────────────────────────

public sealed record GetWorkflowHistoryQuery(Guid DocumentId);

public sealed record WorkflowHistoryItemDto(
    Guid Id,
    AppRole ActorRole,
    string ActorName,
    ApprovalAction Action,
    string? Comments,
    string? SignatureHash,
    DateTimeOffset DecidedAt);

public sealed record WorkflowHistoryDto(
    Guid DocumentId,
    string DocumentNumber,
    WorkflowStage CurrentStage,
    bool IsComplete,
    IReadOnlyList<WorkflowHistoryItemDto> History);

public sealed class GetWorkflowHistoryHandler
{
    private readonly IApplicationDbContext _db;

    public GetWorkflowHistoryHandler(IApplicationDbContext db) => _db = db;

    public async Task<WorkflowHistoryDto> HandleAsync(
        GetWorkflowHistoryQuery q, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == q.DocumentId, ct)
            ?? throw new NotFoundException("Document", q.DocumentId);

        var workflow = await _db.ApprovalWorkflows
            .Include(w => w.History)
                .ThenInclude(h => h.ActorUser)
            .AsNoTracking()
            .Where(w => w.DocumentId == q.DocumentId)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (workflow is null)
        {
            return new WorkflowHistoryDto(
                doc.Id, doc.DocumentNumber, doc.CurrentStage, false, Array.Empty<WorkflowHistoryItemDto>());
        }

        var history = workflow.History
            .OrderBy(h => h.DecidedAt)
            .Select(h => new WorkflowHistoryItemDto(
                h.Id,
                h.ActorRole,
                h.ActorUser?.FullName ?? "Unknown",
                h.Action,
                h.Comments,
                h.SignatureHash,
                h.DecidedAt))
            .ToList();

        return new WorkflowHistoryDto(
            doc.Id, doc.DocumentNumber, workflow.CurrentStage, workflow.IsComplete, history);
    }
}
