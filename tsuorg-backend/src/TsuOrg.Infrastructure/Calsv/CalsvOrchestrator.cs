using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Documents.Services;
using TsuOrg.Application.Features.Settings;
using TsuOrg.Domain.Entities;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Infrastructure.Calsv;

/// <summary>
/// Full CALSV orchestration: signed URLs → ML validate → persist results →
/// status transition → notifications → start approval workflow when valid.
/// </summary>
public sealed class CalsvOrchestrator : ICalsvOrchestrator
{
    private readonly IApplicationDbContext _db;
    private readonly ICalsvClient _calsv;
    private readonly IBlobService _blob;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;
    private readonly ITrackingRecorder _tracking;
    private readonly GetSystemSettingsHandler _settings;
    private readonly ILogger<CalsvOrchestrator> _logger;

    public CalsvOrchestrator(
        IApplicationDbContext db,
        ICalsvClient calsv,
        IBlobService blob,
        IAuditService audit,
        INotificationService notifications,
        ITrackingRecorder tracking,
        GetSystemSettingsHandler settings,
        ILogger<CalsvOrchestrator> logger)
    {
        _db = db;
        _calsv = calsv;
        _blob = blob;
        _audit = audit;
        _notifications = notifications;
        _tracking = tracking;
        _settings = settings;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid documentId, Guid? triggeredByUserId = null, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .Include(d => d.DocumentType)
            .Include(d => d.Attachments)
            .Include(d => d.Organization)
                .ThenInclude(o => o!.CollegeRef)
            .Include(d => d.Organization)
                .ThenInclude(o => o!.PrimaryAdviser)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);

        if (doc is null)
        {
            _logger.LogWarning("CALSV job skipped — document {DocumentId} not found", documentId);
            return;
        }

        if (string.IsNullOrEmpty(doc.PrimaryFileBlobPath))
        {
            await FailAsync(doc, "Primary file missing; cannot run CALSV.", ct);
            return;
        }

