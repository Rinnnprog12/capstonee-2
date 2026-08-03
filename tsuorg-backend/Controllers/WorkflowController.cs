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

    public WorkflowController(
        WorkflowDecisionHandler decide,
        GetReviewQueueHandler queue,
        GetWorkflowHistoryHandler history)
    {
        _decide = decide;
        _queue = queue;
        _history = history;
    }

    /// <summary>Role-scoped review queue (Adviser / Dean / SOU).</summary>
    [HttpGet("queue")]
    [Authorize(Roles = "Adviser,Dean,SouStaff,SystemAdmin")]
    public async Task<IActionResult> Queue(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _queue.HandleAsync(new GetReviewQueueQuery(
                GetUserId(), GetAppRole(), page, pageSize), ct);
            return Ok(result);
        }
        catch (ForbiddenException) { return Forbid(); }
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
