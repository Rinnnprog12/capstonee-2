using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Settings;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Documents.Commands;

// ─── Command ────────────────────────────────────────────────────────────────

/// <summary>
/// Transitions Draft / Returned / Flagged → Submitted and enqueues CALSV.
/// Enforces primary file + mandatory attachments before accept.
/// </summary>
public sealed record SubmitDocumentCommand(
    Guid DocumentId,
    Guid SubmittedByUserId);

public sealed record SubmitDocumentResult(
    Guid DocumentId,
    string DocumentNumber,
    DocumentStatus Status,
    string Message);

// ─── Validator ───────────────────────────────────────────────────────────────

public sealed class SubmitDocumentValidator : AbstractValidator<SubmitDocumentCommand>
{
    public SubmitDocumentValidator()
    {
        RuleFor(x => x.DocumentId).NotEmpty();
        RuleFor(x => x.SubmittedByUserId).NotEmpty();
    }
}

// ─── Handler ─────────────────────────────────────────────────────────────────

public sealed class SubmitDocumentHandler
{
    private static readonly DocumentStatus[] AllowedStatuses =
    [
        DocumentStatus.Draft,
        DocumentStatus.Returned,
        DocumentStatus.Flagged,
    ];

    private readonly IApplicationDbContext _db;
    private readonly ICalsvJobQueue _queue;
    private readonly IAuditService _audit;
    private readonly ITrackingRecorder _tracking;
    private readonly GetSystemSettingsHandler _settings;

    public SubmitDocumentHandler(
        IApplicationDbContext db,
        ICalsvJobQueue queue,
        IAuditService audit,
        ITrackingRecorder tracking,
        GetSystemSettingsHandler settings)
    {
        _db = db;
        _queue = queue;
        _audit = audit;
        _tracking = tracking;
        _settings = settings;
    }

    public async Task<SubmitDocumentResult> HandleAsync(
        SubmitDocumentCommand cmd, CancellationToken ct = default)
    {
        new SubmitDocumentValidator().ValidateAndThrow(cmd);

        var doc = await _db.Documents
            .Include(d => d.DocumentType)
                .ThenInclude(t => t!.Requirements)
            .Include(d => d.Attachments)
            .Include(d => d.SubmittedByUser)
            .FirstOrDefaultAsync(d => d.Id == cmd.DocumentId, ct)
            ?? throw new NotFoundException("Document", cmd.DocumentId);

        if (doc.SubmittedByUserId != cmd.SubmittedByUserId)
            throw new ForbiddenException("Only the document owner can submit.");

        if (string.IsNullOrEmpty(doc.PrimaryFileBlobPath))
            throw new BusinessRuleException("Primary document file must be uploaded before submission.");

        if (!AllowedStatuses.Contains(doc.Status))
            throw new BusinessRuleException($"Document cannot be submitted from '{doc.Status}' status.");

        // Mandatory non-conditional attachments.
        // Primary file satisfies the first mandatory attachment requirement key
        // (DSM upload UX maps that card to /files instead of /attachments).
        var mandatoryAttachments = doc.DocumentType!.Requirements
            .Where(r => r.IsAttachment && r.IsMandatory && !r.IsConditional)
            .ToList();

        var primarySatisfiesKey = !string.IsNullOrEmpty(doc.PrimaryFileBlobPath)
            ? mandatoryAttachments.FirstOrDefault()?.RequirementKey
            : null;

        var uploadedKeys = doc.Attachments
            .Select(a => a.AttachmentType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(primarySatisfiesKey))
            uploadedKeys.Add(primarySatisfiesKey);

        var missing = mandatoryAttachments
            .Where(r => !uploadedKeys.Contains(r.RequirementKey))
            .Select(r => r.DisplayName)
            .ToList();

        if (missing.Count > 0)
            throw new BusinessRuleException(
                $"Missing mandatory attachment(s): {string.Join(", ", missing)}");

        // Snapshot a version when resubmitting after return/flag
        if (doc.Status is DocumentStatus.Returned or DocumentStatus.Flagged)
        {
            var nextVer = await _db.DocumentVersions
                .Where(v => v.DocumentId == doc.Id)
                .Select(v => (int?)v.VersionNumber)
                .MaxAsync(ct) ?? 0;

            _db.DocumentVersions.Add(new DocumentVersion
            {
                DocumentId    = doc.Id,
                VersionNumber = nextVer + 1,
                BlobPath      = doc.PrimaryFileBlobPath!,
                ChangeSummary = $"Resubmit from {doc.Status}",
            });
        }

        var settings = await _settings.HandleAsync(ct);
        var runCalsv = settings.AiValidationEnabled || settings.OcrAutoProcessing;

        doc.Status = DocumentStatus.Submitted;
        doc.CurrentStage = runCalsv ? WorkflowStage.Calsv : WorkflowStage.Officer;
        doc.UpdatedAt = DateTimeOffset.UtcNow;

        var officerName = doc.SubmittedByUser?.FullName ?? "organization officer";
        var position = await _db.OrganizationMemberships
            .AsNoTracking()
            .Where(m =>
                m.OrganizationId == doc.OrganizationId
                && m.UserAccountId == cmd.SubmittedByUserId
                && m.IsActive)
            .OrderByDescending(m => m.MembershipRole == MembershipRole.Officer)
            .Select(m => m.PositionTitle)
            .FirstOrDefaultAsync(ct);
        var who = string.IsNullOrWhiteSpace(position)
            ? officerName
            : $"{officerName} ({position})";

        await _tracking.RecordAsync(
            doc.Id,
            DocumentStatus.Submitted,
            doc.CurrentStage,
            runCalsv
                ? $"Document submitted. Uploaded by {who}."
                : $"Document submitted. Uploaded by {who}. CALSV skipped — AI validation and OCR auto-processing are disabled in System Settings.",
            cmd.SubmittedByUserId,
            ct);

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("Document.Submitted", nameof(Document), doc.Id.ToString(),
            new { doc.DocumentNumber, CalsvQueued = runCalsv }, cmd.SubmittedByUserId, ct);

        if (!runCalsv)
        {
            return new SubmitDocumentResult(
                doc.Id, doc.DocumentNumber, doc.Status,
                "Document submitted. CALSV is disabled in System Settings — proceed to confirmation.");
        }

        await _queue.EnqueueAsync(new CalsvJob(doc.Id, cmd.SubmittedByUserId), ct);

        return new SubmitDocumentResult(
            doc.Id, doc.DocumentNumber, doc.Status,
            "Document submitted. CALSV validation is queued.");
    }
}