        try
        {
            doc.Status       = DocumentStatus.Validating;
            doc.CurrentStage = WorkflowStage.Calsv;
            doc.UpdatedAt    = DateTimeOffset.UtcNow;
            
            // Mark all attachments as queued
            foreach (var att in doc.Attachments)
            {
                att.ProcessingStatus = ProcessingStatus.Queued;
                att.ProcessingError = null;
            }
            
            await _db.SaveChangesAsync(ct);

            var settings = await _settings.HandleAsync(ct);
            if (!settings.AiValidationEnabled && !settings.OcrAutoProcessing)
            {
                _logger.LogInformation(
                    "CALSV skipped for {DocumentId} — disabled in System Settings", documentId);
                doc.Status = DocumentStatus.Submitted;
                doc.CurrentStage = WorkflowStage.Officer;
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                await _tracking.RecordAsync(
                    doc.Id,
                    DocumentStatus.Submitted,
                    WorkflowStage.Officer,
                    "CALSV skipped — AI validation and OCR auto-processing are disabled in System Settings.",
                    triggeredByUserId,
                    ct);
                await _db.SaveChangesAsync(ct);
                return;
            }

            var expiry     = TimeSpan.FromMinutes(30);
            var primaryUrl = await _blob.GetReadUrlAsync(doc.PrimaryFileBlobPath!, expiry, ct);

            var attachmentDtos = new List<CalsvAttachmentDto>();
            foreach (var att in doc.Attachments)
            {
                var attUrl = await _blob.GetReadUrlAsync(att.BlobPath, expiry, ct);
                attachmentDtos.Add(new CalsvAttachmentDto(att.AttachmentType, attUrl));
            }

            var request = new CalsvValidationRequest(
                JobId: Guid.NewGuid(),
                DocumentId: doc.Id,
                DocumentType: doc.DocumentType!.Code,
                PrimaryFileUrl: primaryUrl,
                Attachments: attachmentDtos,
                ConfidenceThreshold: settings.MinComplianceScore,
                Catalog: await BuildOrgCatalogAsync(doc, ct));

            var result = await _calsv.ValidateAsync(request, ct);

            // Persist Layer 1 / Layer 2 / Layer 3 artifacts (ImagePreprocessing, OCR, AI).
            if (settings.OcrAutoProcessing)
                PersistLayerLogs(doc.Id, result.RawResponseJson);

            // Persist attachment OCR results
            PersistAttachmentOcrResults(doc.Attachments.ToList(), result.AttachmentOcrResults);

            var scorePct = ScoreNormalizer.ToPercent(result.Confidence);
            var requiresReview = result.RequiresHumanReview
                || scorePct < settings.MinComplianceScore;

            _db.AIValidationResults.Add(new AIValidationResult
            {
                DocumentId          = doc.Id,
                DocumentClass       = result.DocumentClass,
                Confidence          = ScoreNormalizer.ToUnit(result.Confidence),
                RequiresHumanReview = requiresReview,
                FieldResultsJson    = result.FieldResultsJson,
                RawResponseJson     = result.RawResponseJson,
                ModelVersion        = result.ModelVersion,
                ProcessedAt         = DateTimeOffset.UtcNow,
            });

            // Figure 10 gate: CALSV results are shown to the officer first.
            // Adviser queue starts only after ConfirmSubmission locks metadata.
            var nextStatus = result.DocumentClass switch
            {
                "Valid Submission"      => DocumentStatus.Submitted,
                "Requires Human Review" => DocumentStatus.Submitted,
                "Incomplete Submission" => DocumentStatus.Flagged,
                "Attachment Missing"    => DocumentStatus.Flagged,
                "Structurally Invalid"  => DocumentStatus.Flagged,
                _                       => DocumentStatus.Flagged,
            };

            var nextStage = WorkflowStage.Officer;

            doc.Status       = nextStatus;
            doc.CurrentStage = nextStage;
            doc.UpdatedAt    = DateTimeOffset.UtcNow;

            // Figure 18 audit trail — separate ML layers (OCR then AI validation).
            var (ocrAvg, tokenCount, lowCount) = ExtractOcrStats(result.RawResponseJson);
            var ocrMsg = tokenCount > 0
                ? $"OCR & scanning. Tesseract processed text and layout detection ({tokenCount} tokens, avg confidence {FormatOcrPercent(ocrAvg)}%{(lowCount > 0 ? $", {lowCount} low-confidence" : "")})."
                : "OCR & scanning. Tesseract processed text and layout detection.";
            await _tracking.RecordAsync(
                doc.Id,
                DocumentStatus.Validating,
                WorkflowStage.Calsv,
                settings.OcrAutoProcessing
                    ? ocrMsg
                    : "OCR auto-processing disabled in System Settings — OCR layer skipped in audit trail.",
                triggeredByUserId,
                ct);

            var aiPassed = result.DocumentClass is "Valid Submission" or "Requires Human Review";
            var findings = DescribeCalsvFindings(result.FieldResultsJson, result.DocumentClass);
            var aiMsg = !settings.AiValidationEnabled
                ? "AI validation engine disabled in System Settings."
                : aiPassed
                    ? $"AI validation passed. CALSV: {findings}. Score {scorePct}%."
                    : $"AI validation flagged. CALSV: {result.DocumentClass}; {findings}. Score {scorePct}%.";
            await _tracking.RecordAsync(
                doc.Id,
                nextStatus,
                WorkflowStage.Calsv,
                aiMsg,
                triggeredByUserId,
                ct);

            await _db.SaveChangesAsync(ct);

            await _notifications.SendAsync(
                userId: doc.SubmittedByUserId,
                title: nextStatus == DocumentStatus.Flagged
                    ? "Document requires revision"
                    : "Document ready to confirm",
                message: nextStatus == DocumentStatus.Flagged
                    ? $"Document {doc.DocumentNumber}: {result.DocumentClass}"
                    : $"Document {doc.DocumentNumber}: {result.DocumentClass}. Review OCR/validation, then Confirm Submission.",
                relatedDocumentId: doc.Id,
                ct: ct);

            await _audit.LogAsync(
                "Document.CalsvCompleted",
                nameof(Document),
                doc.Id.ToString(),
                new { result.DocumentClass, result.Confidence, nextStatus, AwaitingConfirm = nextStatus == DocumentStatus.Submitted },
                triggeredByUserId,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CALSV failed for document {DocumentId}", documentId);
            await FailAsync(doc, $"CALSV engine error: {ex.Message}", ct);
        }
    }

    private void PersistLayerLogs(Guid documentId, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("preprocessing", out var pre))
            {
                _db.ImagePreprocessingLogs.Add(new ImagePreprocessingLog
                {
                    DocumentId = documentId,
                    StepsJson  = pre.TryGetProperty("steps", out var steps) ? steps.GetRawText() : "[]",
                    DeskewAngle = pre.TryGetProperty("deskew_angle", out var angle)
                        ? angle.GetDecimal()
                        : 0m,
                });
            }

