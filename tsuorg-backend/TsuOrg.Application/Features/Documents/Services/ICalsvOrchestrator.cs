using TsuOrg.Application.Common;

namespace TsuOrg.Application.Features.Documents.Services;

/// <summary>
/// Runs CALSV for one document inside a fresh DI scope (safe for background workers).
/// </summary>
public interface ICalsvOrchestrator
{
    Task ProcessAsync(Guid documentId, Guid? triggeredByUserId = null, CancellationToken ct = default);
}
