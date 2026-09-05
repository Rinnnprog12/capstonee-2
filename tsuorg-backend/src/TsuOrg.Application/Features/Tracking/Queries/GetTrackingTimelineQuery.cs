using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking.Queries;

// ─── DTM-02: Single-document timeline + audit events ────────────────────────

public sealed record GetTrackingTimelineQuery(
    Guid DocumentId,
    Guid RequestingUserId,
    string RequestingRole);

public sealed record TrackingEventDto(
    Guid Id,
    DocumentStatus Status,
    WorkflowStage Stage,
    string Message,
    Guid? ActorUserId,
    DateTimeOffset OccurredAt,
    string Headline = "",
    string Detail = "");

public sealed record TrackingTimelineDto(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string DocumentTypeCode,
    string DocumentTypeName,
    string SubmittedBy,
    string? SubmittedByPosition,
    string? AdviserName,
    int? Score,
    DocumentStatus CurrentStatus,
    WorkflowStage CurrentStage,
    int CurrentIndex,
    string OverallStatus,
    string OverallTone,
    IReadOnlyList<TimelineStepDto> Steps,
    IReadOnlyList<TrackingEventDto> Events);

public sealed class GetTrackingTimelineHandler
{
    private readonly IApplicationDbContext _db;

    public GetTrackingTimelineHandler(IApplicationDbContext db) => _db = db;

    public async Task<TrackingTimelineDto> HandleAsync(
        GetTrackingTimelineQuery q, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .AsNoTracking()
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .Include(d => d.SubmittedByUser)
            .FirstOrDefaultAsync(d => d.Id == q.DocumentId, ct)
            ?? throw new NotFoundException("Document", q.DocumentId);

        DocumentVisibility.EnsureCanViewDocument(doc, q.RequestingUserId, q.RequestingRole);

        if (q.RequestingRole is "Adviser" or "Dean")
        {
            var scoped = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
                _db,
                _db.Documents.AsNoTracking().Where(d => d.Id == q.DocumentId),
                q.RequestingUserId,
                q.RequestingRole,
                ct);

            if (!await scoped.AnyAsync(ct))
                throw new ForbiddenException("Access denied to this document timeline.");
        }

        var eventRows = await _db.TrackingHistories
            .AsNoTracking()
            .Where(t => t.DocumentId == q.DocumentId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new
            {
                t.Id,
                t.Status,
                t.Stage,
                t.Message,
                t.ActorUserId,
                t.CreatedAt,
            })
            .ToListAsync(ct);

        var events = eventRows.Select(t =>
        {
            var (headline, detail) = SplitAuditMessage(t.Message);
            return new TrackingEventDto(
                t.Id,
                t.Status,
                t.Stage,
                t.Message,
                t.ActorUserId,
                t.CreatedAt,
                headline,
                detail);
        }).ToList();

