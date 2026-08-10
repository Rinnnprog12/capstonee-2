using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Workflow.Commands;

// ─── Command ────────────────────────────────────────────────────────────────

/// <summary>
/// WM-02 / WM-03: One-click Approve, Reject, or Return for Revision
/// with electronic signature capture and immutable timestamp.
/// </summary>
public sealed record WorkflowDecisionCommand(
    Guid DocumentId,
    Guid ActorUserId,
    AppRole ActorRole,
    ApprovalAction Action,
    string? Comments,
    Stream? SignatureStream,
    string? SignatureFileName,
    string? SignatureContentType);

public sealed record WorkflowDecisionResult(
    Guid DocumentId,
    string DocumentNumber,
    ApprovalAction Action,
    DocumentStatus NewStatus,
    WorkflowStage NewStage,
    DateTimeOffset DecidedAt,
    string? SignatureHash,
    string Message);

// ─── Validator ───────────────────────────────────────────────────────────────

public sealed class WorkflowDecisionValidator : AbstractValidator<WorkflowDecisionCommand>
{
    public WorkflowDecisionValidator()
    {
        RuleFor(x => x.DocumentId).NotEmpty();
        RuleFor(x => x.ActorUserId).NotEmpty();
        RuleFor(x => x.Action).IsInEnum();
        RuleFor(x => x.Comments)
            .NotEmpty()
            .When(x => x.Action is ApprovalAction.Reject or ApprovalAction.Return)
            .WithMessage("Comments are required when Rejecting or Returning a document.");
        RuleFor(x => x.SignatureStream)
            .NotNull()
            .When(x => x.Action == ApprovalAction.Approve)
            .WithMessage("Electronic signature is required for Approve (WM-03).");
    }
}

// ─── Handler ─────────────────────────────────────────────────────────────────

public sealed class WorkflowDecisionHandler
{
    private readonly IApplicationDbContext _db;
    private readonly ISignatureService _signatures;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;
    private readonly ITrackingRecorder _tracking;

    public WorkflowDecisionHandler(
        IApplicationDbContext db,
        ISignatureService signatures,
        IAuditService audit,
        INotificationService notifications,
        ITrackingRecorder tracking)
    {
        _db = db;
        _signatures = signatures;
        _audit = audit;
        _notifications = notifications;
        _tracking = tracking;
    }

    public async Task<WorkflowDecisionResult> HandleAsync(
        WorkflowDecisionCommand cmd, CancellationToken ct = default)
    {
        new WorkflowDecisionValidator().ValidateAndThrow(cmd);

        var doc = await _db.Documents
            .Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.Id == cmd.DocumentId, ct)
            ?? throw new NotFoundException("Document", cmd.DocumentId);

        if (doc.Status != DocumentStatus.UnderReview)
            throw new BusinessRuleException(
                $"Document must be UnderReview to decide. Current status: {doc.Status}.");

        if (!WorkflowStateMachine.CanAct(cmd.ActorRole, doc.CurrentStage))
            throw new ForbiddenException(
                $"Role '{cmd.ActorRole}' cannot act at stage '{doc.CurrentStage}'.");

        var workflow = await _db.ApprovalWorkflows
            .Include(w => w.History)
            .FirstOrDefaultAsync(w => w.DocumentId == cmd.DocumentId && !w.IsComplete, ct)
            ?? throw new BusinessRuleException("No active approval workflow found for this document.");

        // WM-03: capture signature (required for Approve; optional for Reject/Return)
        string? signatureBlob = null;
        string? signatureHash = null;
        var decidedAt = DateTimeOffset.UtcNow;

        if (cmd.SignatureStream is not null && !string.IsNullOrEmpty(cmd.SignatureFileName))
        {
            var capture = await _signatures.CaptureAsync(
                cmd.SignatureStream,
                cmd.SignatureFileName,
                cmd.SignatureContentType ?? "image/png",
                cmd.ActorUserId,
                cmd.DocumentId,
                ct);
            signatureBlob = capture.BlobPath;
            signatureHash = capture.SignatureHash;
            decidedAt = capture.CapturedAt;
        }
        else if (cmd.Action == ApprovalAction.Approve)
        {
            throw new BusinessRuleException("Electronic signature is required for Approve (WM-03).");
        }

