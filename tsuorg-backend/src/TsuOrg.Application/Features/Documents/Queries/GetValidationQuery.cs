using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;

namespace TsuOrg.Application.Features.Documents.Queries;

public sealed record GetValidationQuery(Guid DocumentId);

public sealed record OcrLowConfidenceFlagDto(string Token, double Confidence, string Flag);

public sealed record ExtractedFieldDto(string Label, string Value);
public sealed record ComplianceCheckDto(string Label, bool Passed);

public sealed record ValidationDetailDto(
    Guid Id,
    string DocumentClass,
    decimal Confidence,
    bool RequiresHumanReview,
    string ModelVersion,
    DateTimeOffset ProcessedAt,
    JsonElement Fields,
    JsonElement? Attachments,
    JsonElement? Preprocessing,
    JsonElement? ConsistencyIssues,
    string? OcrFullText,
    decimal? OcrAvgConfidence,
    int? OcrTokenCount,
    IReadOnlyList<string> KeyFieldsIdentified,
    IReadOnlyList<OcrLowConfidenceFlagDto> LowConfidenceFlags,
    string RawResponseJson,
    int ScorePercent = 0,
    string Verdict = "",
    IReadOnlyList<ExtractedFieldDto>? ExtractedContent = null,
    IReadOnlyList<ComplianceCheckDto>? Checks = null,
    string? OcrCaption = null,
    string? OcrScannedCaption = null);

public sealed class GetValidationHandler
{
    private readonly IApplicationDbContext _db;

    public GetValidationHandler(IApplicationDbContext db) => _db = db;

    public async Task<ValidationDetailDto> HandleAsync(GetValidationQuery q, CancellationToken ct = default)
    {
        var exists = await _db.Documents.AnyAsync(d => d.Id == q.DocumentId, ct);
        if (!exists) throw new NotFoundException("Document", q.DocumentId);

        var doc = await _db.Documents
            .AsNoTracking()
            .Include(d => d.Organization)
            .Include(d => d.DocumentType)
            .Include(d => d.SubmittedByUser)
            .Include(d => d.Attachments)
            .FirstOrDefaultAsync(d => d.Id == q.DocumentId, ct)
            ?? throw new NotFoundException("Document", q.DocumentId);

        var v = await _db.AIValidationResults
            .AsNoTracking()
            .Where(x => x.DocumentId == q.DocumentId)
            .OrderByDescending(x => x.ProcessedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("AIValidationResult", q.DocumentId);

        var ocr = await _db.OCRResults
            .AsNoTracking()
            .Where(x => x.DocumentId == q.DocumentId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var flags = await _db.OCRErrorLogs
            .AsNoTracking()
            .Where(x => x.DocumentId == q.DocumentId)
            .OrderBy(x => x.Confidence)
            .Take(25)
            .Select(x => new OcrLowConfidenceFlagDto(x.Token, x.Confidence, x.Flag))
            .ToListAsync(ct);

        using var fieldsDoc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(v.FieldResultsJson) ? "[]" : v.FieldResultsJson);
        using var rawDoc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(v.RawResponseJson) ? "{}" : v.RawResponseJson);

        JsonElement? attachments = null;
        JsonElement? preprocessing = null;
        JsonElement? consistency = null;

        var root = rawDoc.RootElement;
        if (root.TryGetProperty("attachments", out var a)) attachments = a.Clone();
        if (root.TryGetProperty("preprocessing", out var p)) preprocessing = p.Clone();
        if (root.TryGetProperty("consistency_issues", out var c)) consistency = c.Clone();

        string? ocrText = ocr?.FullText;
        decimal? ocrAvg = ocr?.AvgConfidence;
        int? ocrTokens = null;
        if (root.TryGetProperty("ocr", out var ocrEl) && ocrEl.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(ocrText) && ocrEl.TryGetProperty("full_text", out var ft))
                ocrText = ft.GetString();
            if (ocrAvg is null or 0 && ocrEl.TryGetProperty("avg_confidence", out var ac)
                && ac.TryGetDecimal(out var acd))
                ocrAvg = acd;
            if (ocrEl.TryGetProperty("token_count", out var tc) && tc.TryGetInt32(out var tci))
                ocrTokens = tci;
        }
        else if (ocr is not null && !string.IsNullOrWhiteSpace(ocr.TokensJson))
        {
            try
            {
                using var tokDoc = JsonDocument.Parse(ocr.TokensJson);
                if (tokDoc.RootElement.ValueKind == JsonValueKind.Array)
                    ocrTokens = tokDoc.RootElement.GetArrayLength();
                else if (tokDoc.RootElement.TryGetProperty("ocr_token_count", out var metaTc)
                         && metaTc.TryGetInt32(out var metaCount))
                    ocrTokens = metaCount;
            }
            catch { /* ignore */ }
        }

        var keyFields = ExtractKeyFields(fieldsDoc.RootElement);
        var score = ReviewEvaluationMapper.ScorePercent(v.Confidence);
        var extracted = ReviewEvaluationMapper.BuildExtractedContent(
            doc, fieldsDoc.RootElement, attachments, score);
        var checks = ReviewEvaluationMapper.BuildChecks(
            doc,
            v.DocumentClass,
            fieldsDoc.RootElement,
            attachments,
            consistency,
            flags.Count);

        return new ValidationDetailDto(
            v.Id,
            v.DocumentClass,
            v.Confidence,
            v.RequiresHumanReview,
            v.ModelVersion,
            v.ProcessedAt,
            fieldsDoc.RootElement.Clone(),
            attachments,
            preprocessing,
            consistency,
            ocrText,
            ocrAvg,
            ocrTokens,
            keyFields,
            flags,
            v.RawResponseJson,
            score,
            ReviewEvaluationMapper.Verdict(v.DocumentClass, v.RequiresHumanReview, score),
            extracted.Select(e => new ExtractedFieldDto(e.Label, e.Value)).ToList(),
            checks.Select(c => new ComplianceCheckDto(c.Label, c.Passed)).ToList(),
            ReviewEvaluationMapper.BuildCaption(extracted),
            ReviewEvaluationMapper.BuildScannedCaption(ocrText));
    }

    private static IReadOnlyList<string> ExtractKeyFields(JsonElement fields)
    {
        var names = new List<string>();
        if (fields.ValueKind != JsonValueKind.Array) return names;

        foreach (var f in fields.EnumerateArray())
        {
            var label = f.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
            var hasMatch = f.TryGetProperty("matched_text", out var mt)
                           && !string.IsNullOrWhiteSpace(mt.GetString());

            if (label is not ("PRESENT" or "SIGNED" or "ILLEGIBLE") && !hasMatch)
                continue;

            var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var pretty = System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2")
                .Replace('_', ' ');
            if (!names.Contains(pretty, StringComparer.OrdinalIgnoreCase))
                names.Add(pretty);
        }

        return names;
    }
}
