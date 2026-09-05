using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TsuOrg.Application.Common;

namespace TsuOrg.Infrastructure.Calsv;

public sealed class CalsvHttpClient : ICalsvClient
{
    private readonly HttpClient _http;

    public CalsvHttpClient(HttpClient http) => _http = http;

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CalsvValidationResponse> ValidateAsync(
        CalsvValidationRequest request, CancellationToken ct = default)
    {
        var payload = new
            {
                job_id             = request.JobId,
                document_id        = request.DocumentId,
                document_type      = request.DocumentType,
                primary_file_url   = request.PrimaryFileUrl,
                attachments        = request.Attachments.Select(a => new { type = a.Type, file_url = a.FileUrl }),
                options            = new
                {
                    confidence_threshold = request.ConfidenceThreshold,
                    return_tokens        = true,
                },
                catalog            = request.Catalog is null ? null : new
                {
                    submitted_name         = request.Catalog.SubmittedName,
                    submitted_acronym      = request.Catalog.SubmittedAcronym,
                    submitted_college_code = request.Catalog.SubmittedCollegeCode,
                    submitted_adviser_name = request.Catalog.SubmittedAdviserName,
                    organizations          = request.Catalog.Organizations.Select(o => new
                    {
                        name         = o.Name,
                        acronym      = o.Acronym,
                        college_code = o.CollegeCode,
                        college_name = o.CollegeName,
                        adviser_name = o.AdviserName,
                        aliases      = o.Aliases,
                    }),
                },
            };

        using var response = await _http.PostAsJsonAsync("/v1/validate", payload, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc  = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root       = doc.RootElement;

        // Parse attachment OCR results
        var attachmentOcrResults = new List<CalsvAttachmentOcrResult>();
        if (root.TryGetProperty("attachment_ocr_results", out var attOcrArray) 
            && attOcrArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var attOcr in attOcrArray.EnumerateArray())
            {
                attachmentOcrResults.Add(new CalsvAttachmentOcrResult(
                    AttachmentType: attOcr.GetProperty("attachment_type").GetString() ?? "",
                    FullText: attOcr.TryGetProperty("full_text", out var ft) ? ft.GetString() ?? "" : "",
                    AvgConfidence: attOcr.TryGetProperty("avg_confidence", out var ac) && ac.TryGetDecimal(out var acd) 
                        ? acd : 0m,
                    TokenCount: attOcr.TryGetProperty("token_count", out var tc) && tc.TryGetInt32(out var tci) 
                        ? tci : 0,
                    LowConfidenceCount: attOcr.TryGetProperty("low_confidence_count", out var lc) && lc.TryGetInt32(out var lci) 
                        ? lci : 0,
                    Engine: attOcr.TryGetProperty("engine", out var eng) ? eng.GetString() ?? "tesseract5" : "tesseract5",
                    Status: attOcr.TryGetProperty("status", out var st) ? st.GetString() ?? "completed" : "completed",
                    Error: attOcr.TryGetProperty("error", out var err) ? err.GetString() : null
                ));
            }
        }

        return new CalsvValidationResponse(
            JobId:              root.GetProperty("job_id").GetGuid(),
            DocumentClass:      root.GetProperty("document_class").GetString() ?? "Requires Human Review",
            Confidence:         ScoreNormalizer.ToUnit(root.GetProperty("confidence").GetDecimal()),
            RequiresHumanReview: root.GetProperty("requires_human_review").GetBoolean(),
            FieldResultsJson:   root.TryGetProperty("fields", out var fields) ? fields.GetRawText() : "[]",
            RawResponseJson:    root.GetRawText(),
            ModelVersion:       root.TryGetProperty("model_version", out var mv) ? mv.GetString() ?? "unknown" : "unknown",
            AttachmentOcrResults: attachmentOcrResults);
    }
}
