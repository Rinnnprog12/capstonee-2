using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Workflow.Commands;
using TsuOrg.Application.Features.Workflow.Queries;
using TsuOrg.Domain.Enums;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Workflow Management &amp; Approval (WM-01..03).
/// Sequential routing: Adviser → Dean → SOU with Approve / Reject / Return.
/// </summary>
[ApiController]
[Route("api/v1/workflow")]
[Authorize]
public sealed class WorkflowController : ControllerBase
{
    private readonly WorkflowDecisionHandler _decide;
    private readonly GetReviewQueueHandler _queue;
    private readonly GetWorkflowHistoryHandler _history;
    private readonly GetMyReviewHistoryHandler _myHistory;
    private readonly GetWorkflowMonitorHandler _monitor;
    private readonly GetSystemAuditLogHandler _systemAudit;
    private readonly GetScopedAuditLogHandler _scopedAudit;

    public WorkflowController(
        WorkflowDecisionHandler decide,
        GetReviewQueueHandler queue,
        GetWorkflowHistoryHandler history,
        GetMyReviewHistoryHandler myHistory,
        GetWorkflowMonitorHandler monitor,
        GetSystemAuditLogHandler systemAudit,
        GetScopedAuditLogHandler scopedAudit)
    {
        _decide = decide;
        _queue = queue;
        _history = history;
        _myHistory = myHistory;
        _monitor = monitor;
        _systemAudit = systemAudit;
        _scopedAudit = scopedAudit;
    }

    /// <summary>Figure 15 — Adviser / Dean review inbox (also available to SOU/Admin).</summary>
    [HttpGet("queue")]
    [Authorize(Roles = "Adviser,Dean,SouStaff,SystemAdmin")]
    public async Task<IActionResult> Queue(
        [FromQuery] ReviewQueueTab tab = ReviewQueueTab.All,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _queue.HandleAsync(new GetReviewQueueQuery(
                GetUserId(), GetAppRole(), tab, page, pageSize), ct);
            return Ok(result);
        }
        catch (ForbiddenException) { return Forbid(); }
    }

    /// <summary>Figure 22: SOU workflow monitor — every active submission in the pipeline.</summary>
    [HttpGet("monitor")]
    [Authorize(Roles = "SouStaff,SystemAdmin")]
    public async Task<IActionResult> Monitor(
        [FromQuery] WorkflowMonitorTab tab = WorkflowMonitorTab.AllActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _monitor.HandleAsync(new GetWorkflowMonitorQuery(tab, page, pageSize), ct);
        return Ok(result);
    }

    /// <summary>Figure 28: system-wide audit log — every submission and decision, with actor.</summary>
    [HttpGet("audit")]
    [Authorize(Roles = "SouStaff,SystemAdmin")]
    public async Task<IActionResult> SystemAudit(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _systemAudit.HandleAsync(new GetSystemAuditLogQuery(page, pageSize), ct);
        return Ok(result);
    }

    /// <summary>Figure 20: Adviser / Dean audit log — TrackingHistories for org/college-scoped docs.</summary>
    [HttpGet("audit/scoped")]
    [Authorize(Roles = "Adviser,Dean")]
    public async Task<IActionResult> ScopedAudit(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _scopedAudit.HandleAsync(new GetScopedAuditLogQuery(
                GetUserId(),
                User.FindFirstValue(ClaimTypes.Role) ?? "OrgOfficer",
                page,
                pageSize), ct);
            return Ok(result);
        }
        catch (ForbiddenException) { return Forbid(); }
    }

    /// <summary>Figures 19: the caller's own past review decisions.</summary>
    [HttpGet("my-history")]
    [Authorize(Roles = "Adviser,Dean,SouStaff,SystemAdmin")]
    public async Task<IActionResult> MyHistory(
        [FromQuery] ReviewHistoryTab tab = ReviewHistoryTab.All,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var result = await _myHistory.HandleAsync(new GetMyReviewHistoryQuery(
            GetUserId(), tab, page, pageSize), ct);
        return Ok(result);
    }

    [HttpGet("{documentId:guid}/history")]
    public async Task<IActionResult> History(Guid documentId, CancellationToken ct = default)
    {
        try
        {
            var result = await _history.HandleAsync(new GetWorkflowHistoryQuery(documentId), ct);
            return Ok(result);
        }
        catch (NotFoundException ex) { return NotFound(Problem(ex.Message)); }
    }

    /// <summary>WM-02 / WM-03: Approve with required electronic signature.</summary>
    [HttpPost("{documentId:guid}/approve")]
    [Authorize(Roles = "Adviser,Dean,SouStaff,SystemAdmin")]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> Approve(
        Guid documentId,
        [FromForm] string? comments,
        IFormFile signature,
        CancellationToken ct = default)
    {
        if (signature is null || signature.Length == 0)
            return BadRequest(Problem("Signature file is required for Approve (WM-03)."));

        return await DecideAsync(documentId, ApprovalAction.Approve, comments, signature, ct);
    }

    [HttpPost("{documentId:guid}/reject")]
    [Authorize(Roles = "Adviser,Dean,SouStaff,SystemAdmin")]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> Reject(
        Guid documentId,
        [FromForm] string comments,
        IFormFile? signature,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(comments))
            return BadRequest(Problem("Comments are required when rejecting."));

        return await DecideAsync(documentId, ApprovalAction.Reject, comments, signature, ct);
    }

    [HttpPost("{documentId:guid}/return")]
    [Authorize(Roles = "Adviser,Dean,SouStaff,SystemAdmin")]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> Return(
        Guid documentId,
        [FromForm] string comments,
        IFormFile? signature,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(comments))
            return BadRequest(Problem("Comments are required when returning for revision."));

        return await DecideAsync(documentId, ApprovalAction.Return, comments, signature, ct);
    }

    private async Task<IActionResult> DecideAsync(
        Guid documentId,
        ApprovalAction action,
        string? comments,
        IFormFile? signature,
        CancellationToken ct)
    {
        try
        {
            Stream? sigStream = null;
            string? sigName = null;
            string? sigCt = null;

            if (signature is not null && signature.Length > 0)
            {
                sigStream = signature.OpenReadStream();
                sigName = signature.FileName;
                sigCt = signature.ContentType;
            }

            await using var _ = sigStream as IAsyncDisposable;
            var result = await _decide.HandleAsync(new WorkflowDecisionCommand(
                documentId,
                GetUserId(),
                GetAppRole(),
                action,
                comments,
                sigStream,
                sigName,
                sigCt), ct);

            return Ok(result);
        }
        catch (NotFoundException ex) { return NotFound(Problem(ex.Message)); }
        catch (BusinessRuleException ex) { return UnprocessableEntity(Problem(ex.Message)); }
        catch (ForbiddenException) { return Forbid(); }
        catch (ValidationException vex) { return BadRequest(Problem(vex.Message)); }
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private AppRole GetAppRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "OrgOfficer";
        return Enum.TryParse<AppRole>(role, ignoreCase: true, out var parsed)
            ? parsed
            : AppRole.OrgOfficer;
    }
}
