using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking;
using TsuOrg.Application.Features.Tracking.Queries;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Document Tracking &amp; Monitoring (DTM-01..03).
/// Thin controller — all rules live in Application/Features/Tracking.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class TrackingController : ControllerBase
{
    private readonly GetTrackerListHandler _list;
    private readonly GetTrackingTimelineHandler _timeline;
    private readonly GetAnalyticsSummaryHandler _analytics;

    public TrackingController(
        GetTrackerListHandler list,
        GetTrackingTimelineHandler timeline,
        GetAnalyticsSummaryHandler analytics)
    {
        _list = list;
        _timeline = timeline;
        _analytics = analytics;
    }

    /// <summary>
    /// DTM-01: Role-scoped tracker board with tab filters and pipeline cards.
    /// Tabs: All | Submitted | AiValidated | AdviserReview | DeanReview | Approved
    /// </summary>
    [HttpGet("tracking")]
    public async Task<IActionResult> List(
        [FromQuery] TrackerTab tab = TrackerTab.All,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _list.HandleAsync(
            new GetTrackerListQuery(GetUserId(), GetRole(), tab, page, pageSize), ct);
        return Ok(result);
    }

    /// <summary>DTM-01 / DTM-02: Document status + step-by-step timeline + history events.</summary>
    [HttpGet("tracking/{documentId:guid}")]
    public async Task<IActionResult> GetTimeline(Guid documentId, CancellationToken ct = default)
    {
        try
        {
            var result = await _timeline.HandleAsync(new GetTrackingTimelineQuery(
                documentId, GetUserId(), GetRole()), ct);
            return Ok(result);
        }
        catch (NotFoundException ex) { return NotFound(Problem(ex.Message)); }
        catch (ForbiddenException) { return Forbid(); }
    }

    /// <summary>Alias for timeline (DTM-02).</summary>
    [HttpGet("tracking/{documentId:guid}/timeline")]
    public Task<IActionResult> GetTimelineAlias(Guid documentId, CancellationToken ct = default)
        => GetTimeline(documentId, ct);

    /// <summary>DTM-03: Pending vs processed analytics for admins.</summary>
    [HttpGet("analytics/summary")]
    [Authorize(Roles = "SouStaff,SystemAdmin,Dean")]
    public async Task<IActionResult> AnalyticsSummary(
        [FromQuery] Guid? organizationId,
        [FromQuery] Guid? academicYearId,
        CancellationToken ct = default)
    {
        var result = await _analytics.HandleAsync(
            new GetAnalyticsSummaryQuery(organizationId, academicYearId), ct);
        return Ok(result);
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private string GetRole() =>
        User.FindFirstValue(ClaimTypes.Role) ?? "OrgOfficer";
}
