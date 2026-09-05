using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Application.Features.Tracking.Queries;

/// <summary>
/// Figure 24 — SOU Admin global Document Tracker.
/// One <c>Documents</c> table; university-wide lens via Organization.CollegeId
/// (not separate Officer/Adviser/SOU document models).
/// </summary>
public sealed record GetSouTrackerQuery(
    TrackerTab Tab = TrackerTab.All,
    string? Search = null,
    string? College = null,
    Guid? OrganizationId = null,
    string ComplianceBand = "all",
    int Page = 1,
    int PageSize = 100);

public sealed record CollegeChipDto(string Code, string Name);

public sealed record SouTrackerDocumentDto(
    Guid Id,
    string DocumentNumber,
    string Title,
    string OrganizationName,
    string? CollegeCode,
    string PipelineLabel,
    string Status,
    string CurrentStage,
    DateTimeOffset SubmittedAt,
    string? AssignedTo,
    int? Score,
    int FlagCount,
    int DaysElapsed,
    string CleanLabel);

public sealed record SouTrackerResult(
    IReadOnlyList<SouTrackerDocumentDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<TrackerTabCountDto> TabCounts,
    IReadOnlyList<CollegeChipDto> Colleges,
    IReadOnlyList<OrgComplianceDto> Organizations);

public sealed class GetSouTrackerHandler
{
    private readonly IApplicationDbContext _db;

    public GetSouTrackerHandler(IApplicationDbContext db) => _db = db;

    public async Task<SouTrackerResult> HandleAsync(GetSouTrackerQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        var now = DateTimeOffset.UtcNow;

        var colleges = await _db.Colleges
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new CollegeChipDto(c.Code, c.Name))
            .ToListAsync(ct);

        var orgs = await _db.Organizations
            .AsNoTracking()
            .Include(o => o.CollegeRef)
            .Where(o => o.Status == "Active")
            .OrderBy(o => o.Name)
            .ToListAsync(ct);

        var docsQuery = _db.Documents
            .AsNoTracking()
            .Where(d => d.Status != DocumentStatus.Draft);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            docsQuery = docsQuery.Where(d =>
                d.DocumentNumber.Contains(term)
                || d.Title.Contains(term)
                || d.Organization!.Name.Contains(term)
                || d.Organization!.Acronym.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(q.College))
        {
            var college = q.College.Trim();
            docsQuery = docsQuery.Where(d =>
                d.Organization!.College == college
                || d.Organization!.CollegeRef!.Code == college);
        }

        if (q.OrganizationId.HasValue)
            docsQuery = docsQuery.Where(d => d.OrganizationId == q.OrganizationId.Value);

        var orgIds = orgs.Select(o => o.Id).ToList();

        var deans = await _db.UserAccounts
            .AsNoTracking()
            .Include(u => u.Role)
            .Where(u => u.IsActive && u.Role!.Code == "Dean" && u.CollegeId != null)
            .ToListAsync(ct);
        var deanByCollege = deans
            .GroupBy(u => u.CollegeId!.Value)
            .ToDictionary(g => g.Key, g => g.First().FullName);

        var lifecycleByOrg = await _db.Documents
            .AsNoTracking()
            .Where(d => d.Status != DocumentStatus.Draft && orgIds.Contains(d.OrganizationId))
            .Select(d => new { d.OrganizationId, d.Status })
            .ToListAsync(ct);