        var (newStatus, newStage, workflowComplete) = cmd.Action switch
        {
            ApprovalAction.Approve => WorkflowStateMachine.ApplyApprove(doc.CurrentStage),
            ApprovalAction.Reject  => WorkflowStateMachine.ApplyReject(),
            ApprovalAction.Return  => WorkflowStateMachine.ApplyReturn(),
            _ => throw new BusinessRuleException($"Unsupported action: {cmd.Action}")
        };

        // Persist approval history
        var history = new ApprovalHistory
        {
            WorkflowId        = workflow.Id,
            ActorUserId       = cmd.ActorUserId,
            ActorRole         = cmd.ActorRole,
            Action            = cmd.Action,
            Comments          = cmd.Comments,
            SignatureBlobPath = signatureBlob,
            SignatureHash     = signatureHash,
            DecidedAt         = decidedAt,
        };
        _db.ApprovalHistories.Add(history);

        // Update document + workflow
        doc.Status       = newStatus;
        doc.CurrentStage = newStage;
        doc.UpdatedAt    = decidedAt;

        workflow.CurrentStage = newStage;
        workflow.IsComplete   = workflowComplete;
        workflow.UpdatedAt    = decidedAt;

        // If SOU approved → archive immediately (DMA hand-off)
        if (cmd.Action == ApprovalAction.Approve && workflowComplete)
            doc.Status = DocumentStatus.Archived;

        await _tracking.RecordAsync(
            doc.Id,
            doc.Status,
            newStage,
            BuildTrackingMessage(cmd.Action, cmd.ActorRole, newStage, cmd.Comments),
            cmd.ActorUserId,
            ct);

        // Version snapshot on Return so officer resubmit creates a new version path
        if (cmd.Action == ApprovalAction.Return)
        {
            var nextVersion = await _db.DocumentVersions
                .Where(v => v.DocumentId == doc.Id)
                .Select(v => (int?)v.VersionNumber)
                .MaxAsync(ct) ?? 0;

            _db.DocumentVersions.Add(new DocumentVersion
            {
                DocumentId    = doc.Id,
                VersionNumber = nextVersion + 1,
                BlobPath      = doc.PrimaryFileBlobPath ?? string.Empty,
                ChangeSummary = $"Returned by {cmd.ActorRole}: {cmd.Comments}",
            });
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            action: $"Workflow.{cmd.Action}",
            entityName: nameof(Document),
            entityId: doc.Id.ToString(),
            details: new
            {
                doc.DocumentNumber,
                cmd.Action,
                cmd.ActorRole,
                NewStatus = doc.Status,
                NewStage  = newStage,
                signatureHash,
                decidedAt,
            },
            actorUserId: cmd.ActorUserId,
            ct: ct);

        // Notify submitter
        await _notifications.SendAsync(
            userId: doc.SubmittedByUserId,
            title: $"Document {cmd.Action}: {doc.DocumentNumber}",
            message: BuildNotificationMessage(cmd.Action, cmd.ActorRole, newStage, cmd.Comments),
            relatedDocumentId: doc.Id,
            ct: ct);

        return new WorkflowDecisionResult(
            doc.Id,
            doc.DocumentNumber,
            cmd.Action,
            doc.Status,
            newStage,
            decidedAt,
            signatureHash,
            BuildTrackingMessage(cmd.Action, cmd.ActorRole, newStage, cmd.Comments));
    }

    private static string BuildTrackingMessage(
        ApprovalAction action, AppRole role, WorkflowStage stage, string? comments)
    {
        var baseMsg = action switch
        {
            ApprovalAction.Approve => $"{role} approved. Advanced to {stage}.",
            ApprovalAction.Reject  => $"{role} rejected the submission.",
            ApprovalAction.Return  => $"{role} returned for revision.",
            _ => $"{role} performed {action}."
        };
        return string.IsNullOrWhiteSpace(comments) ? baseMsg : $"{baseMsg} Comments: {comments}";
    }

    private static string BuildNotificationMessage(
        ApprovalAction action, AppRole role, WorkflowStage stage, string? comments)
        => BuildTrackingMessage(action, role, stage, comments);
}
