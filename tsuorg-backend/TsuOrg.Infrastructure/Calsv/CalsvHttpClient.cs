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
            }
        };

        using var response = await _http.PostAsJsonAsync("/v1/validate", payload, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc  = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root       = doc.RootElement;

        return new CalsvValidationResponse(
            JobId:              root.GetProperty("job_id").GetGuid(),
            DocumentClass:      root.GetProperty("document_class").GetString() ?? "Requires Human Review",
            Confidence:         root.GetProperty("confidence").GetDecimal(),
            RequiresHumanReview: root.GetProperty("requires_human_review").GetBoolean(),
            FieldResultsJson:   root.TryGetProperty("fields", out var fields) ? fields.GetRawText() : "[]",
            RawResponseJson:    root.GetRawText(),
            ModelVersion:       root.TryGetProperty("model_version", out var mv) ? mv.GetString() ?? "unknown" : "unknown");
    }
}
