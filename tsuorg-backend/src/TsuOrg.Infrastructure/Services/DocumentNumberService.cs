using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Infrastructure.Persistence;

namespace TsuOrg.Infrastructure.Services;

/// <summary>
/// Generates unique Document IDs per paper requirement DSM-02.
/// Format: {TypeCode}-{YYYYMM}-{SEQ:05}
/// Example: SF08-202607-00001 | ACCOM-202607-00042
/// Sequential counter is per (type, year-month) — restarts at 1 each month per type.
/// </summary>
public sealed class DocumentNumberService : IDocumentNumberService
{
    private readonly TsuOrgDbContext _db;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public DocumentNumberService(TsuOrgDbContext db) => _db = db;

    public async Task<string> GenerateAsync(string documentTypeCode, CancellationToken ct = default)
    {
        // Truncate code to 5 chars max for readability
        var typePrefix = documentTypeCode.ToUpperInvariant()[..Math.Min(5, documentTypeCode.Length)];
        var periodKey  = DateTime.UtcNow.ToString("yyyyMM");
        var prefix     = $"{typePrefix}-{periodKey}-";

        // Serialise to prevent duplicate numbers under concurrent submissions
        await _lock.WaitAsync(ct);
        try
        {
            var lastNumber = await _db.Documents
                .Where(d => d.DocumentNumber.StartsWith(prefix))
                .OrderByDescending(d => d.DocumentNumber)
                .Select(d => d.DocumentNumber)
                .FirstOrDefaultAsync(ct);

            var nextSeq = 1;
            if (lastNumber is not null)
            {
                var seqPart = lastNumber[(prefix.Length)..];
                if (int.TryParse(seqPart, out var last))
                    nextSeq = last + 1;
            }

            return $"{prefix}{nextSeq:D5}";
        }
        finally
        {
            _lock.Release();
        }
    }
}
