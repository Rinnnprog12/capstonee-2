using System.Text.Json;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Infrastructure.Persistence;

namespace TsuOrg.Infrastructure.Services;

public sealed class AuditService : IAuditService
{
    private readonly TsuOrgDbContext _db;

    public AuditService(TsuOrgDbContext db) => _db = db;

    public async Task LogAsync(
        string action,
        string entityName,
        string? entityId = null,
        object? details = null,
        Guid? actorUserId = null,
        CancellationToken ct = default)
    {
        var entry = new AuditLog
        {
            ActorUserId = actorUserId,
            Action      = action,
            EntityName  = entityName,
            EntityId    = entityId,
            DetailsJson = details is null
                ? "{}"
                : JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = false }),
        };

        _db.AuditLogs.Add(entry);
        await _db.SaveChangesAsync(ct);
    }
}
