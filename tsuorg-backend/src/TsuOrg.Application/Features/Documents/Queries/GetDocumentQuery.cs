using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Documents.Queries;

// ─── Query ────────────────────────────────────────────────────────────────

public sealed record GetDocumentQuery(Guid DocumentId, Guid RequestingUserId, string RequestingUserRole);

// ─── Result DTO ──────────────────────────────────────────────────────────

public sealed record DocumentDetailDto(
    Guid Id,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string DocumentTypeCode,
    string DocumentTypeName,
    string AcademicYear,
    string SubmittedBy,
    DocumentStatus Status,
    WorkflowStage CurrentStage,
    string? PrimaryFileName,
    string? PrimaryContentType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool IsMetadataLocked,
    string? MetadataLockHash,
    DateTimeOffset? MetadataLockedAt,
    IReadOnlyList<AttachmentDto> Attachments,
    ValidationResultDto? LatestValidation);

public sealed record AttachmentDto(
    Guid Id,
    string AttachmentType,
    string FileName,
    string ContentType,
    ProcessingStatus ProcessingStatus,
    string? ProcessingError,
    AttachmentOcrSummaryDto? OcrSummary);

public sealed record AttachmentOcrSummaryDto(
    string FullText,
    decimal AvgConfidence,
    int TokenCount,
    int LowConfidenceCount);

public sealed record ValidationResultDto(
    string DocumentClass,
    decimal Confidence,
    bool RequiresHumanReview,
    string ModelVersion,
    DateTimeOffset ProcessedAt,
    string FieldResultsJson);

// ─── Handler ─────────────────────────────────────────────────────────────

public sealed class GetDocumentHandler
{
    private readonly IApplicationDbContext _db;

    public GetDocumentHandler(IApplicationDbContext db) => _db = db;

    public async Task<DocumentDetailDto> HandleAsync(GetDocumentQuery q, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .Include(d => d.AcademicYear)
            .Include(d => d.SubmittedByUser)
            .Include(d => d.Attachments)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == q.DocumentId, ct)
            ?? throw new NotFoundException("Document", q.DocumentId);

        // Load attachment OCR results separately
        var attachmentOcrResults = await _db.AttachmentOCRResults
            .AsNoTracking()
            .Where(ocr => doc.Attachments.Select(a => a.Id).Contains(ocr.AttachmentId))
            .ToDictionaryAsync(ocr => ocr.AttachmentId, ct);

        // Scope: officers → own submissions; adviser/dean → assigned org / college.
        if (q.RequestingUserRole == "OrgOfficer" && doc.SubmittedByUserId != q.RequestingUserId)
            throw new ForbiddenException("Access denied to this document.");

        if (q.RequestingUserRole is "Adviser" or "Dean")
        {
            var scoped = await DocumentVisibility.ApplyAdviserDeanScopeAsync(
                _db,
                _db.Documents.AsNoTracking().Where(d => d.Id == q.DocumentId),
                q.RequestingUserId,
                q.RequestingUserRole,
                ct);
            if (!await scoped.AnyAsync(ct))
                throw new ForbiddenException("Access denied to this document.");
        }

        var latestValidation = await _db.AIValidationResults
            .Where(v => v.DocumentId == q.DocumentId)
            .OrderByDescending(v => v.ProcessedAt)
            .Select(v => new ValidationResultDto(
                v.DocumentClass, v.Confidence, v.RequiresHumanReview,
                v.ModelVersion, v.ProcessedAt, v.FieldResultsJson))
            .FirstOrDefaultAsync(ct);

        return new DocumentDetailDto(
            doc.Id,
            doc.DocumentNumber,
            doc.Title,
            doc.Organization?.Name ?? "",
            doc.DocumentType?.Code ?? "",
            doc.DocumentType?.Name ?? "",
            doc.AcademicYear?.Label ?? "",
            doc.SubmittedByUser?.FullName ?? "",
            doc.Status,
            doc.CurrentStage,
            doc.PrimaryFileName,
            doc.PrimaryContentType,
            doc.CreatedAt,
            doc.UpdatedAt,
            doc.IsMetadataLocked,
            doc.MetadataLockHash,
            doc.MetadataLockedAt,
            doc.Attachments.Select(a => new AttachmentDto(
                a.Id, 
                a.AttachmentType, 
                a.FileName, 
                a.ContentType,
                a.ProcessingStatus,
                a.ProcessingError,
                attachmentOcrResults.TryGetValue(a.Id, out var ocr)
                    ? new AttachmentOcrSummaryDto(ocr.FullText, ocr.AvgConfidence, ocr.TokenCount, ocr.LowConfidenceCount)
                    : null
            )).ToList(),
            latestValidation);
    }
}
