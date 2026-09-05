namespace TsuOrg.Application.Common;

public interface ICalsvClient
{
    Task<bool> HealthAsync(CancellationToken ct = default);
    Task<CalsvValidationResponse> ValidateAsync(CalsvValidationRequest request, CancellationToken ct = default);
}

public sealed record CalsvValidationRequest(
    Guid JobId,
    Guid DocumentId,
    string DocumentType,
    string PrimaryFileUrl,
    IReadOnlyList<CalsvAttachmentDto> Attachments,
    double ConfidenceThreshold = 60,
    CalsvOrgCatalogDto? Catalog = null);

public sealed record CalsvOrgCatalogDto(
    string? SubmittedName,
    string? SubmittedAcronym,
    string? SubmittedCollegeCode,
    string? SubmittedAdviserName,
    IReadOnlyList<CalsvCatalogOrgDto> Organizations);

public sealed record CalsvCatalogOrgDto(
    string Name,
    string Acronym,
    string? CollegeCode,
    string? CollegeName,
    string? AdviserName,
    IReadOnlyList<string> Aliases);

public sealed record CalsvAttachmentDto(string Type, string FileUrl);

public sealed record CalsvValidationResponse(
    Guid JobId,
    string DocumentClass,
    decimal Confidence,
    bool RequiresHumanReview,
    string FieldResultsJson,
    string RawResponseJson,
    string ModelVersion,
    IReadOnlyList<CalsvAttachmentOcrResult> AttachmentOcrResults);

public sealed record CalsvAttachmentOcrResult(
    string AttachmentType,
    string FullText,
    decimal AvgConfidence,
    int TokenCount,
    int LowConfidenceCount,
    string Engine,
    string Status,
    string? Error);
