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
    double ConfidenceThreshold = 60);

public sealed record CalsvAttachmentDto(string Type, string FileUrl);

public sealed record CalsvValidationResponse(
    Guid JobId,
    string DocumentClass,
    decimal Confidence,
    bool RequiresHumanReview,
    string FieldResultsJson,
    string RawResponseJson,
    string ModelVersion);