            // Prefer nested ocr object; fall back to meta/token summaries from CALSV response
            JsonElement? ocrEl = null;
            if (root.TryGetProperty("ocr", out var ocrObj))
                ocrEl = ocrObj;
            else if (root.TryGetProperty("tokens", out var tokensEl))
                ocrEl = tokensEl;

            string fullText = "";
            decimal avgConf = 0m;
            string tokensJson = "[]";

            if (ocrEl is { } ocr)
            {
                if (ocr.ValueKind == JsonValueKind.Object)
                {
                    if (ocr.TryGetProperty("full_text", out var ft))
                        fullText = ft.GetString() ?? "";
                    if (ocr.TryGetProperty("avg_confidence", out var ac) && ac.TryGetDecimal(out var acd))
                        avgConf = acd;
                    if (ocr.TryGetProperty("token_count", out var tcount) && tcount.TryGetInt32(out var tcVal)
                        && string.IsNullOrWhiteSpace(fullText) == false)
                    {
                        // keep avg as reported; token_count used only for logging
                        _logger.LogDebug("OCR tokens={TokenCount} for {DocumentId}", tcVal, documentId);
                    }
                    tokensJson = ocr.TryGetProperty("tokens", out var toks) ? toks.GetRawText() : "[]";

                    // Persist low-confidence flags when token array is present
                    if (ocr.TryGetProperty("tokens", out var tokenArr) && tokenArr.ValueKind == JsonValueKind.Array)
                        PersistOcrErrors(documentId, tokenArr);
                    else if (ocr.ValueKind == JsonValueKind.Array)
                        PersistOcrErrors(documentId, ocr);
                }
                else if (ocr.ValueKind == JsonValueKind.Array)
                {
                    tokensJson = ocr.GetRawText();
                    PersistOcrErrors(documentId, ocr);
                }

                _db.OCRResults.Add(new OCRResult
                {
                    DocumentId    = documentId,
                    FullText      = fullText,
                    AvgConfidence = ScoreNormalizer.ToUnit(avgConf),
                    TokensJson    = tokensJson,
                });
            }
            else if (root.TryGetProperty("meta", out var meta))
            {
                // CALSV often returns only meta counts; still record a stub OCR row
                var tokenCount = meta.TryGetProperty("ocr_token_count", out var tc) ? tc.GetInt32() : 0;
                var lowCount   = meta.TryGetProperty("low_confidence_token_count", out var lc) ? lc.GetInt32() : 0;
                _db.OCRResults.Add(new OCRResult
                {
                    DocumentId    = documentId,
                    FullText      = "",
                    AvgConfidence = tokenCount == 0 ? 0m : Math.Max(0m, 1m - (decimal)lowCount / tokenCount),
                    TokensJson    = meta.GetRawText(),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist layer logs for {DocumentId}", documentId);
        }
    }

    private void PersistOcrErrors(Guid documentId, JsonElement tokens)
    {
        const double threshold = 60.0;
        var count = 0;
        foreach (var t in tokens.EnumerateArray())
        {
            if (count >= 100) break; // cap noise

            var text = t.TryGetProperty("text", out var tx) ? tx.GetString() ?? ""
                : t.TryGetProperty("token", out var tk) ? tk.GetString() ?? ""
                : "";

            double conf = 100;
            if (t.TryGetProperty("confidence", out var c))
            {
                if (c.TryGetDouble(out var cd)) conf = cd;
                else if (c.TryGetDecimal(out var cdec)) conf = (double)cdec;
            }

            var low = t.TryGetProperty("low_confidence", out var lf) && lf.ValueKind == JsonValueKind.True;
            if (!low && conf >= threshold) continue;
            if (!IsMeaningfulOcrToken(text)) continue;

            _db.OCRErrorLogs.Add(new OCRErrorLog
            {
                DocumentId = documentId,
                Token      = text.Length > 256 ? text[..256] : text,
                Confidence = conf,
                Flag       = "LOW_CONFIDENCE",
            });
            count++;
        }
    }

    private static bool IsMeaningfulOcrToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var alnum = text.Count(char.IsLetterOrDigit);
        return alnum >= 2 && text.Length >= 3;
    }

