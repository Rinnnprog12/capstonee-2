using System.Text.Json;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Entities;

namespace TsuOrg.Application.Features.Documents.Queries;

/// <summary>
/// Figure 16 / 23 — maps persisted CALSV + OCR rows into reviewer-facing extract
/// and checklist. No LLM: values come from Tesseract/LayoutLM field labels stored in DB.
/// </summary>
public static class ReviewEvaluationMapper
{
    public sealed record ExtractedField(string Label, string Value);
    public sealed record Check(string Label, bool Passed);

    public static int ScorePercent(decimal confidence) => ScoreNormalizer.ToPercent(confidence);

    public static string Verdict(string documentClass, bool requiresHumanReview, int scorePercent)
    {
        if (string.Equals(documentClass, "Incomplete Submission", StringComparison.OrdinalIgnoreCase)
            || string.Equals(documentClass, "Attachment Missing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(documentClass, "Structurally Invalid", StringComparison.OrdinalIgnoreCase)
            || scorePercent < 60)
            return "Needs revision before review";

        if (requiresHumanReview)
            return "Flagged for human review";

        return "Approved for review";
    }

    public static IReadOnlyList<ExtractedField> BuildExtractedContent(
        Document doc,
        JsonElement fields,
        JsonElement? attachments,
        int scorePercent)
    {
        var title = string.IsNullOrWhiteSpace(doc.Title)
            ? (doc.DocumentType?.Name ?? doc.DocumentNumber)
            : doc.Title;
        var submitted = (doc.MetadataLockedAt ?? doc.CreatedAt).ToLocalTime().ToString("MMM d, yyyy");
        var ocrOrg = FieldMatchedText(fields, "OrganizationName");
        var org = !string.IsNullOrWhiteSpace(ocrOrg) ? ocrOrg : (doc.Organization?.Name ?? "—");
        var officer = doc.SubmittedByUser?.FullName ?? "—";
        var sig = SignatureValue(fields, "AdviserSignature", "Adviser");
        var budget = BudgetValue(fields, attachments, doc);

        return
        [
            new("DOCUMENT", title),
            new("Organization", org),
            new("Date submitted", submitted),
            new("Officer", officer),
            new("Adviser signature", sig),
            new("Budget/financial data", budget),
            new("Compliance assessment", $"{scorePercent}%"),
        ];
    }

    /// <summary>
    /// Figure 23 caption — template from OCR/CALSV fields (not an LLM).
    /// </summary>
    public static string BuildCaption(IReadOnlyList<ExtractedField> rows)
    {
        string V(string label) =>
            rows.FirstOrDefault(r => r.Label.Equals(label, StringComparison.OrdinalIgnoreCase))?.Value ?? "—";

        return
            $"{V("DOCUMENT")} submitted by {V("Officer")} ({V("Organization")}) on {V("Date submitted")}. " +
            $"Adviser signature: {V("Adviser signature")}. " +
            $"Budget/financial data: {V("Budget/financial data")}. " +
            $"Compliance assessment: {V("Compliance assessment")}.";
    }

    public static string? BuildScannedCaption(string? ocrFullText, int maxChars = 420)
    {
        if (string.IsNullOrWhiteSpace(ocrFullText)) return null;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(ocrFullText.Trim(), @"\s+", " ");
        if (collapsed.Length <= maxChars) return collapsed;
        return collapsed[..maxChars].TrimEnd() + "…";
    }

    public static IReadOnlyList<Check> BuildChecks(
        Document doc,
        string documentClass,
        JsonElement fields,
        JsonElement? attachments,
        JsonElement? consistency,
        int lowConfidenceFlagCount)
    {
        var checks = new List<Check>();

        var missingFields = LabelsMatching(fields, "MISSING", "ABSENT");
        checks.Add(missingFields.Count == 0
            ? new("All required fields present", true)
            : new($"Missing fields: {string.Join(", ", missingFields.Take(4))}", false));

        var isPdf = string.Equals(doc.PrimaryContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                    || (doc.PrimaryFileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ?? false);
        checks.Add(isPdf
            ? new("File format valid (PDF, structured)", true)
            : new($"File format: {doc.PrimaryContentType ?? "unknown"}", false));

        var classOk = documentClass.Equals("Valid Submission", StringComparison.OrdinalIgnoreCase)
                      || documentClass.Equals("Requires Human Review", StringComparison.OrdinalIgnoreCase);
        checks.Add(classOk
            ? new("Checklist requirements satisfied", true)
            : new($"AI classification: {documentClass}", false));

        foreach (var unsigned in LabelsMatching(fields, "UNSIGNED", "ILLEGIBLE"))
            checks.Add(new($"{unsigned} not detected — minor flag", false));

        foreach (var missingAtt in AttachmentGaps(attachments))
            checks.Add(new($"Missing attachment: {missingAtt}", false));

        foreach (var issue in ConsistencyFlags(consistency))
            checks.Add(new(issue, false));

        if (lowConfidenceFlagCount > 0 && checks.All(c => c.Passed || !c.Label.Contains("minor flag", StringComparison.OrdinalIgnoreCase)))
            checks.Add(new("Low-confidence OCR tokens flagged — minor flag", false));

        return checks;
    }

    private static string FieldMatchedText(JsonElement fields, string name)
    {
        if (fields.ValueKind != JsonValueKind.Array) return "";
        foreach (var f in fields.EnumerateArray())
        {
            var n = f.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
            if (!n.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return f.TryGetProperty("matched_text", out var mt) ? mt.GetString() ?? "" : "";
        }
        return "";
    }

    private static string SignatureValue(JsonElement fields, params string[] needles)
    {
        var hit = FindField(fields, needles);
        if (hit is null) return "Not detected";
        return hit.Value.Label switch
        {
            "PRESENT" or "SIGNED" => "Present ✓",
            "UNSIGNED" or "MISSING" => "Not detected",
            "ILLEGIBLE" => "Illegible",
            _ => Prettify(hit.Value.Label),
        };
    }

    private static string BudgetValue(JsonElement fields, JsonElement? attachments, Document doc)
    {
        var field = FindField(fields, "Budget", "Financial");
        if (field is { Label: "PRESENT" or "SIGNED" }) return "Included";
        if (field is { Label: "MISSING" or "ABSENT" }) return "Not detected";

        if (HasAttachment(attachments, "FinancialLiquidation", "Budget"))
            return "Included";

        if (doc.Attachments.Any(a =>
                a.AttachmentType.Contains("Financial", StringComparison.OrdinalIgnoreCase)
                || a.AttachmentType.Contains("Budget", StringComparison.OrdinalIgnoreCase)
                || a.AttachmentType.Contains("Liquidation", StringComparison.OrdinalIgnoreCase)))
            return "Included";

        return "Not detected";
    }

    private static (string Name, string Label)? FindField(JsonElement fields, params string[] needles)
    {
        if (fields.ValueKind != JsonValueKind.Array) return null;
        foreach (var f in fields.EnumerateArray())
        {
            var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!needles.Any(nd => name.Contains(nd, StringComparison.OrdinalIgnoreCase)))
                continue;
            var label = f.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
            return (name, label);
        }
        return null;
    }

    private static List<string> LabelsMatching(JsonElement fields, params string[] labels)
    {
        var names = new List<string>();
        if (fields.ValueKind != JsonValueKind.Array) return names;
        foreach (var f in fields.EnumerateArray())
        {
            var label = f.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
            if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase)) continue;
            var name = f.TryGetProperty("name", out var n) ? Prettify(n.GetString()) : "field";
            names.Add(name);
        }
        return names;
    }

