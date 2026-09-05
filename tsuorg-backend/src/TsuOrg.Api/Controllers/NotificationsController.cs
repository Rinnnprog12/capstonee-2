using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;

namespace TsuOrg.Api.Controllers;

/// <summary>
/// Poll-based real-time status notifications for 1.3.4 and 1.4.1.
/// Frontend calls GET /notifications every ~10 s to display toast/badge.
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public sealed class NotificationsController : ControllerBase
{
    private readonly IApplicationDbContext _db;

    public NotificationsController(IApplicationDbContext db) => _db = db;

    /// <summary>Unread notifications for the current user (latest 20).</summary>
    [HttpGet]
    public async Task<IActionResult> ListUnread(CancellationToken ct = default)
    {
        var userId = GetUserId();

        var items = await _db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .OrderByDescending(n => n.CreatedAt)
            .Take(20)
            .Select(n => new
            {
                n.Id,
                n.Title,
                n.Message,
                n.RelatedDocumentId,
                n.CreatedAt,
            })
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(new { unreadCount = items.Count, items });
    }

    /// <summary>Mark a single notification as read.</summary>
    [HttpPatch("{id:guid}/read")]
    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var notif  = await _db.Notifications.FirstOrDefaultAsync(
            n => n.Id == id && n.UserId == userId, ct);

        if (notif is null) return NotFound();

        notif.IsRead = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Mark all notifications for the current user as read.</summary>
    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct = default)
    {
        var userId = GetUserId();

        await _db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);

        return NoContent();
    }

    private Guid GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }
}
