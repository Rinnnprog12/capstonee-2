using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Documents.Commands;

public sealed record RerunValidationCommand(Guid DocumentId, Guid ActorUserId);

public sealed record RerunValidationResult(Guid DocumentId, DocumentStatus Status, string Message);

public sealed class RerunValidationHandler
{
    private readonly IApplicationDbContext _db;
    private readonly ICalsvJobQueue _queue;
    private readonly IAuditService _audit;
    private readonly ITrackingRecorder _tracking;

    public RerunValidationHandler(
        IApplicationDbContext db,
        ICalsvJobQueue queue,
        IAuditService audit,
        ITrackingRecorder tracking)
    {
        _db = db;
        _queue = queue;
        _audit = audit;
        _tracking = tracking;
    }

    public async Task<RerunValidationResult> HandleAsync(
        RerunValidationCommand cmd, CancellationToken ct = default)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == cmd.DocumentId, ct)
            ?? throw new NotFoundException("Document", cmd.DocumentId);

        if (string.IsNullOrEmpty(doc.PrimaryFileBlobPath))
            throw new BusinessRuleException("Primary file required before re-running validation.");

        if (doc.Status is DocumentStatus.Validating or DocumentStatus.Submitted)
            throw new BusinessRuleException("Validation is already in progress.");

        doc.Status       = DocumentStatus.Submitted;
        doc.CurrentStage = WorkflowStage.Calsv;
        doc.UpdatedAt    = DateTimeOffset.UtcNow;

        await _tracking.RecordAsync(
            doc.Id,
            DocumentStatus.Submitted,
            WorkflowStage.Calsv,
            "CALSV re-run requested.",
            cmd.ActorUserId,
            ct);

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("Document.CalsvRerun", nameof(Domain.Entities.Document),
            doc.Id.ToString(), new { doc.DocumentNumber }, cmd.ActorUserId, ct);

        await _queue.EnqueueAsync(new CalsvJob(doc.Id, cmd.ActorUserId), ct);

        return new RerunValidationResult(doc.Id, doc.Status, "CALSV re-run queued.");
    }
}
