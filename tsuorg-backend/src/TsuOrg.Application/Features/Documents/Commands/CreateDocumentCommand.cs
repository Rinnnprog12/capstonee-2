using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Documents.Commands;

// ─── Command ────────────────────────────────────────────────────────────────

/// <summary>
/// DSM-03: Enforce structured submission by requiring document type selection before upload.
/// Creates a Document record in Draft status; returns the new ID + generated DocumentNumber.
/// </summary>
public sealed record CreateDocumentCommand(
    Guid OrganizationId,
    Guid DocumentTypeId,
    Guid AcademicYearId,
    Guid SubmittedByUserId,
    string? Title = null);

public sealed record CreateDocumentResult(
    Guid DocumentId,
    string DocumentNumber);

// ─── Validator ───────────────────────────────────────────────────────────────

public sealed class CreateDocumentCommandValidator : AbstractValidator<CreateDocumentCommand>
{
    public CreateDocumentCommandValidator()
    {
        RuleFor(x => x.OrganizationId).NotEmpty();
        RuleFor(x => x.DocumentTypeId).NotEmpty();
        RuleFor(x => x.AcademicYearId).NotEmpty();
        RuleFor(x => x.SubmittedByUserId).NotEmpty();
    }
}

// ─── Handler ─────────────────────────────────────────────────────────────────

public sealed class CreateDocumentHandler
{
    private readonly IApplicationDbContext _db;
    private readonly IDocumentNumberService _numberSvc;
    private readonly IAuditService _audit;

    public CreateDocumentHandler(
        IApplicationDbContext db,
        IDocumentNumberService numberSvc,
        IAuditService audit)
    {
        _db = db;
        _numberSvc = numberSvc;
        _audit = audit;
    }

    public async Task<CreateDocumentResult> HandleAsync(
        CreateDocumentCommand cmd, CancellationToken ct = default)
    {
        // Resolve the type code for document number generation (DSM-02)
        var docType = await _db.DocumentTypes
            .FirstOrDefaultAsync(t => t.Id == cmd.DocumentTypeId, ct)
            ?? throw new NotFoundException("DocumentType", cmd.DocumentTypeId);

        var org = await _db.Organizations
            .FirstOrDefaultAsync(o => o.Id == cmd.OrganizationId, ct)
            ?? throw new NotFoundException("Organization", cmd.OrganizationId);

        _ = await _db.AcademicYears
            .FirstOrDefaultAsync(a => a.Id == cmd.AcademicYearId, ct)
            ?? throw new NotFoundException("AcademicYear", cmd.AcademicYearId);

        // Official must belong to the org (officer membership, designated OfficerId, or adviser excluded)
        var isMember = await _db.OrganizationMemberships.AnyAsync(m =>
            m.OrganizationId == cmd.OrganizationId
            && m.UserAccountId == cmd.SubmittedByUserId
            && m.IsActive
            && (m.MembershipRole == MembershipRole.Officer || m.MembershipRole == MembershipRole.Member), ct);

        var isDesignatedOfficer = org.OfficerId == cmd.SubmittedByUserId;

        if (!isMember && !isDesignatedOfficer)
            throw new ForbiddenException("You are not a member of this organization.");

        var docNumber = await _numberSvc.GenerateAsync(docType.Code, ct);

        var document = new Document
        {
            DocumentNumber    = docNumber,
            Title             = cmd.Title?.Trim() ?? string.Empty,
            OrganizationId    = cmd.OrganizationId,
            DocumentTypeId    = cmd.DocumentTypeId,
            AcademicYearId    = cmd.AcademicYearId,
            SubmittedByUserId = cmd.SubmittedByUserId,
            Status            = DocumentStatus.Draft,
            CurrentStage      = WorkflowStage.Officer,
        };

        _db.Documents.Add(document);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            action: "Document.Created",
            entityName: nameof(Document),
            entityId: document.Id.ToString(),
            details: new { document.DocumentNumber, docType.Code },
            actorUserId: cmd.SubmittedByUserId,
            ct: ct);

        return new CreateDocumentResult(document.Id, document.DocumentNumber);
    }
}
