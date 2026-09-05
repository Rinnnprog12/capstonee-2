using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking.Queries;

// ─── SOU System Reports & Analytics (Figure 27) ──────────────────────────────
// Data-driven KPIs from Document → OCR → AIValidation → ApprovalHistory.
// Export cards produce CSV for decision-making (no seeded fake metrics).

public sealed record GetSystemReportsQuery;

public sealed record MonthlySubmissionsDto(string Label, int Year, int Month, int Count);

public sealed record ProcessingTimeDto(
    double? OcrMinutes,
    double? AiValidationMinutes,
    double? AdviserReviewDays,
    double? FullCycleDays);

public sealed record SystemReportsDto(
    IReadOnlyList<MonthlySubmissionsDto> MonthlySubmissions,
    ProcessingTimeDto ProcessingTime,
    int TotalValidated,
    int ValidationSuccessRate,
    string? AcademicYearLabel);

public sealed class GetSystemReportsHandler
{
    private const int TrendMonths = 5;

    private readonly IApplicationDbContext _db;

    public GetSystemReportsHandler(IApplicationDbContext db) => _db = db;

    public async Task<SystemReportsDto> HandleAsync(CancellationToken ct = default)
    {
        var ayLabel = await _db.AcademicYears
            .AsNoTracking()
            .Where(a => a.IsCurrent)
            .Select(a => a.Label)
            .FirstOrDefaultAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var rangeStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .AddMonths(-(TrendMonths - 1));

        var docs = await _db.Documents
            .AsNoTracking()
            .Where(d => d.Status != DocumentStatus.Draft)
            .Select(d => new { d.Id, d.CreatedAt })
            .ToListAsync(ct);
        var createdAt = docs.ToDictionary(d => d.Id, d => d.CreatedAt);

        // ── Monthly submission volume (Figure 27 bar chart) ───────────────
        var monthly = new List<MonthlySubmissionsDto>(TrendMonths);
        for (var i = 0; i < TrendMonths; i++)
        {
            var month = rangeStart.AddMonths(i);
            var next = month.AddMonths(1);
            monthly.Add(new MonthlySubmissionsDto(
                month.ToString("MMM", CultureInfo.InvariantCulture),
                month.Year,
                month.Month,
                docs.Count(d => d.CreatedAt >= month && d.CreatedAt < next)));
        }

        // ── Pipeline turnaround (per-document earliest stage timestamps) ──
        var ocrByDoc = await _db.OCRResults
            .AsNoTracking()
            .GroupBy(o => o.DocumentId)
            .Select(g => new { DocumentId = g.Key, At = g.Min(o => o.CreatedAt) })
            .ToListAsync(ct);

        var aiByDoc = await _db.AIValidationResults
            .AsNoTracking()
            .GroupBy(v => v.DocumentId)
            .Select(g => new
            {
                DocumentId = g.Key,
                At = g.Min(v => v.ProcessedAt),
                LatestRequiresReview = g.OrderByDescending(v => v.ProcessedAt).First().RequiresHumanReview,
            })
            .ToListAsync(ct);

        var ocrMinutes = AverageMinutes(ocrByDoc
            .Where(o => createdAt.ContainsKey(o.DocumentId))
            .Select(o => (o.At - createdAt[o.DocumentId]).TotalMinutes));

        var aiMinutes = AverageMinutes(aiByDoc
            .Where(v => createdAt.ContainsKey(v.DocumentId))
            .Select(v => (v.At - createdAt[v.DocumentId]).TotalMinutes));

        var adviserFirst = await _db.ApprovalHistories
            .AsNoTracking()
            .Where(h => h.ActorRole == AppRole.Adviser)
            .GroupBy(h => h.Workflow!.DocumentId)
            .Select(g => new { DocumentId = g.Key, At = g.Min(h => h.DecidedAt) })
            .ToListAsync(ct);

        var adviserDays = AverageDays(adviserFirst
            .Where(a => createdAt.ContainsKey(a.DocumentId))
            .Select(a => (a.At - createdAt[a.DocumentId]).TotalDays));

        var completedApprovals = await _db.ApprovalHistories
            .AsNoTracking()
            .Where(h => h.Action == ApprovalAction.Approve && h.Workflow!.IsComplete)
            .GroupBy(h => h.Workflow!.DocumentId)
            .Select(g => new { DocumentId = g.Key, At = g.Max(h => h.DecidedAt) })
            .ToListAsync(ct);

        var fullCycleDays = AverageDays(completedApprovals
            .Where(c => createdAt.ContainsKey(c.DocumentId))
            .Select(c => (c.At - createdAt[c.DocumentId]).TotalDays));

        // ── Validation KPIs (CALSV output) ────────────────────────────────
        var totalValidated = aiByDoc.Count;
        var cleanCount = aiByDoc.Count(v => !v.LatestRequiresReview);
        var successRate = totalValidated == 0
            ? 0
            : (int)Math.Round(100.0 * cleanCount / totalValidated);

        return new SystemReportsDto(
            monthly,
            new ProcessingTimeDto(ocrMinutes, aiMinutes, adviserDays, fullCycleDays),
            totalValidated,
            successRate,
            ayLabel);
    }

