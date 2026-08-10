using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Documents.Services;
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
    private readonly ILogger<CalsvOrchestrator> _logger;

    public CalsvOrchestrator(
        IApplicationDbContext db,
        ICalsvClient calsv,
        IBlobService blob,
        IAuditService audit,
        INotificationService notifications,
        ITrackingRecorder tracking,
        ILogger<CalsvOrchestrator> logger)
    {
        _db = db;
        _calsv = calsv;
        _blob = blob;
        _audit = audit;
        _notifications = notifications;
        _tracking = tracking;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid documentId, Guid? triggeredByUserId = null, CancellationToken ct = default)
    {
        var doc = await _db.Documents
            .Include(d => d.DocumentType)
            .Include(d => d.Attachments)
            .Include(d => d.Organization)
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
            await _db.SaveChangesAsync(ct);

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
                Attachments: attachmentDtos);

            var result = await _calsv.ValidateAsync(request, ct);

            // Persist Layer 1 / Layer 2 summary when present in raw JSON
            PersistLayerLogs(doc.Id, result.RawResponseJson);

            _db.AIValidationResults.Add(new AIValidationResult
            {
                DocumentId          = doc.Id,
                DocumentClass       = result.DocumentClass,
                Confidence          = result.Confidence,
                RequiresHumanReview = result.RequiresHumanReview,
                FieldResultsJson    = result.FieldResultsJson,
                RawResponseJson     = result.RawResponseJson,
                ModelVersion        = result.ModelVersion,
                ProcessedAt         = DateTimeOffset.UtcNow,
            });

            var nextStatus = result.DocumentClass switch
            {
                "Valid Submission"      => DocumentStatus.UnderReview,
                "Requires Human Review" => DocumentStatus.UnderReview,
                "Incomplete Submission" => DocumentStatus.Flagged,
                "Attachment Missing"    => DocumentStatus.Flagged,
                "Structurally Invalid"  => DocumentStatus.Flagged,
                _                       => DocumentStatus.Flagged,
            };

            var nextStage = nextStatus == DocumentStatus.UnderReview
                ? WorkflowStage.Adviser
                : WorkflowStage.Officer;

            doc.Status       = nextStatus;
            doc.CurrentStage = nextStage;
            doc.UpdatedAt    = DateTimeOffset.UtcNow;

            await _tracking.RecordAsync(
                doc.Id,
                nextStatus,
                nextStage,
                $"CALSV validation complete: {result.DocumentClass} (confidence {result.Confidence:P0})",
                triggeredByUserId,
                ct);

            await _db.SaveChangesAsync(ct);

            await _notifications.SendAsync(
                userId: doc.SubmittedByUserId,
                title: nextStatus == DocumentStatus.Flagged
                    ? "Document requires revision"
                    : "Document passed validation",
                message: $"Document {doc.DocumentNumber}: {result.DocumentClass}",
                relatedDocumentId: doc.Id,
                ct: ct);

            if (nextStatus == DocumentStatus.UnderReview)
                await StartApprovalWorkflowAsync(doc, ct);

            await _audit.LogAsync(
                "Document.CalsvCompleted",
                nameof(Document),
                doc.Id.ToString(),
                new { result.DocumentClass, result.Confidence, nextStatus },
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
                    AvgConfidence = avgConf,
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
}
