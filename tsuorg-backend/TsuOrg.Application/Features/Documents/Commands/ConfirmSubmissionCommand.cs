using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Documents.Commands;

/// <summary>
/// Figure 10 — Final confirmation after CALSV (Steps 3–4).
/// Locks metadata, writes SHA-256 audit trail, routes document to Adviser Review Queue.
/// Blocked when validation is Incomplete / Attachment Missing / Structurally Invalid.
/// </summary>
public sealed record ConfirmSubmissionCommand(Guid DocumentId, Guid ActorUserId);

public sealed record ConfirmSubmissionResult(
    Guid DocumentId,
    string DocumentNumber,
    DocumentStatus Status,
    WorkflowStage CurrentStage,
    bool IsMetadataLocked,
    string MetadataLockHash,
    DateTimeOffset MetadataLockedAt,
    string ValidationStatus,
    string Message);

public sealed class ConfirmSubmissionValidator : AbstractValidator<ConfirmSubmissionCommand>
{
    public ConfirmSubmissionValidator()
    {
        RuleFor(x => x.DocumentId).NotEmpty();
        RuleFor(x => x.ActorUserId).NotEmpty();
    }
}

public sealed class ConfirmSubmissionHandler
{
    private static readonly HashSet<string> ConfirmableClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Valid Submission",
        "Requires Human Review",
    };

    private readonly IApplicationDbContext _db;
    private readonly IAuditService _audit;
    private readonly ITrackingRecorder _tracking;
    private readonly INotificationService _notifications;

    public ConfirmSubmissionHandler(
        IApplicationDbContext db,
        IAuditService audit,
        ITrackingRecorder tracking,
        INotificationService notifications)
    {
        _db = db;
        _audit = audit;
        _tracking = tracking;
        _notifications = notifications;
    }

    public async Task<ConfirmSubmissionResult> HandleAsync(
        ConfirmSubmissionCommand cmd, CancellationToken ct = default)
    {
        new ConfirmSubmissionValidator().ValidateAndThrow(cmd);

        var doc = await _db.Documents
            .Include(d => d.Attachments)
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .FirstOrDefaultAsync(d => d.Id == cmd.DocumentId, ct)
            ?? throw new NotFoundException("Document", cmd.DocumentId);

        if (doc.SubmittedByUserId != cmd.ActorUserId)
            throw new ForbiddenException("Only the document owner can confirm submission.");

        var latestVal = await _db.AIValidationResults
            .AsNoTracking()
            .Where(v => v.DocumentId == doc.Id)
            .OrderByDescending(v => v.ProcessedAt)
            .FirstOrDefaultAsync(ct);

        // Idempotent: already locked → return current snapshot
        if (doc.IsMetadataLocked)
        {
            return new ConfirmSubmissionResult(
                doc.Id,
                doc.DocumentNumber,
                doc.Status,
                doc.CurrentStage,
                true,
                doc.MetadataLockHash ?? string.Empty,
                doc.MetadataLockedAt ?? DateTimeOffset.UtcNow,
                FormatValidationStatus(latestVal),
                "Submission already confirmed. Metadata is locked.");
        }

        if (latestVal is null)
            throw new BusinessRuleException(
                "CALSV validation has not completed. Finish the OCR and Validation steps first.");

        if (!ConfirmableClasses.Contains(latestVal.DocumentClass))
            throw new BusinessRuleException(
                $"Cannot confirm: validation status is '{latestVal.DocumentClass}'. " +
                "Fix the flagged issues and re-run validation before confirming.");

        if (doc.Status is DocumentStatus.Draft or DocumentStatus.Validating or DocumentStatus.Submitted)
            throw new BusinessRuleException(
                "Document is still being processed. Wait for CALSV to finish before confirming.");

        if (doc.Status is DocumentStatus.Flagged or DocumentStatus.Returned)
            throw new BusinessRuleException(
                "This submission requires revision before it can be confirmed for review.");

        if (doc.Status is DocumentStatus.Rejected or DocumentStatus.Approved or DocumentStatus.Archived)
            throw new BusinessRuleException($"Document cannot be confirmed from '{doc.Status}' status.");

        var lockedAt = DateTimeOffset.UtcNow;
        var hash = ComputeMetadataHash(doc, latestVal);

        doc.IsMetadataLocked  = true;
        doc.MetadataLockHash  = hash;
        doc.MetadataLockedAt  = lockedAt;
        doc.Status            = DocumentStatus.UnderReview;
        doc.CurrentStage      = WorkflowStage.Adviser;
        doc.UpdatedAt         = lockedAt;

        await EnsureApprovalWorkflowAsync(doc, lockedAt, ct);

        await _tracking.RecordAsync(
            doc.Id,
            DocumentStatus.UnderReview,
            WorkflowStage.Adviser,
            $"Submission confirmed. Metadata locked (SHA-256 {hash[..12]}…). Routed to Adviser review.",
            cmd.ActorUserId,
            ct);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "Document.Confirmed",
            nameof(Document),
            doc.Id.ToString(),
            new
            {
                doc.DocumentNumber,
                MetadataLockHash = hash,
                lockedAt,
                latestVal.DocumentClass,
                latestVal.Confidence,
                RoutedTo = WorkflowStage.Adviser.ToString(),
            },
            cmd.ActorUserId,
            ct);

        await _notifications.SendAsync(
            userId: doc.SubmittedByUserId,
            title: $"Submission confirmed: {doc.DocumentNumber}",
            message: "Metadata is locked. Your document is now in the Adviser review queue.",
            relatedDocumentId: doc.Id,
            ct: ct);

        return new ConfirmSubmissionResult(
            doc.Id,
            doc.DocumentNumber,
            doc.Status,
            doc.CurrentStage,
            true,
            hash,
            lockedAt,
            FormatValidationStatus(latestVal),
            "Submission confirmed. Metadata locked. Routed to Adviser review queue.");
    }

    private async Task EnsureApprovalWorkflowAsync(Document doc, DateTimeOffset now, CancellationToken ct)
    {
        var open = await _db.ApprovalWorkflows
            .FirstOrDefaultAsync(w => w.DocumentId == doc.Id && !w.IsComplete, ct);

        if (open is not null)
        {
            open.CurrentStage = WorkflowStage.Adviser;
            open.UpdatedAt = now;
            return;
        }

        _db.ApprovalWorkflows.Add(new ApprovalWorkflow
        {
            DocumentId    = doc.Id,
            CurrentStage  = WorkflowStage.Adviser,
            IsComplete    = false,
            CreatedAt     = now,
            UpdatedAt     = now,
        });
    }

    private static string ComputeMetadataHash(Document doc, AIValidationResult validation)
    {
        var attachments = doc.Attachments
            .OrderBy(a => a.AttachmentType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.BlobPath, StringComparer.Ordinal)
            .Select(a => $"{a.AttachmentType}:{a.BlobPath}:{a.FileName}");

        var payload = string.Join('\n', new[]
        {
            doc.Id.ToString("N"),
            doc.DocumentNumber,
            doc.Title ?? "",
            doc.OrganizationId.ToString("N"),
            doc.DocumentTypeId.ToString("N"),
            doc.AcademicYearId.ToString("N"),
            doc.SubmittedByUserId.ToString("N"),
            doc.PrimaryFileBlobPath ?? "",
            doc.PrimaryFileName ?? "",
            string.Join('|', attachments),
            validation.DocumentClass,
            validation.Confidence.ToString("F4"),
            validation.ModelVersion ?? "",
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FormatValidationStatus(AIValidationResult? v)
    {
        if (v is null) return "Pending";
        if (ConfirmableClasses.Contains(v.DocumentClass))
        {
            return v.RequiresHumanReview || v.DocumentClass.Equals("Requires Human Review", StringComparison.OrdinalIgnoreCase)
                ? "Complete with minor flags"
                : "Complete";
        }
        return v.DocumentClass;
    }
}
