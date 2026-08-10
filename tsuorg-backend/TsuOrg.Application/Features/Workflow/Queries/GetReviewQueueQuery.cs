using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Workflow.Queries;

// ─── Review Queue (WM) ───────────────────────────────────────────────────────

public sealed record GetReviewQueueQuery(
    Guid RequestingUserId,
    AppRole RequestingRole,
    int Page = 1,
    int PageSize = 20);

public sealed record ReviewQueueItemDto(
    Guid DocumentId,
    string DocumentNumber,
    string OrganizationName,
    string DocumentTypeCode,
    DocumentStatus Status,
    WorkflowStage CurrentStage,
    DateTimeOffset SubmittedAt,
    string SubmittedBy,
    string? LatestValidationClass,
    decimal? LatestValidationConfidence);

public sealed class GetReviewQueueHandler
{
    private readonly IApplicationDbContext _db;

    public GetReviewQueueHandler(IApplicationDbContext db) => _db = db;

    public async Task<PagedResult<ReviewQueueItemDto>> HandleAsync(
        GetReviewQueueQuery q, CancellationToken ct = default)
    {
        var roleCode = q.RequestingRole.ToString();

        var targetStage = q.RequestingRole switch
        {
            AppRole.Adviser     => WorkflowStage.Adviser,
            AppRole.Dean        => WorkflowStage.Dean,
            AppRole.SouStaff    => WorkflowStage.Sou,
            AppRole.SystemAdmin => (WorkflowStage?)null,
            _ => throw new ForbiddenException("Your role does not have a review queue.")
        };

        var query = _db.Documents
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .Include(d => d.SubmittedByUser)
            .AsNoTracking()
            .Where(d => d.Status == DocumentStatus.UnderReview);

        if (targetStage.HasValue)
            query = query.Where(d => d.CurrentStage == targetStage.Value);
        else
            query = query.Where(d =>
                d.CurrentStage == WorkflowStage.Adviser
                || d.CurrentStage == WorkflowStage.Dean
                || d.CurrentStage == WorkflowStage.Sou);

        // Adviser = assigned orgs; Dean = college-wide; Sou/Admin = global
        query = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
            _db, query, q.RequestingUserId, roleCode, ct);

        var total = await query.CountAsync(ct);

        var page = await query
            .OrderBy(d => d.UpdatedAt ?? d.CreatedAt)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(Math.Min(q.PageSize, 100))
            .Select(d => new
            {
                d.Id,
                d.DocumentNumber,
                OrgName = d.Organization!.Name,
                TypeCode = d.DocumentType!.Code,
                d.Status,
                d.CurrentStage,
                d.CreatedAt,
                SubmittedBy = d.SubmittedByUser!.FullName,
            })
            .ToListAsync(ct);

        var docIds = page.Select(p => p.Id).ToList();
        var validations = await _db.AIValidationResults
            .AsNoTracking()
            .Where(v => docIds.Contains(v.DocumentId))
            .GroupBy(v => v.DocumentId)
            .Select(g => g.OrderByDescending(v => v.ProcessedAt).First())
            .ToListAsync(ct);

        var valMap = validations.ToDictionary(v => v.DocumentId);

        var items = page.Select(p =>
        {
            valMap.TryGetValue(p.Id, out var v);
            return new ReviewQueueItemDto(
                p.Id,
                p.DocumentNumber,
                p.OrgName,
                p.TypeCode,
                p.Status,
                p.CurrentStage,
                p.CreatedAt,
                p.SubmittedBy,
                v?.DocumentClass,
                v?.Confidence);
        }).ToList();

        return new PagedResult<ReviewQueueItemDto>(items, total, q.Page, q.PageSize);
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
