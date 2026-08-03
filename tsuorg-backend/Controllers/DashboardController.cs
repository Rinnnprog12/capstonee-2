using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Features.Tracking.Queries;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Dashboard aggregated data for 1.4.3 — pending/processed counts per role.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly GetDashboardHandler _handler;

    public DashboardController(GetDashboardHandler handler) => _handler = handler;

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

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }
}
