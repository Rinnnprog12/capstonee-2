using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Workflow.Queries;

// ─── SOU System Audit Log (Figure 28) ────────────────────────────────────────
// Platform-wide immutable timeline from TrackingHistory (upload → CALSV → decisions).

public sealed record GetSystemAuditLogQuery(int Page = 1, int PageSize = 50);

public sealed record SystemAuditItemDto(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string DocumentTypeCode,
    /// <summary>Display action: Submitted | Approved | Returned | Rejected | …</summary>
    string Action,
    string ByName,
    string ByRole,
    DateTimeOffset At);

public sealed record SystemAuditResult(
    IReadOnlyList<SystemAuditItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed class GetSystemAuditLogHandler
{
    private readonly IApplicationDbContext _db;

    public GetSystemAuditLogHandler(IApplicationDbContext db) => _db = db;

    public async Task<SystemAuditResult> HandleAsync(
        GetSystemAuditLogQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);

        var baseQuery = _db.TrackingHistories
            .AsNoTracking()
            .Where(t => t.Document!.Status != DocumentStatus.Draft);

        var total = await baseQuery.CountAsync(ct);

        var events = await baseQuery
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                t.DocumentId,
                t.Document!.DocumentNumber,
                t.Document.Title,
                TypeCode = t.Document.DocumentType!.Code,
                TypeName = t.Document.DocumentType!.Name,
                t.Status,
                t.Stage,
                t.Message,
                t.ActorUserId,
                t.CreatedAt,
                SubmitterName = t.Document.SubmittedByUser != null
                    ? t.Document.SubmittedByUser.FullName
                    : null,
            })
            .ToListAsync(ct);

        var actorIds = events
            .Where(e => e.ActorUserId is Guid)
            .Select(e => e.ActorUserId!.Value)
            .Distinct()
            .ToList();

        var actors = actorIds.Count == 0
            ? new Dictionary<Guid, (string Name, string Role)>()
            : await _db.UserAccounts
                .AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, Role = u.Role!.Code })
                .ToDictionaryAsync(u => u.Id, u => (u.FullName, u.Role), ct);

        var items = events.Select(e =>
        {
            var action = MapAction(e.Status, e.Stage, e.Message);
            var (byName, byRole) = ResolveActor(
                e.ActorUserId,
                actors,
                e.Stage,
                e.SubmitterName,
                action);

            var title = string.IsNullOrWhiteSpace(e.Title) ? e.TypeName : e.Title;

            return new SystemAuditItemDto(
                e.DocumentId,
                e.DocumentNumber,
                title,
                e.TypeCode,
                action,
                byName,
                byRole,
                e.CreatedAt);
        }).ToList();

        return new SystemAuditResult(items, total, page, pageSize);
    }

    private static string MapAction(DocumentStatus status, WorkflowStage stage, string message)
    {
        var msg = message ?? "";

        if (msg.Contains("OCR", StringComparison.OrdinalIgnoreCase))
            return "OCR scanned";
        if (msg.Contains("AI validation flagged", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("requires revision", StringComparison.OrdinalIgnoreCase))
            return "AI flagged";
        if (msg.Contains("AI validation", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("CALSV", StringComparison.OrdinalIgnoreCase))
            return "AI validated";
        if (msg.Contains("Routed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("confirmed", StringComparison.OrdinalIgnoreCase))
            return "Confirmed";
        if (msg.Contains("returned", StringComparison.OrdinalIgnoreCase)
            || status == DocumentStatus.Returned)
            return "Returned";
        if (msg.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || status == DocumentStatus.Rejected)
            return "Rejected";
        if (msg.Contains("approved", StringComparison.OrdinalIgnoreCase)
            || status is DocumentStatus.Approved or DocumentStatus.Archived)
            return "Approved";
        if (msg.Contains("submitted", StringComparison.OrdinalIgnoreCase)
            || status == DocumentStatus.Submitted
            || stage == WorkflowStage.Officer)
            return "Submitted";

        return status.ToString();
    }

    private static (string Name, string Role) ResolveActor(
        Guid? actorUserId,
        IReadOnlyDictionary<Guid, (string Name, string Role)> actors,
        WorkflowStage stage,
        string? submitterName,
        string action)
    {
        if (actorUserId is Guid id && actors.TryGetValue(id, out var actor))
            return (actor.Name, FriendlyRole(actor.Role));

        if (action is "OCR scanned" or "AI validated" or "AI flagged")
            return ("CALSV Engine", "System");

        if (!string.IsNullOrWhiteSpace(submitterName)
            && action is "Submitted" or "Confirmed")
            return (submitterName, "Officer");

        return stage switch
        {
            WorkflowStage.Officer => (submitterName ?? "Officer", "Officer"),
            WorkflowStage.Calsv => ("CALSV Engine", "System"),
            WorkflowStage.Adviser => ("Adviser", "Adviser"),
            WorkflowStage.Dean => ("Dean", "Dean"),
            WorkflowStage.Sou => ("SOU Staff", "SOU"),
            _ => ("System", "System"),
        };
    }

    private static string FriendlyRole(string roleCode) => roleCode switch
    {
        "OrgOfficer" => "Officer",
        "SouStaff" => "SOU",
        "SystemAdmin" => "Admin",
        _ => roleCode,
    };
}
