namespace TsuOrg.Application.Common;

/// <summary>
/// Fire-and-forget queue for CALSV jobs. Implementations must create a new DI scope
/// when processing so DbContext is not disposed with the HTTP request.
/// </summary>
public interface ICalsvJobQueue
{
    ValueTask EnqueueAsync(CalsvJob job, CancellationToken ct = default);
    IAsyncEnumerable<CalsvJob> DequeueAllAsync(CancellationToken ct);
}

public sealed record CalsvJob(Guid DocumentId, Guid? TriggeredByUserId = null);
