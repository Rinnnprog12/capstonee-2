using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Workflow.Queries;

// ─── Figure 20: Adviser / Dean Audit Log (org / college scoped) ────────────────

public sealed record GetScopedAuditLogQuery(
    Guid RequestingUserId,
    string RequestingRole,
    int Page = 1,
    int PageSize = 50);

/// <summary>
/// Ordered activity rows for documents in the reviewer's visibility scope.
/// Source of truth: TrackingHistories (OCR, AI, confirm, decisions).
/// </summary>
public sealed record ScopedAuditItemDto(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string DocumentTypeCode,
    string DocumentTypeName,
    string Activity,
    string ActivityKind,
    DateTimeOffset At);

public sealed record ScopedAuditResult(
    IReadOnlyList<ScopedAuditItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed class GetScopedAuditLogHandler
{
    private readonly IApplicationDbContext _db;

    public GetScopedAuditLogHandler(IApplicationDbContext db) => _db = db;

    public async Task<ScopedAuditResult> HandleAsync(
        GetScopedAuditLogQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);

        if (q.RequestingRole is not ("Adviser" or "Dean"))
            throw new ForbiddenException("Scoped audit log is for Adviser and Dean roles.");

        var docs = _db.Documents
            .AsNoTracking()
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .Where(d => d.Status != DocumentStatus.Draft);

        docs = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
            _db, docs, q.RequestingUserId, q.RequestingRole, ct);

        var docMeta = await docs
            .Select(d => new
            {
                d.Id,
                d.DocumentNumber,
                d.Title,
                TypeCode = d.DocumentType!.Code,
                TypeName = d.DocumentType!.Name,
            })
            .ToListAsync(ct);

        if (docMeta.Count == 0)
            return new ScopedAuditResult([], 0, page, pageSize);

        var docIds = docMeta.Select(d => d.Id).ToList();
        var metaMap = docMeta.ToDictionary(d => d.Id);

        var events = await _db.TrackingHistories
            .AsNoTracking()
            .Where(t => docIds.Contains(t.DocumentId))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                t.DocumentId,
                t.Status,
                t.Stage,
                t.Message,
                t.CreatedAt,
            })
            .ToListAsync(ct);

        var all = events
            .Where(e => metaMap.ContainsKey(e.DocumentId))
            .Select(e =>
            {
                var doc = metaMap[e.DocumentId];
                var kind = Classify(e.Status, e.Stage, e.Message);
                var title = string.IsNullOrWhiteSpace(doc.Title) ? doc.TypeName : doc.Title;
                var activity = string.IsNullOrWhiteSpace(e.Message)
                    ? $"{e.Status} · {e.Stage}"
                    : e.Message;
                return new ScopedAuditItemDto(
                    e.DocumentId,
                    doc.DocumentNumber,
                    title,
                    doc.TypeCode,
                    doc.TypeName,
                    activity,
                    kind,
                    e.CreatedAt);
            })
            .ToList();

        var items = all
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new ScopedAuditResult(items, all.Count, page, pageSize);
    }

    private static string Classify(DocumentStatus status, WorkflowStage stage, string message)
    {
        var msg = message ?? "";
        if (msg.Contains("OCR", StringComparison.OrdinalIgnoreCase))
            return "OCR";
        if (msg.Contains("AI validation", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("CALSV", StringComparison.OrdinalIgnoreCase))
            return "AI";
        if (msg.Contains("Routed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("confirmed", StringComparison.OrdinalIgnoreCase))
            return "Confirm";
        if (msg.Contains("approved", StringComparison.OrdinalIgnoreCase)
            || status == DocumentStatus.Approved)
            return "Approve";
        if (msg.Contains("returned", StringComparison.OrdinalIgnoreCase)
            || status == DocumentStatus.Returned)
            return "Return";
        if (msg.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || status == DocumentStatus.Rejected)
            return "Reject";
        if (status == DocumentStatus.Submitted || stage == WorkflowStage.Officer)
            return "Submit";
        return "Other";
    }
}
