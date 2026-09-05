using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Features.Tracking.Queries;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Dashboard aggregated data for Figure 5 (Officer) and role portals —
/// lifecycle counts, compliance, flagged items, and recent submissions.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly GetDashboardHandler _handler;
    private readonly GetSouDashboardHandler _souHandler;

    public DashboardController(GetDashboardHandler handler, GetSouDashboardHandler souHandler)
    {
        _handler = handler;
        _souHandler = souHandler;
    }

    /// <summary>
    /// Role-scoped dashboard metrics.
    /// Optional query param orgId scopes OrgOfficer view to a single organization.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] Guid? orgId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var roleStr = User.FindFirstValue(ClaimTypes.Role) ?? "OrgOfficer";

        if (!Enum.TryParse<AppRole>(roleStr, out var role))
            role = AppRole.OrgOfficer;

        var result = await _handler.HandleAsync(new GetDashboardQuery(userId, role, orgId), ct);
        return Ok(result);
    }

    /// <summary>Figure 21 — SOU Admin Dashboard (global officer-pipeline stats).</summary>
    [HttpGet("sou")]
    [Authorize(Roles = "SouStaff,SystemAdmin")]
    public async Task<IActionResult> GetSou(CancellationToken ct = default)
    {
        var result = await _souHandler.HandleAsync(ct);
        return Ok(result);
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }
}
