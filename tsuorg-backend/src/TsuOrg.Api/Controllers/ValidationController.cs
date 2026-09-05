using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Documents.Commands;
using TsuOrg.Application.Features.Documents.Queries;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// CALSV orchestration endpoints bound to a document.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class ValidationController : ControllerBase
{
    private readonly GetValidationHandler _get;
    private readonly RerunValidationHandler _rerun;
    private readonly ICalsvClient _calsv;

    public ValidationController(
        GetValidationHandler get,
        RerunValidationHandler rerun,
        ICalsvClient calsv)
    {
        _get = get;
        _rerun = rerun;
        _calsv = calsv;
    }

    /// <summary>Latest CALSV result for a document (full fields JSON).</summary>
    [HttpGet("documents/{documentId:guid}/validation")]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken ct = default)
    {
        try
        {
            var result = await _get.HandleAsync(new GetValidationQuery(documentId), ct);
            return Ok(result);
        }
        catch (NotFoundException ex) { return NotFound(Problem(ex.Message)); }
    }

    /// <summary>Re-queue CALSV for SouStaff / Admin (or after flag fixes).</summary>
    [HttpPost("documents/{documentId:guid}/validation/re-run")]
    [Authorize(Roles = "SouStaff,SystemAdmin,OrgOfficer")]
    public async Task<IActionResult> Rerun(Guid documentId, CancellationToken ct = default)
    {
        try
        {
            var result = await _rerun.HandleAsync(
                new RerunValidationCommand(documentId, GetUserId()), ct);
            return Accepted(result);
        }
        catch (NotFoundException ex)     { return NotFound(Problem(ex.Message)); }
        catch (BusinessRuleException ex) { return UnprocessableEntity(Problem(ex.Message)); }
    }

    /// <summary>ML service readiness probe via backend.</summary>
    [HttpGet("validation/health")]
    [AllowAnonymous]
    public async Task<IActionResult> MlHealth(CancellationToken ct = default)
    {
        var ok = await _calsv.HealthAsync(ct);
        return ok
            ? Ok(new { status = "ok", service = "tsuorg-ml" })
            : StatusCode(503, Problem("ML service unreachable"));
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }
}