    private void PersistAttachmentOcrResults(
        List<DocumentAttachment> attachments, 
        IReadOnlyList<CalsvAttachmentOcrResult> ocrResults)
    {
        foreach (var ocrResult in ocrResults)
        {
            var attachment = attachments.FirstOrDefault(a => 
                string.Equals(a.AttachmentType, ocrResult.AttachmentType, StringComparison.OrdinalIgnoreCase));
            
            if (attachment is null)
            {
                _logger.LogWarning("Attachment OCR result for type {Type} has no matching attachment", ocrResult.AttachmentType);
                continue;
            }

            if (ocrResult.Status == "failed")
            {
                attachment.ProcessingStatus = ProcessingStatus.Failed;
                attachment.ProcessingError = ocrResult.Error;
                _logger.LogWarning("Attachment {AttachmentId} ({Type}) processing failed: {Error}", 
                    attachment.Id, attachment.AttachmentType, ocrResult.Error);
            }
            else
            {
                attachment.ProcessingStatus = ProcessingStatus.Completed;
                attachment.ProcessingError = null;

                // Create AttachmentOCRResult record
                _db.AttachmentOCRResults.Add(new AttachmentOCRResult
                {
                    AttachmentId = attachment.Id,
                    FullText = ocrResult.FullText,
                    AvgConfidence = ocrResult.AvgConfidence,
                    TokenCount = ocrResult.TokenCount,
                    LowConfidenceCount = ocrResult.LowConfidenceCount,
                    Engine = ocrResult.Engine,
                    TokensJson = "[]",  // Could be populated if ML returns tokens in future
                });

                _logger.LogInformation("Attachment {AttachmentId} ({Type}) OCR completed: {TokenCount} tokens, {AvgConf:P0} confidence",
                    attachment.Id, attachment.AttachmentType, ocrResult.TokenCount, ocrResult.AvgConfidence);
            }
        }
    }

    private async Task StartApprovalWorkflowAsync(Document doc, CancellationToken ct)
    {
        // Close any previous incomplete workflows (e.g. after return/resubmit)
        var open = await _db.ApprovalWorkflows
            .Where(w => w.DocumentId == doc.Id && !w.IsComplete)
            .ToListAsync(ct);
        foreach (var w in open)
            w.IsComplete = true;

        var workflow = new ApprovalWorkflow
        {
            DocumentId   = doc.Id,
            CurrentStage = WorkflowStage.Adviser,
            IsComplete   = false,
        };
        _db.ApprovalWorkflows.Add(workflow);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("Workflow.Started", nameof(ApprovalWorkflow),
            workflow.Id.ToString(), new { doc.DocumentNumber, stage = "Adviser" }, null, ct);

        // Notify advisers assigned to this organization
        var orgId = doc.OrganizationId;

        var adviserIds = await _db.OrganizationMemberships
            .Where(m =>
                m.OrganizationId == orgId
                && m.IsActive
                && m.MembershipRole == MembershipRole.Adviser)
            .Select(m => m.UserAccountId)
            .ToListAsync(ct);

        if (doc.Organization?.PrimaryAdviserUserId is Guid primaryAdviser)
            adviserIds.Add(primaryAdviser);

        if (adviserIds.Count == 0 && doc.Organization?.CollegeId is Guid collegeId)
        {
            adviserIds = await _db.UserAccounts
                .Where(u => u.IsActive && u.CollegeId == collegeId && u.Role!.Code == "Adviser")
                .Select(u => u.Id)
                .ToListAsync(ct);
        }

        foreach (var adviserId in adviserIds.Distinct())
        {
            await _notifications.SendAsync(
                adviserId,
                "Document awaiting your review",
                $"Document {doc.DocumentNumber} from {doc.Organization?.Name ?? "an organization"} is ready for adviser review.",
                doc.Id,
                ct);
        }
    }

    private static (decimal AvgConfidence, int TokenCount, int LowConfidenceCount) ExtractOcrStats(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
            var root = doc.RootElement;

            decimal avg = 0m;
            var tokens = 0;
            var low = 0;

            if (root.TryGetProperty("ocr", out var ocr) && ocr.ValueKind == JsonValueKind.Object)
            {
                if (ocr.TryGetProperty("avg_confidence", out var ac) && ac.TryGetDecimal(out var acd))
                    avg = acd;
                if (ocr.TryGetProperty("token_count", out var tc) && tc.TryGetInt32(out var tci))
                    tokens = tci;
                if (ocr.TryGetProperty("tokens", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    if (tokens == 0) tokens = arr.GetArrayLength();
                    foreach (var t in arr.EnumerateArray())
                    {
                        var isLow = t.TryGetProperty("low_confidence", out var lf) && lf.ValueKind == JsonValueKind.True;
                        if (isLow) low++;
                    }
                }
            }
            else if (root.TryGetProperty("meta", out var meta))
            {
                if (meta.TryGetProperty("ocr_token_count", out var tc) && tc.TryGetInt32(out var tci))
                    tokens = tci;
                if (meta.TryGetProperty("low_confidence_token_count", out var lc) && lc.TryGetInt32(out var lci))
                    low = lci;
            }

            return (avg, tokens, low);
        }
        catch
        {
            return (0m, 0, 0);
        }
    }

