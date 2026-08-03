using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Documents.Commands;
using TsuOrg.Application.Features.Documents.Queries;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Document Submission Management (DSM) endpoints.
/// DSM-01: PDF/image upload support
/// DSM-02: Automatic document ID generation
/// DSM-03: Enforce document type selection before upload
/// DSM-04: Structured submission by type
/// </summary>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class DocumentsController : ControllerBase
{
    private readonly CreateDocumentHandler    _create;
    private readonly UploadPrimaryFileHandler _uploadPrimary;
    private readonly AddAttachmentHandler     _addAttachment;
    private readonly SubmitDocumentHandler    _submit;
    private readonly GetDocumentHandler       _getDoc;
    private readonly ListDocumentsHandler     _listDocs;

    public DocumentsController(
        CreateDocumentHandler    create,
        UploadPrimaryFileHandler uploadPrimary,
        AddAttachmentHandler     addAttachment,
        SubmitDocumentHandler    submit,
        GetDocumentHandler       getDoc,
        ListDocumentsHandler     listDocs)
    {
        _create        = create;
        _uploadPrimary = uploadPrimary;
        _addAttachment = addAttachment;
        _submit        = submit;
        _getDoc        = getDoc;
        _listDocs      = listDocs;
    }

    // ── GET /api/v1/documents ─────────────────────────────────────────────
    /// <summary>List documents visible to the calling user (role-scoped).</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? organizationId,
        [FromQuery] DocumentStatus? status,
        [FromQuery] string? documentTypeCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _listDocs.HandleAsync(new ListDocumentsQuery(
            RequestingUserId:   GetUserId(),
            RequestingUserRole: GetRole(),
            OrganizationId:     organizationId,
            Status:             status,
            DocumentTypeCode:   documentTypeCode,
            Page:               page,
            PageSize:           Math.Min(pageSize, 100)),
            ct);

        return Ok(result);
    }

    // ── GET /api/v1/documents/{id} ────────────────────────────────────────
    /// <summary>Get full document detail including attachments and latest CALSV result.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        try
        {
            var doc = await _getDoc.HandleAsync(
                new GetDocumentQuery(id, GetUserId(), GetRole()), ct);
            return Ok(doc);
        }
        catch (NotFoundException ex)  { return NotFound(Problem(ex.Message)); }
        catch (ForbiddenException ex) { return Forbid(); }
    }

    // ── POST /api/v1/documents ────────────────────────────────────────────
    /// <summary>
    /// DSM-03: Create a document submission shell.
    /// Document type MUST be selected before any file upload.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "CanSubmitDocuments")]
    public async Task<IActionResult> Create(
        [FromBody] CreateDocumentRequest body,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _create.HandleAsync(new CreateDocumentCommand(
                body.OrganizationId,
                body.DocumentTypeId,
                body.AcademicYearId,
                GetUserId(),
                body.Title), ct);

            return CreatedAtAction(nameof(GetById), new { id = result.DocumentId }, result);
        }
        catch (NotFoundException ex)       { return NotFound(Problem(ex.Message)); }
        catch (BusinessRuleException ex)   { return UnprocessableEntity(Problem(ex.Message)); }
    }

    // ── POST /api/v1/documents/{id}/files ─────────────────────────────────
    /// <summary>
    /// DSM-01: Upload the primary document file (PDF or image).
    /// Accepted: application/pdf, image/png, image/jpeg, image/tiff.
    /// </summary>
    [HttpPost("{id:guid}/files")]
    [Authorize(Policy = "CanSubmitDocuments")]
    [RequestSizeLimit(50_000_000)]   // 50 MB
    public async Task<IActionResult> UploadPrimary(
        Guid id,
        IFormFile file,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(Problem("No file provided."));

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _uploadPrimary.HandleAsync(new UploadPrimaryFileCommand(
                id, GetUserId(), stream, file.FileName, file.ContentType), ct);

            return Ok(result);
        }
        catch (NotFoundException ex)       { return NotFound(Problem(ex.Message)); }
        catch (BusinessRuleException ex)   { return UnprocessableEntity(Problem(ex.Message)); }
        catch (ForbiddenException ex)      { return Forbid(); }
    }

    // ── POST /api/v1/documents/{id}/attachments ───────────────────────────
    /// <summary>Add a supporting attachment (identified by attachment type key).</summary>
    [HttpPost("{id:guid}/attachments")]
    [Authorize(Policy = "CanSubmitDocuments")]
    [RequestSizeLimit(30_000_000)]   // 30 MB
    public async Task<IActionResult> AddAttachment(
        Guid id,
        [FromQuery] string attachmentType,
        IFormFile file,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(Problem("No file provided."));
        if (string.IsNullOrWhiteSpace(attachmentType))
            return BadRequest(Problem("attachmentType query parameter is required."));

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _addAttachment.HandleAsync(new AddAttachmentCommand(
                id, GetUserId(), attachmentType, stream, file.FileName, file.ContentType), ct);

            return Ok(result);
        }
        catch (NotFoundException ex)       { return NotFound(Problem(ex.Message)); }
        catch (BusinessRuleException ex)   { return UnprocessableEntity(Problem(ex.Message)); }
        catch (ForbiddenException ex)      { return Forbid(); }
    }

    // ── POST /api/v1/documents/{id}/submit ───────────────────────────────
    /// <summary>
    /// Submit document for CALSV validation → triggers Layer 1→2→3 pipeline.
    /// Only works on Draft or Returned documents.
    /// </summary>
    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = "CanSubmitDocuments")]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct = default)
    {
        try
        {
            var result = await _submit.HandleAsync(
                new SubmitDocumentCommand(id, GetUserId()), ct);
            return Ok(result);
        }
        catch (NotFoundException ex)       { return NotFound(Problem(ex.Message)); }
        catch (BusinessRuleException ex)   { return UnprocessableEntity(Problem(ex.Message)); }
        catch (ForbiddenException ex)      { return Forbid(); }
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private string GetRole() =>
        User.FindFirstValue(ClaimTypes.Role) ?? "OrgOfficer";
}

// ─── Request DTOs ────────────────────────────────────────────────────────────

public sealed record CreateDocumentRequest(
    Guid OrganizationId,
    Guid DocumentTypeId,
    Guid AcademicYearId,
    string? Title = null);
