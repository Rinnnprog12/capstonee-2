using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
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
    DateTimeOffset OccurredAt);

public sealed record TrackingTimelineDto(
    Guid DocumentId,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string DocumentTypeCode,
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

        var events = await _db.TrackingHistories
            .AsNoTracking()
            .Where(t => t.DocumentId == q.DocumentId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new TrackingEventDto(
                t.Id,
                t.Status,
                t.Stage,
                t.Message,
                t.ActorUserId,
                t.CreatedAt))
            .ToListAsync(ct);

        var timeline = DocumentTimelineProjector.Project(doc.Status, doc.CurrentStage);

        return new TrackingTimelineDto(
            doc.Id,
            doc.DocumentNumber,
            string.IsNullOrWhiteSpace(doc.Title) ? (doc.DocumentType?.Code ?? "") : doc.Title,
            doc.Organization?.Name ?? "",
            doc.DocumentType?.Code ?? "",
            doc.Status,
            doc.CurrentStage,
            timeline.CurrentIndex,
            timeline.OverallStatus,
            timeline.OverallTone,
            timeline.Steps,
            events);
    }
}
