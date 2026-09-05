using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Documents.Commands;

// ─── Commands ────────────────────────────────────────────────────────────────

/// <summary>
/// DSM-01: Upload primary document file (PDF or image).
/// Stores bytes in Azure Blob; saves path + metadata in DB.
/// </summary>
public sealed record UploadPrimaryFileCommand(
    Guid DocumentId,
    Guid ActorUserId,
    Stream FileStream,
    string FileName,
    string ContentType);

/// <summary>Add a supporting attachment to a document bundle.</summary>
public sealed record AddAttachmentCommand(
    Guid DocumentId,
    Guid ActorUserId,
    string AttachmentType,
    Stream FileStream,
    string FileName,
    string ContentType);

public sealed record UploadFileResult(string BlobPath, string FileName);

// ─── Validators ──────────────────────────────────────────────────────────────

public sealed class UploadPrimaryFileValidator : AbstractValidator<UploadPrimaryFileCommand>
{
    private static readonly string[] _allowed =
        ["application/pdf", "image/png", "image/jpeg", "image/jpg", "image/tiff", "image/tif"];

    public UploadPrimaryFileValidator()
    {
        RuleFor(x => x.DocumentId).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(256);
        RuleFor(x => x.ContentType)
            .Must(ct => IsAllowedContentType(ct))
            .WithMessage("Only PDF, PNG, JPEG, and TIFF files are accepted (DSM-01).");
        RuleFor(x => x.FileStream)
            .Must(s => s is { CanRead: true })
            .WithMessage("File stream is not readable.");
    }

    private static bool IsAllowedContentType(string? ct)
    {
        if (string.IsNullOrWhiteSpace(ct)) return false;
        return _allowed.Contains(ct.Trim().ToLowerInvariant());
    }
}

public sealed class AddAttachmentValidator : AbstractValidator<AddAttachmentCommand>
{
    private static readonly string[] _allowed =
        ["application/pdf", "image/png", "image/jpeg", "image/jpg", "image/tiff", "image/tif"];

    public AddAttachmentValidator()
    {
        RuleFor(x => x.DocumentId).NotEmpty();
        RuleFor(x => x.AttachmentType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(256);
        RuleFor(x => x.ContentType)
            .Must(ct => !string.IsNullOrWhiteSpace(ct) && _allowed.Contains(ct.Trim().ToLowerInvariant()))
            .WithMessage("Only PDF, PNG, JPEG, and TIFF files are accepted.");
    }
}

// ─── Handlers ────────────────────────────────────────────────────────────────

public sealed class UploadPrimaryFileHandler
{
    private readonly IApplicationDbContext _db;
    private readonly IBlobService _blob;
    private readonly IAuditService _audit;

    public UploadPrimaryFileHandler(IApplicationDbContext db, IBlobService blob, IAuditService audit)
    {
        _db = db;
        _blob = blob;
        _audit = audit;
    }

    public async Task<UploadFileResult> HandleAsync(UploadPrimaryFileCommand cmd, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == cmd.DocumentId, ct)
            ?? throw new NotFoundException("Document", cmd.DocumentId);

        if (doc.SubmittedByUserId != cmd.ActorUserId)
            throw new ForbiddenException("Only the document owner can upload files.");

        if (doc.IsMetadataLocked)
            throw new BusinessRuleException("Metadata is locked. This submission can no longer be edited.");

        if (doc.Status != DocumentStatus.Draft
            && doc.Status != DocumentStatus.Returned
            && doc.Status != DocumentStatus.Flagged)
            throw new BusinessRuleException("Cannot upload files to a document that is not in Draft, Returned, or Flagged status.");

        var oldBlob = doc.PrimaryFileBlobPath;
        var blobPath = await _blob.UploadAsync(cmd.FileStream, cmd.FileName, cmd.ContentType, ct);