        var orgCompliance = orgs
            .Select(org =>
            {
                var rows = lifecycleByOrg.Where(d => d.OrganizationId == org.Id).ToList();
                var submissions = rows.Count;
                var approved = rows.Count(d =>
                    d.Status is DocumentStatus.Approved or DocumentStatus.Archived);
                var college = org.CollegeRef?.Code ?? org.College;
                return new OrgComplianceDto(
                    org.Id,
                    org.Name,
                    string.IsNullOrWhiteSpace(org.Acronym) ? null : org.Acronym,
                    college,
                    submissions,
                    approved,
                    submissions == 0 ? 0 : (int)Math.Round(100.0 * approved / submissions));
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(q.College))
        {
            orgCompliance = orgCompliance
                .Where(o => string.Equals(o.College, q.College, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Figure 24 left rail: High / Mid / At risk is org compliance, not CALSV score.
        var bandedOrgs = orgCompliance
            .Where(o => MatchesOrgBand(q.ComplianceBand, o.CompliancePercent))
            .ToList();

        if (!string.Equals(q.ComplianceBand, "all", StringComparison.OrdinalIgnoreCase))
        {
            var bandOrgIds = bandedOrgs.Select(o => o.OrganizationId).ToList();
            docsQuery = docsQuery.Where(d => bandOrgIds.Contains(d.OrganizationId));
        }

        var keys = await docsQuery
            .Select(d => new
            {
                d.Id,
                d.Status,
                d.CurrentStage,
                SubmittedAt = d.MetadataLockedAt ?? d.CreatedAt,
            })
            .ToListAsync(ct);

        var tabCounts = Enum.GetValues<TrackerTab>()
            .Where(t => t is not TrackerTab.Returned)
            .Select(tab => new TrackerTabCountDto(
                tab.ToString(),
                keys.Count(d => DocumentTimelineProjector.MatchesTab(tab, d.Status, d.CurrentStage))))
            .ToList();

        var filteredKeys = keys
            .Where(d => DocumentTimelineProjector.MatchesTab(q.Tab, d.Status, d.CurrentStage))
            .OrderByDescending(d => d.SubmittedAt)
            .ToList();

        var pageIds = filteredKeys
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => d.Id)
            .ToList();

        var docs = pageIds.Count == 0
            ? []
            : await _db.Documents
                .AsNoTracking()
                .Include(d => d.Organization)!.ThenInclude(o => o!.CollegeRef)
                .Include(d => d.Organization)!.ThenInclude(o => o!.PrimaryAdviser)
                .Include(d => d.DocumentType)
                .Include(d => d.Workflow)
                .Where(d => pageIds.Contains(d.Id))
                .ToListAsync(ct);

        var byId = docs.ToDictionary(d => d.Id);
        var pageDocs = pageIds.Select(id => byId[id]).ToList();
        var docIds = pageIds;

        var validations = docIds.Count == 0
            ? []
            : await _db.AIValidationResults
                .AsNoTracking()
                .Where(v => docIds.Contains(v.DocumentId))
                .OrderByDescending(v => v.ProcessedAt)
                .ToListAsync(ct);
        var valMap = validations
            .GroupBy(v => v.DocumentId)
            .ToDictionary(g => g.Key, g => g.First());

        var flagRows = docIds.Count == 0
            ? []
            : await _db.OCRErrorLogs
                .AsNoTracking()
                .Where(f => docIds.Contains(f.DocumentId))
                .GroupBy(f => f.DocumentId)
                .Select(g => new { DocumentId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
        var flagMap = flagRows.ToDictionary(f => f.DocumentId, f => f.Count);

        var pageItems = pageDocs.Select(d =>
        {
            valMap.TryGetValue(d.Id, out var v);
            var score = v is null ? (int?)null : ScoreNormalizer.ToPercent(v.Confidence);
            var flags = flagMap.TryGetValue(d.Id, out var fc) ? fc : 0;
            if (v is { RequiresHumanReview: true } && flags == 0)
                flags = 1;

            var submitted = d.MetadataLockedAt ?? d.CreatedAt;
            var last = d.UpdatedAt ?? submitted;
            var stage = d.Workflow?.CurrentStage ?? d.CurrentStage;
            var timeline = DocumentTimelineProjector.Project(d.Status, stage);

            return new SouTrackerDocumentDto(
                d.Id,
                d.DocumentNumber,
                string.IsNullOrWhiteSpace(d.Title) ? (d.DocumentType?.Name ?? d.DocumentNumber) : d.Title,
                d.Organization?.Name ?? "—",
                d.Organization?.CollegeRef?.Code ?? d.Organization?.College,
                PipelineBadge(timeline.OverallStatus),
                d.Status.ToString(),
                stage.ToString(),
                submitted,
                AssignedTo(d, deanByCollege),
                score,
                flags,
                Math.Max(0, (int)(now - last).TotalDays),
                flags > 0 ? $"{flags} flag{(flags == 1 ? "" : "s")}" : (v is null ? "" : "Clean"));
        }).ToList();

        return new SouTrackerResult(
            pageItems,
            filteredKeys.Count,
            page,
            pageSize,
            tabCounts,
            colleges,
            bandedOrgs);
    }

    private static bool MatchesOrgBand(string band, int percent) => band switch
    {
        "high" => percent is >= 80,
        "mid" => percent is >= 60 and < 80,
        "risk" => percent < 60,
        _ => true,
    };

    private static string PipelineBadge(string overall) => overall switch
    {
        "Submitted" => "Submitted",
        "AI validating" => "AI validated",
        "Flagged — needs revision" => "AI validated",
        "Adviser review" => "Adviser review",
        "Dean review" => "Dean review",
        "SOU review" => "SOU review",
        "Approved" or "Archived" => "Approved",
        "Returned for revision" => "Returned",
        _ => overall,
    };

    private static string? AssignedTo(
        Domain.Entities.Document d,
        IReadOnlyDictionary<Guid, string> deanByCollege)
    {
        if (d.Status == DocumentStatus.Returned)
            return "Awaiting officer resubmission";

        if (d.Status != DocumentStatus.UnderReview)
            return null;

        var stage = d.Workflow?.CurrentStage ?? d.CurrentStage;
        return stage switch
        {
            WorkflowStage.Adviser => d.Organization?.PrimaryAdviser?.FullName ?? "Organization adviser",
            WorkflowStage.Dean when d.Organization?.CollegeId is Guid cid
                && deanByCollege.TryGetValue(cid, out var dean) => dean,
            WorkflowStage.Dean => string.IsNullOrWhiteSpace(d.Organization?.College)
                ? "College Dean"
                : $"College Dean ({d.Organization.College})",
            WorkflowStage.Sou => "SOU staff",
            _ => null,
        };
    }
}
