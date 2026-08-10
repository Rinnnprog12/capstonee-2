using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Infrastructure.Services;

/// <summary>
/// Stages a TrackingHistory row. Caller owns <c>SaveChangesAsync</c>
/// so events stay in the same unit of work as status transitions.
/// </summary>
public sealed class TrackingRecorder : ITrackingRecorder
{
    private readonly IApplicationDbContext _db;

    public TrackingRecorder(IApplicationDbContext db) => _db = db;

    public Task RecordAsync(
        Guid documentId,
        DocumentStatus status,
        WorkflowStage stage,
        string message,
        Guid? actorUserId = null,
        CancellationToken ct = default)
    {
        _db.TrackingHistories.Add(new TrackingHistory
        {
            DocumentId  = documentId,
            Status      = status,
            Stage       = stage,
            Message     = message,
            ActorUserId = actorUserId,
        });
        return Task.CompletedTask;
    }
}