    private static int FormatOcrPercent(decimal avg) => ScoreNormalizer.ToPercent(avg);

    /// <summary>Figure 25 audit line — format + signature from LayoutLM field labels (not an LLM).</summary>
    private static string DescribeCalsvFindings(string fieldResultsJson, string documentClass)
    {
        var formatOk = documentClass is "Valid Submission" or "Requires Human Review";
        var parts = new List<string>
        {
            formatOk ? "format compliant" : "format issues detected",
        };

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(fieldResultsJson) ? "[]" : fieldResultsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in doc.RootElement.EnumerateArray())
                {
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.Contains("Signature", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("Adviser", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var label = f.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
                    parts.Add(label is "PRESENT" or "SIGNED"
                        ? "signature detected"
                        : "signature not detected");
                    break;
                }
            }
        }
        catch
        {
            // keep format-only summary
        }

        if (parts.Count == 1)
            parts.Add("fields checked");

        return string.Join(", ", parts);
    }

    private async Task FailAsync(Document doc, string message, CancellationToken ct)
    {
        doc.Status       = DocumentStatus.Flagged;
        doc.CurrentStage = WorkflowStage.Officer;
        doc.UpdatedAt    = DateTimeOffset.UtcNow;
        await _tracking.RecordAsync(
            doc.Id,
            DocumentStatus.Flagged,
            WorkflowStage.Officer,
            message,
            ct: ct);
        await _db.SaveChangesAsync(ct);

        await _notifications.SendAsync(
            doc.SubmittedByUserId,
            "Document validation failed",
            $"Document {doc.DocumentNumber}: {message}",
            doc.Id,
            ct);
    }

    private async Task<CalsvOrgCatalogDto> BuildOrgCatalogAsync(Document doc, CancellationToken ct)
    {
        var orgs = await _db.Organizations
            .AsNoTracking()
            .Include(o => o.CollegeRef)
            .Include(o => o.PrimaryAdviser)
            .Where(o => o.Status == "Active")
            .ToListAsync(ct);

        var submitted = doc.Organization;
        return new CalsvOrgCatalogDto(
            SubmittedName: submitted?.Name,
            SubmittedAcronym: submitted?.Acronym,
            SubmittedCollegeCode: submitted?.CollegeRef?.Code ?? submitted?.College,
            SubmittedAdviserName: submitted?.PrimaryAdviser?.FullName,
            Organizations: orgs.Select(o => new CalsvCatalogOrgDto(
                Name: o.Name,
                Acronym: o.Acronym,
                CollegeCode: o.CollegeRef?.Code ?? o.College,
                CollegeName: o.CollegeRef?.Name,
                AdviserName: o.PrimaryAdviser?.FullName,
                Aliases: BuildOrgAliases(o.Name, o.Acronym))).ToList());
    }

    private static IReadOnlyList<string> BuildOrgAliases(string name, string acronym)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                aliases.Add(value.Trim());
        }

        Add(name);
        Add(acronym);
        Add(name.Replace("-", " "));
        Add(name.Replace("'", ""));
        Add(name.Replace("'", "'"));

        if (name.EndsWith(" - SC", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("- SC", StringComparison.OrdinalIgnoreCase)
            || acronym.EndsWith("-SC", StringComparison.OrdinalIgnoreCase))
        {
            var college = acronym.Replace("-SC", "", StringComparison.OrdinalIgnoreCase).Trim();
            Add($"{college} Student Council");
            Add($"{college} SC");
        }

        if (name.Contains("Programmers", StringComparison.OrdinalIgnoreCase))
        {
            Add("Programmers' Den");
            Add("Programmers Den");
            Add("Programmer's Den");
        }

        if (name.Contains("Artist Circle", StringComparison.OrdinalIgnoreCase))
        {
            Add("Artist's Circle");
            Add("Artists Circle");
        }

        if (name.Contains("Supreme Student Council", StringComparison.OrdinalIgnoreCase))
            Add("SSC");

        aliases.Remove(name);
        aliases.Remove(acronym);
        return aliases.ToList();
    }
}
