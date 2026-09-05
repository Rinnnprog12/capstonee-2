using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Workflow.Queries;

// ─── SOU Workflow Monitor (Figure 22) ────────────────────────────────────────
// University-wide oversight of every active submission in the pipeline.

/// <summary>Figure 22 tabs — All active | Flagged | Stale (idle &gt; 5 days).</summary>
public enum WorkflowMonitorTab
{
    AllActive = 0,
    Flagged = 1,
    Stale = 2,
}

public sealed record GetWorkflowMonitorQuery(
    WorkflowMonitorTab Tab = WorkflowMonitorTab.AllActive,
    int Page = 1,
    int PageSize = 50);

public sealed record WorkflowMonitorItemDto(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string StageLabel,
    string? AssignedTo,
    DateTimeOffset SubmittedAt,
    DateTimeOffset LastActivityAt,
    int DaysElapsed,
    int? Score,
    int FlagCount,
    /// <summary>InReview | Flagged | Clean | Stale — badge on the monitor row.</summary>
    string State);

public sealed record WorkflowMonitorResult(
    IReadOnlyList<WorkflowMonitorItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int AllActiveCount,
    int FlaggedCount,
    int StaleCount);

public sealed class GetWorkflowMonitorHandler
{
    private const int StaleAfterDays = 5;

    private readonly IApplicationDbContext _db;

    public GetWorkflowMonitorHandler(IApplicationDbContext db) => _db = db;

    public async Task<WorkflowMonitorResult> HandleAsync(
        GetWorkflowMonitorQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 100);
        var now = DateTimeOffset.UtcNow;

        var staleAlertsOn = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == "notify.stale_alerts")
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        var enableStale = !bool.TryParse(staleAlertsOn, out var staleFlag) || staleFlag;

        // Active = still moving through the pipeline (not Draft / terminal states).
        var active = await _db.Documents
            .AsNoTracking()
            .Include(d => d.Organization)!.ThenInclude(o => o!.PrimaryAdviser)
            .Include(d => d.DocumentType)
            .Where(d => d.Status == DocumentStatus.Submitted
                || d.Status == DocumentStatus.Validating
                || d.Status == DocumentStatus.Flagged
                || d.Status == DocumentStatus.UnderReview
                || d.Status == DocumentStatus.Returned)
            .OrderBy(d => d.UpdatedAt ?? d.CreatedAt)
            .ToListAsync(ct);

        var docIds = active.Select(d => d.Id).ToList();

        var validations = await _db.AIValidationResults
            .AsNoTracking()
            .Where(v => docIds.Contains(v.DocumentId))
            .GroupBy(v => v.DocumentId)
            .Select(g => g.OrderByDescending(v => v.ProcessedAt).First())
            .ToListAsync(ct);
        var valMap = validations.ToDictionary(v => v.DocumentId);

        var flagCounts = await _db.OCRErrorLogs
            .AsNoTracking()
            .Where(f => docIds.Contains(f.DocumentId))
            .GroupBy(f => f.DocumentId)
            .Select(g => new { DocumentId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var flagMap = flagCounts.ToDictionary(f => f.DocumentId, f => f.Count);

        var all = active.Select(d =>
        {
            valMap.TryGetValue(d.Id, out var v);
            var lastActivity = d.UpdatedAt ?? d.CreatedAt;
            var daysElapsed = Math.Max(0, (int)(now - lastActivity).TotalDays);

            var isFlagged = d.Status == DocumentStatus.Flagged
                || (v is not null
                    && (v.RequiresHumanReview
                        || !v.DocumentClass.Equals("Valid Submission", StringComparison.OrdinalIgnoreCase)));
            var isStale = enableStale && daysElapsed > StaleAfterDays;

            var state = isStale ? "Stale"
                : isFlagged ? "Flagged"
                : v is not null ? "Clean"
                : "InReview";

            return new WorkflowMonitorItemDto(
                d.Id,
                d.DocumentNumber,
                string.IsNullOrWhiteSpace(d.Title) ? (d.DocumentType?.Name ?? d.DocumentNumber) : d.Title,
                d.Organization?.Name ?? "",
                StageLabel(d.Status, d.CurrentStage),
                AssignedLabel(d),
                d.CreatedAt,
                lastActivity,
                daysElapsed,
                v is null ? null : (int)Math.Round(v.Confidence > 1m ? v.Confidence : v.Confidence * 100),
                flagMap.TryGetValue(d.Id, out var fc) ? fc : 0,
                state);
        }).ToList();

        var flaggedCount = all.Count(i => i.State == "Flagged");
        var staleCount = all.Count(i => i.State == "Stale");

        var filtered = q.Tab switch
        {
            WorkflowMonitorTab.Flagged => all.Where(i => i.State == "Flagged").ToList(),
            WorkflowMonitorTab.Stale => all.Where(i => i.State == "Stale").ToList(),
            _ => all,
        };

        var items = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new WorkflowMonitorResult(
            items, filtered.Count, page, pageSize,
            all.Count, flaggedCount, staleCount);
    }

    private static string StageLabel(DocumentStatus status, WorkflowStage stage) => status switch
    {
        DocumentStatus.Submitted  => "Submitted",
        DocumentStatus.Validating => "AI validating",
        DocumentStatus.Flagged    => "Flagged by AI",
        DocumentStatus.Returned   => "Returned",
        DocumentStatus.UnderReview => stage switch
        {
            WorkflowStage.Adviser => "Under review (Adviser)",
            WorkflowStage.Dean    => "Under review (Dean)",
            WorkflowStage.Sou     => "Under review (SOU)",
            _                     => "Under review",
        },
        _ => status.ToString(),
    };

    private static string? AssignedLabel(Domain.Entities.Document d) => d.Status switch
    {
        DocumentStatus.Returned => "Awaiting resubmission from officer",
        DocumentStatus.UnderReview => d.CurrentStage switch
        {
            WorkflowStage.Adviser => d.Organization?.PrimaryAdviser?.FullName ?? "Organization adviser",
            WorkflowStage.Dean    => $"College Dean{(string.IsNullOrWhiteSpace(d.Organization?.College) ? "" : $" ({d.Organization!.College})")}",
            WorkflowStage.Sou     => "SOU staff",
            _                     => null,
        },
        _ => null,
    };
}