    private static double? AverageMinutes(IEnumerable<double> values)
    {
        var list = values.Where(v => v >= 0).ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 1);
    }

    private static double? AverageDays(IEnumerable<double> values)
    {
        var list = values.Where(v => v >= 0).ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 1);
    }
}

// ─── CSV exports (Figure 27 "Export reports") ────────────────────────────────

public sealed class ExportReportHandler
{
    private readonly IApplicationDbContext _db;

    public ExportReportHandler(IApplicationDbContext db) => _db = db;

    public async Task<(string FileName, string Csv)?> HandleAsync(string type, CancellationToken ct = default)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
        return type.ToLowerInvariant() switch
        {
            "compliance-summary" => ($"compliance-summary-{stamp}.csv", await ComplianceSummaryAsync(ct)),
            "audit-trail" => ($"document-audit-trail-{stamp}.csv", await AuditTrailAsync(ct)),
            "organization-status" => ($"organization-status-{stamp}.csv", await OrganizationStatusAsync(ct)),
            "adviser-performance" => ($"adviser-performance-{stamp}.csv", await AdviserPerformanceAsync(ct)),
            _ => null,
        };
    }

    private async Task<string> ComplianceSummaryAsync(CancellationToken ct)
    {
        var lifecycle = await _db.Documents
            .AsNoTracking()
            .Where(d => d.Status != DocumentStatus.Draft)
            .GroupBy(d => new
            {
                d.OrganizationId,
                OrgName = d.Organization!.Name,
                College = d.Organization.College,
            })
            .Select(g => new
            {
                g.Key.OrganizationId,
                g.Key.OrgName,
                g.Key.College,
                Submissions = g.Count(),
                Approved = g.Count(d => d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Archived),
                Returned = g.Count(d => d.Status == DocumentStatus.Returned || d.Status == DocumentStatus.Rejected),
            })
            .OrderBy(r => r.OrgName)
            .ToListAsync(ct);

        var mlRows = await (
            from v in _db.AIValidationResults.AsNoTracking()
            join d in _db.Documents.AsNoTracking() on v.DocumentId equals d.Id
            where d.Status != DocumentStatus.Draft
            select new { d.OrganizationId, v.DocumentId, v.Confidence, v.ProcessedAt }
        ).ToListAsync(ct);

        var mlByOrg = mlRows
            .GroupBy(r => r.DocumentId)
            .Select(g => g.OrderByDescending(r => r.ProcessedAt).First())
            .GroupBy(r => r.OrganizationId)
            .ToDictionary(
                g => g.Key,
                g => (int)Math.Round(g.Average(r => (double)ScoreNormalizer.ToPercent(r.Confidence))));

        var ay = await _db.AcademicYears.AsNoTracking()
            .Where(a => a.IsCurrent)
            .Select(a => a.Label)
            .FirstOrDefaultAsync(ct) ?? "";

        var sb = new StringBuilder($"AcademicYear,Organization,College,Submissions,Approved,Returned,Compliance %\n");
        foreach (var r in lifecycle)
        {
            var compliance = mlByOrg.TryGetValue(r.OrganizationId, out var ml)
                ? ml
                : r.Submissions == 0 ? 0 : (int)Math.Round(100.0 * r.Approved / r.Submissions);
            sb.AppendLine($"{Escape(ay)},{Escape(r.OrgName)},{Escape(r.College)},{r.Submissions},{r.Approved},{r.Returned},{compliance}");
        }
        return sb.ToString();
    }

    private async Task<string> AuditTrailAsync(CancellationToken ct)
    {
        var decisions = await _db.ApprovalHistories
            .AsNoTracking()
            .OrderByDescending(h => h.DecidedAt)
            .Select(h => new
            {
                h.Workflow!.Document!.DocumentNumber,
                h.Workflow.Document.Title,
                Org = h.Workflow.Document.Organization!.Name,
                Action = h.Action.ToString(),
                By = h.ActorUser!.FullName,
                Role = h.ActorRole.ToString(),
                At = h.DecidedAt,
                h.Comments,
            })
            .ToListAsync(ct);

        var sb = new StringBuilder("Document,Title,Organization,Action,By,Role,Date,Comments\n");
        foreach (var d in decisions)
            sb.AppendLine($"{d.DocumentNumber},{Escape(d.Title)},{Escape(d.Org)},{d.Action},{Escape(d.By)},{d.Role},{d.At:yyyy-MM-dd HH:mm},{Escape(d.Comments)}");
        return sb.ToString();
    }

    private async Task<string> OrganizationStatusAsync(CancellationToken ct)
    {
        var orgs = await _db.Organizations
            .AsNoTracking()
            .Include(o => o.PrimaryAdviser)
            .Include(o => o.Officer)
            .Include(o => o.CollegeRef)
            .OrderBy(o => o.College)
            .ThenBy(o => o.Name)
            .ToListAsync(ct);

        var sb = new StringBuilder("Organization,Acronym,College,Adviser,President,Status\n");
        foreach (var o in orgs)
        {
            var college = o.CollegeRef?.Code ?? o.College;
            sb.AppendLine($"{Escape(o.Name)},{Escape(o.Acronym)},{Escape(college)},{Escape(o.PrimaryAdviser?.FullName)},{Escape(o.Officer?.FullName)},{Escape(o.Status)}");
        }
        return sb.ToString();
    }

    private async Task<string> AdviserPerformanceAsync(CancellationToken ct)
    {
        var decisions = await _db.ApprovalHistories
            .AsNoTracking()
            .Where(h => h.ActorRole == AppRole.Adviser)
            .Select(h => new
            {
                h.ActorUserId,
                Name = h.ActorUser!.FullName,
                h.Action,
                h.DecidedAt,
                SubmittedAt = h.Workflow!.Document!.CreatedAt,
            })
            .ToListAsync(ct);

        var sb = new StringBuilder("Adviser,Decisions,Approved,Returned,Rejected,Approval rate %,Avg turnaround (days)\n");
        foreach (var g in decisions.GroupBy(d => new { d.ActorUserId, d.Name }).OrderBy(g => g.Key.Name))
        {
            var total = g.Count();
            var approved = g.Count(d => d.Action == ApprovalAction.Approve);
            var returned = g.Count(d => d.Action == ApprovalAction.Return);
            var rejected = g.Count(d => d.Action == ApprovalAction.Reject);
            var rate = total == 0 ? 0 : (int)Math.Round(100.0 * approved / total);
            var avgDays = Math.Round(g.Average(d => (d.DecidedAt - d.SubmittedAt).TotalDays), 1);
            sb.AppendLine($"{Escape(g.Key.Name)},{total},{approved},{returned},{rejected},{rate},{avgDays}");
        }
        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