    private static IEnumerable<string> AttachmentGaps(JsonElement? attachments)
    {
        if (attachments is not { ValueKind: JsonValueKind.Array } atts)
            yield break;

        foreach (var a in atts.EnumerateArray())
        {
            var label = a.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
            var present = a.TryGetProperty("present", out var p) && p.ValueKind == JsonValueKind.True;
            var missing = label is "ATTACHMENT_MISSING" or "MISSING"
                          || (a.TryGetProperty("present", out _) && !present);

            if (label is "CONDITIONALLY_REQUIRED") continue;
            if (!missing) continue;

            var name = a.TryGetProperty("type", out var t) ? Prettify(t.GetString())
                : a.TryGetProperty("name", out var n) ? Prettify(n.GetString())
                : a.TryGetProperty("attachment_type", out var at) ? Prettify(at.GetString())
                : "attachment";
            yield return name;
        }
    }

    private static bool HasAttachment(JsonElement? attachments, params string[] needles)
    {
        if (attachments is not { ValueKind: JsonValueKind.Array } atts)
            return false;
        foreach (var a in atts.EnumerateArray())
        {
            var type = a.TryGetProperty("type", out var t) ? t.GetString() ?? ""
                : a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var label = a.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
            if (!needles.Any(nd => type.Contains(nd, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (label is "ATTACHMENT_PRESENT" or "PRESENT") return true;
        }
        return false;
    }

    private static IEnumerable<string> ConsistencyFlags(JsonElement? consistency)
    {
        if (consistency is not { ValueKind: JsonValueKind.Array } issues)
            yield break;

        foreach (var issue in issues.EnumerateArray().Take(4))
        {
            var desc = issue.TryGetProperty("description", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(desc))
                desc = issue.TryGetProperty("code", out var c) ? Prettify(c.GetString()) : "Format issue";
            yield return desc!;
        }
    }

    private static string Prettify(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? "field"
            : System.Text.RegularExpressions.Regex
                .Replace(name, "([a-z])([A-Z])", "$1 $2")
                .Replace('_', ' ');
}
