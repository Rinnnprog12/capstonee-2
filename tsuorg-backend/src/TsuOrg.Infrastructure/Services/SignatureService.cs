using System.Security.Cryptography;
using TsuOrg.Application.Common;

namespace TsuOrg.Infrastructure.Services;

/// <summary>
/// WM-03: stores signature image in blob and computes SHA-256 hash for integrity.
/// Hash is content-based (not a blockchain ledger) — supports audit verification.
/// </summary>
public sealed class SignatureService : ISignatureService
{
    private readonly IBlobService _blob;

    public SignatureService(IBlobService blob) => _blob = blob;

    public async Task<SignatureCaptureResult> CaptureAsync(
        Stream signatureStream,
        string fileName,
        string contentType,
        Guid actorUserId,
        Guid documentId,
        CancellationToken ct = default)
    {
        // Buffer so we can hash and upload from the same content
        using var buffer = new MemoryStream();
        await signatureStream.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var hashBytes = await SHA256.HashDataAsync(buffer, ct);
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        buffer.Position = 0;
        var safeName = $"sig_{documentId:N}_{actorUserId:N}_{Path.GetFileName(fileName)}";
        var blobPath = await _blob.UploadAsync(buffer, safeName, contentType, ct);

        return new SignatureCaptureResult(
            BlobPath: blobPath,
            SignatureHash: hashHex,
            CapturedAt: DateTimeOffset.UtcNow);
    }
}
