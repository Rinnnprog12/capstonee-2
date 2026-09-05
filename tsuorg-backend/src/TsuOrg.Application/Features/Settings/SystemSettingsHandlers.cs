using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;

namespace TsuOrg.Application.Features.Settings;

// ─── SOU System Settings (Figure 29) ─────────────────────────────────────────
// Global platform toggles: Document Validation · Notifications · Compliance.

public sealed record SystemSettingsDto(
    // Document Validation
    bool AiValidationEnabled,
    bool OcrAutoProcessing,
    // Notifications
    bool EmailNotifications,
    bool StaleDocumentAlerts,
    bool LowComplianceWarnings,
    // Compliance Thresholds
    int MinComplianceScore,
    /// <summary>All | FinancialOnly | None</summary>
    string MandatoryAdviserReviewTypes)
{
    public static SystemSettingsDto Defaults => new(
        AiValidationEnabled: true,
        OcrAutoProcessing: true,
        EmailNotifications: true,
        StaleDocumentAlerts: true,
        LowComplianceWarnings: true,
        MinComplianceScore: 75,
        MandatoryAdviserReviewTypes: "All");
}

public sealed class GetSystemSettingsHandler
{
    private readonly IApplicationDbContext _db;

    public GetSystemSettingsHandler(IApplicationDbContext db) => _db = db;

    public async Task<SystemSettingsDto> HandleAsync(CancellationToken ct = default)
    {
        var rows = await _db.SystemSettings
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var d = SystemSettingsDto.Defaults;
        return new SystemSettingsDto(
            GetBool(rows, "validation.ai_enabled", d.AiValidationEnabled),
            GetBool(rows, "validation.ocr_auto", d.OcrAutoProcessing),
            GetBool(rows, "notify.email_officers", d.EmailNotifications),
            GetBool(rows, "notify.stale_alerts", d.StaleDocumentAlerts),
            GetBool(rows, "notify.low_compliance", d.LowComplianceWarnings),
            GetInt(rows, "compliance.min_score", d.MinComplianceScore),
            rows.GetValueOrDefault("compliance.mandatory_adviser_types", d.MandatoryAdviserReviewTypes)
                ?? d.MandatoryAdviserReviewTypes);
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> rows, string key, bool fallback) =>
        rows.TryGetValue(key, out var raw) && bool.TryParse(raw, out var v) ? v : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> rows, string key, int fallback) =>
        rows.TryGetValue(key, out var raw) && int.TryParse(raw, out var v) ? Math.Clamp(v, 0, 100) : fallback;
}

public sealed class UpdateSystemSettingsHandler
{
    private static readonly string[] AllowedReviewTypes = ["All", "FinancialOnly", "None"];
    private static readonly string[] ObsoleteKeys =
    [
        "validation.sha256_hash",
        "validation.auto_qr",
    ];

    private readonly IApplicationDbContext _db;

    public UpdateSystemSettingsHandler(IApplicationDbContext db) => _db = db;

    public async Task<SystemSettingsDto> HandleAsync(SystemSettingsDto dto, CancellationToken ct = default)
    {
        var minScore = Math.Clamp(dto.MinComplianceScore, 0, 100);
        var reviewTypes = AllowedReviewTypes.Contains(dto.MandatoryAdviserReviewTypes)
            ? dto.MandatoryAdviserReviewTypes
            : "All";

        var values = new Dictionary<string, string>
        {
            ["validation.ai_enabled"] = dto.AiValidationEnabled.ToString(),
            ["validation.ocr_auto"] = dto.OcrAutoProcessing.ToString(),
            ["notify.email_officers"] = dto.EmailNotifications.ToString(),
            ["notify.stale_alerts"] = dto.StaleDocumentAlerts.ToString(),
            ["notify.low_compliance"] = dto.LowComplianceWarnings.ToString(),
            ["compliance.min_score"] = minScore.ToString(CultureInfo.InvariantCulture),
            ["compliance.mandatory_adviser_types"] = reviewTypes,
        };

        var existing = await _db.SystemSettings
            .Where(s => values.Keys.Contains(s.Key) || ObsoleteKeys.Contains(s.Key))
            .ToListAsync(ct);

        foreach (var obsolete in existing.Where(s => ObsoleteKeys.Contains(s.Key)).ToList())
            _db.SystemSettings.Remove(obsolete);

        var existingMap = existing
            .Where(s => values.ContainsKey(s.Key))
            .ToDictionary(s => s.Key);

        foreach (var (key, value) in values)
        {
            if (existingMap.TryGetValue(key, out var row))
            {
                if (row.Value == value) continue;
                row.Value = value;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
            }
        }

        await _db.SaveChangesAsync(ct);

        return dto with
        {
            MinComplianceScore = minScore,
            MandatoryAdviserReviewTypes = reviewTypes,
        };
    }
}
