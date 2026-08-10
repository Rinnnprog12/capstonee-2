using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Common;

/// <summary>
/// Appends immutable tracking events for DTM timeline (TrackingHistory).
/// Workflow / CALSV / submit handlers call this instead of writing histories ad hoc.
/// </summary>
public interface ITrackingRecorder
{
    Task RecordAsync(
        Guid documentId,
        DocumentStatus status,
        WorkflowStage stage,
        string message,
        Guid? actorUserId = null,
        CancellationToken ct = default);
}