        doc.PrimaryFileBlobPath = blobPath;
        doc.PrimaryFileName   = cmd.FileName;
        doc.PrimaryContentType = cmd.ContentType;
        doc.UpdatedAt          = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(oldBlob) && !string.Equals(oldBlob, blobPath, StringComparison.Ordinal))
        {
            try { await _blob.DeleteAsync(oldBlob, ct); } catch { /* best-effort */ }
        }

        await _audit.LogAsync("Document.PrimaryFileUploaded", nameof(Document),
            doc.Id.ToString(), new { blobPath, cmd.FileName }, cmd.ActorUserId, ct);

        return new UploadFileResult(blobPath, cmd.FileName);
    }
}

public sealed class AddAttachmentHandler
{
    private readonly IApplicationDbContext _db;
    private readonly IBlobService _blob;
    private readonly IAuditService _audit;

    public AddAttachmentHandler(IApplicationDbContext db, IBlobService blob, IAuditService audit)
    {
        _db = db;
        _blob = blob;
        _audit = audit;
    }

    public async Task<UploadFileResult> HandleAsync(AddAttachmentCommand cmd, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .Include(d => d.DocumentType)
                .ThenInclude(t => t!.Requirements)
            .FirstOrDefaultAsync(d => d.Id == cmd.DocumentId, ct)
            ?? throw new NotFoundException("Document", cmd.DocumentId);

        if (doc.SubmittedByUserId != cmd.ActorUserId)
            throw new ForbiddenException("Only the document owner can upload files.");

        if (doc.IsMetadataLocked)
            throw new BusinessRuleException("Metadata is locked. This submission can no longer be edited.");

        if (doc.Status != DocumentStatus.Draft
            && doc.Status != DocumentStatus.Returned
            && doc.Status != DocumentStatus.Flagged)
            throw new BusinessRuleException("Cannot add attachments to a document not in Draft, Returned, or Flagged status.");

        // Validate attachment type is expected for this document type
        var knownRequirement = doc.DocumentType?.Requirements
            .Any(r => r.IsAttachment && r.RequirementKey == cmd.AttachmentType);

        if (knownRequirement == false)
            throw new BusinessRuleException(
                $"Attachment type '{cmd.AttachmentType}' is not defined for document type '{doc.DocumentType?.Code}'.");

        var blobPath = await _blob.UploadAsync(cmd.FileStream, cmd.FileName, cmd.ContentType, ct);

        // Upsert: replacing the same checklist slot must not create duplicate rows.
        var existing = await _db.DocumentAttachments
            .Where(a => a.DocumentId == cmd.DocumentId && a.AttachmentType == cmd.AttachmentType)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        string? oldBlob = null;
        if (existing.Count > 0)
        {
            var keep = existing[0];
            oldBlob = keep.BlobPath;
            keep.BlobPath = blobPath;
            keep.FileName = cmd.FileName;
            keep.ContentType = cmd.ContentType;
            keep.UpdatedAt = DateTimeOffset.UtcNow;

            foreach (var dup in existing.Skip(1))
            {
                if (!string.IsNullOrWhiteSpace(dup.BlobPath))
                {
                    try { await _blob.DeleteAsync(dup.BlobPath, ct); } catch { /* best-effort */ }
                }
                _db.DocumentAttachments.Remove(dup);
            }
        }
        else
        {
            _db.DocumentAttachments.Add(new DocumentAttachment
            {
                DocumentId     = cmd.DocumentId,
                AttachmentType = cmd.AttachmentType,
                BlobPath       = blobPath,
                FileName       = cmd.FileName,
                ContentType    = cmd.ContentType,
            });
        }

        await _db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(oldBlob) && !string.Equals(oldBlob, blobPath, StringComparison.Ordinal))
        {
            try { await _blob.DeleteAsync(oldBlob, ct); } catch { /* best-effort */ }
        }

        await _audit.LogAsync("Document.AttachmentAdded", nameof(DocumentAttachment),
            cmd.DocumentId.ToString(), new { cmd.AttachmentType, blobPath, cmd.FileName }, cmd.ActorUserId, ct);

        return new UploadFileResult(blobPath, cmd.FileName);
    }
}
