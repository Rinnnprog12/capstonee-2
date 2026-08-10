namespace TsuOrg.Application.Common;

/// <summary>
/// Generates unique, non-repeating Document IDs (DSM-02).
/// Format: {TypeCode}-{YYYYMM}-{SEQ:05}
/// Example: SF08-202607-00042, ACCOM-202607-00001
/// </summary>
public interface IDocumentNumberService
{
    Task<string> GenerateAsync(string documentTypeCode, CancellationToken ct = default);
}
