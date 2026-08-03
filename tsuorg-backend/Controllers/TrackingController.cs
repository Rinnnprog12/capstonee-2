using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Tracking.Queries;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Document Tracking &amp; Monitoring (DTM-01..03).
/// Real-time status, visual timeline, analytics panel.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class TrackingController : ControllerBase
{
    private readonly GetTrackingTimelineHandler _timeline;
    private readonly GetAnalyticsSummaryHandler _analytics;

    public TrackingController(
        GetTrackingTimelineHandler timeline,
        GetAnalyticsSummaryHandler analytics)
    {
        _timeline = timeline;
        _analytics = analytics;
    }

    /// <summary>DTM-01 / DTM-02: Document status + step-by-step timeline.</summary>
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
