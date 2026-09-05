using TsuOrg.Frontend.Models;

namespace TsuOrg.Frontend.Services;

/// <summary>
/// Shared state across the 5-step Org Officer submission wizard.
/// Scoped lifetime (per circuit / WASM session).
/// </summary>
public sealed class SubmissionWizardState
{
    public Guid? DocumentId { get; private set; }
    public string? DocumentNumber { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public string? OrganizationName { get; private set; }
    public Guid? DocumentTypeId { get; private set; }
    public string? DocumentTypeCode { get; private set; }
    public string? DocumentTypeName { get; private set; }
    public Guid? AcademicYearId { get; private set; }
    public string? AcademicYearLabel { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public IReadOnlyList<RequirementDto> Requirements { get; private set; } = [];

    public bool HasDraft => DocumentId.HasValue && DocumentId != Guid.Empty;

    public void Begin(
        Guid documentId,
        string documentNumber,
        Guid organizationId,
        string organizationName,
        Guid documentTypeId,
        string documentTypeCode,
        string documentTypeName,
        Guid academicYearId,
        string academicYearLabel,
        string title,
        IReadOnlyList<RequirementDto> requirements)
    {
        DocumentId = documentId;
        DocumentNumber = documentNumber;
        OrganizationId = organizationId;
        OrganizationName = organizationName;
        DocumentTypeId = documentTypeId;
        DocumentTypeCode = documentTypeCode;
        DocumentTypeName = documentTypeName;
        AcademicYearId = academicYearId;
        AcademicYearLabel = academicYearLabel;
        Title = title;
        Requirements = requirements;
    }

    public void Clear()
    {
        DocumentId = null;
        DocumentNumber = null;
        OrganizationId = null;
        OrganizationName = null;
        DocumentTypeId = null;
        DocumentTypeCode = null;
        DocumentTypeName = null;
        AcademicYearId = null;
        AcademicYearLabel = null;
        Title = string.Empty;
        Requirements = [];
    }

    public IEnumerable<RequirementDto> AttachmentRequirements =>
        Requirements.Where(r => r.IsAttachment);

    /// <summary>First mandatory non-conditional attachment is treated as primary form if present; else first attachment.</summary>
    public RequirementDto? PrimaryAttachmentRequirement =>
        AttachmentRequirements.FirstOrDefault(r => r.IsMandatory && !r.IsConditional)
        ?? AttachmentRequirements.FirstOrDefault();
}
