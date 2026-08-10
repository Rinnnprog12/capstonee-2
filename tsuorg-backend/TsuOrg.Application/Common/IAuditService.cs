namespace TsuOrg.Application.Common;

public interface IAuditService
{
    Task LogAsync(
        string action,
        string entityName,
        string? entityId = null,
        object? details = null,
        Guid? actorUserId = null,
        CancellationToken ct = default);
}