        var stage = await _db.ApprovalWorkflows.AsNoTracking()
            .Where(w => w.DocumentId == q.DocumentId)
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => (WorkflowStage?)w.CurrentStage)
            .FirstOrDefaultAsync(ct) ?? doc.CurrentStage;

        // Adviser shown in the Figure 18 details panel: primary adviser first,
        // otherwise the active Adviser membership on the owning organization.
        string? adviserName = null;
        if (doc.Organization?.PrimaryAdviserUserId is Guid primaryAdviserId)
        {
            adviserName = await _db.UserAccounts
                .AsNoTracking()
                .Where(u => u.Id == primaryAdviserId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(ct);
        }
        adviserName ??= await _db.OrganizationMemberships
            .AsNoTracking()
            .Where(m =>
                m.OrganizationId == doc.OrganizationId
                && m.IsActive
                && m.MembershipRole == MembershipRole.Adviser)
            .Select(m => m.UserAccount!.FullName)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(adviserName)
            && !string.IsNullOrWhiteSpace(doc.Organization?.College))
            adviserName = $"{adviserName} ({doc.Organization.College})";

        var submittedByPosition = await _db.OrganizationMemberships
            .AsNoTracking()
            .Where(m =>
                m.OrganizationId == doc.OrganizationId
                && m.UserAccountId == doc.SubmittedByUserId
                && m.IsActive)
            .OrderByDescending(m => m.MembershipRole == MembershipRole.Officer)
            .Select(m => m.PositionTitle)
            .FirstOrDefaultAsync(ct);

        var latestValidation = await _db.AIValidationResults
            .AsNoTracking()
            .Where(v => v.DocumentId == q.DocumentId)
            .OrderByDescending(v => v.ProcessedAt)
            .FirstOrDefaultAsync(ct);
        int? score = ScoreNormalizer.ToPercentOrNull(latestValidation?.Confidence);

        var projected = DocumentTimelineProjector.Project(doc.Status, stage);
        var submittedAt = doc.MetadataLockedAt ?? doc.CreatedAt;
        var steps = EnrichMilestoneTimestamps(
            projected.Steps,
            events,
            latestValidation?.ProcessedAt,
            submittedAt);

        return new TrackingTimelineDto(
            doc.Id,
            doc.DocumentNumber,
            string.IsNullOrWhiteSpace(doc.Title) ? (doc.DocumentType?.Name ?? doc.DocumentNumber) : doc.Title,
            doc.Organization?.Name ?? "",
            doc.DocumentType?.Code ?? "",
            doc.DocumentType?.Name ?? "",
            doc.SubmittedByUser?.FullName ?? "",
            string.IsNullOrWhiteSpace(submittedByPosition) ? null : submittedByPosition,
            adviserName,
            score,
            doc.Status,
            stage,
            projected.CurrentIndex,
            projected.OverallStatus,
            projected.OverallTone,
            steps,
            events);
    }

    /// <summary>
    /// Figure 18 milestone captions: completed steps show real DB timestamps when available.
    /// </summary>
    private static IReadOnlyList<TimelineStepDto> EnrichMilestoneTimestamps(
        IReadOnlyList<TimelineStepDto> steps,
        IReadOnlyList<TrackingEventDto> events,
        DateTimeOffset? validationAt,
        DateTimeOffset submittedAt)
    {
        DateTimeOffset? TimeAt(Func<TrackingEventDto, bool> pred) =>
            events.Where(pred).Select(e => (DateTimeOffset?)e.OccurredAt).FirstOrDefault();

        var times = new DateTimeOffset?[]
        {
            TimeAt(e =>
                    (e.Status is DocumentStatus.Submitted or DocumentStatus.UnderReview)
                    && e.Stage is WorkflowStage.Officer or WorkflowStage.Adviser or WorkflowStage.Calsv)
                ?? submittedAt,
            validationAt
                ?? TimeAt(e => e.Stage == WorkflowStage.Calsv
                               || e.Status is DocumentStatus.Validating
                                   or DocumentStatus.Flagged
                                   or DocumentStatus.Submitted),
            TimeAt(e => e.Stage == WorkflowStage.Adviser),
            TimeAt(e => e.Stage == WorkflowStage.Dean),
            TimeAt(e => e.Stage is WorkflowStage.Sou or WorkflowStage.Done
                        || e.Status is DocumentStatus.Approved or DocumentStatus.Archived),
        };

        return steps.Select((step, i) =>
        {
            var at = i < times.Length ? times[i] : null;
            if (at is null || step.Tone == "upcoming")
                return step;

            var label = at.Value.ToLocalTime().ToString("HH:mm");
            var state = step.Tone == "completed" ? label : step.State;
            return step with { State = state, OccurredAt = at };
        }).ToList();
    }

    /// <summary>Figure 25: first sentence is the headline; remainder is the detail line.</summary>
    private static (string Headline, string Detail) SplitAuditMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return ("Update", "");

        var text = message.Trim();
        var idx = text.IndexOf(". ", StringComparison.Ordinal);
        if (idx < 0)
            return (text.TrimEnd('.'), "");

        return (text[..idx], text[(idx + 2)..].Trim());
    }
}
