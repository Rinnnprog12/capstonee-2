namespace TsuOrg.Application.Common;

/// <summary>
/// Captures an electronic signature and produces an immutable hash + timestamp (WM-03).
/// </summary>
public interface ISignatureService
{
    /// <summary>
    /// Persist signature image bytes to blob storage and return path + content hash.
    /// Timestamp is always DateTimeOffset.UtcNow at capture time.
    /// </summary>
    Task<SignatureCaptureResult> CaptureAsync(
        Stream signatureStream,
        string fileName,
        string contentType,
        Guid actorUserId,
        Guid documentId,
        CancellationToken ct = default);
}

public sealed record SignatureCaptureResult(
    string BlobPath,
    string SignatureHash,
    DateTimeOffset CapturedAt);
