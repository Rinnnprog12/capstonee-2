using Microsoft.EntityFrameworkCore;
using TsuOrg.Application.Common;
using TsuOrg.Application.Features.Settings;
using TsuOrg.Domain.Entities;
using TsuOrg.Infrastructure.Persistence;

namespace TsuOrg.Infrastructure.Services;

/// <summary>
/// Architecture box 9 — Notification Module.
/// Persists in-app notification AND fires email via MailKit when enabled in System Settings.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly TsuOrgDbContext _db;
    private readonly EmailService _email;
    private readonly GetSystemSettingsHandler _settings;

    public NotificationService(TsuOrgDbContext db, EmailService email, GetSystemSettingsHandler settings)
    {
        _db = db;
        _email = email;
        _settings = settings;
    }

    public async Task SendAsync(
        Guid userId,
        string title,
        string message,
        Guid? relatedDocumentId = null,
        CancellationToken ct = default)
    {
        var notif = new Notification
        {
            UserId            = userId,
            Title             = title,
            Message           = message,
            IsRead            = false,
            RelatedDocumentId = relatedDocumentId,
        };
        _db.Notifications.Add(notif);
        await _db.SaveChangesAsync(ct);

        var settings = await _settings.HandleAsync(ct);
        if (!_email.IsEnabled || !settings.EmailNotifications)
            return;

        var user = await _db.UserAccounts.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user?.Email is not { Length: > 0 } email)
            return;

        var html = BuildEmailHtml(title, message, relatedDocumentId);
        _ = _email.SendAsync(email, user.FullName, $"[TSU-ORGDOCX] {title}", html, ct)
            .ContinueWith(_ => { }, TaskContinuationOptions.None);
    }

    private static string BuildEmailHtml(string title, string message, Guid? documentId)
    {
        var trackLink = documentId.HasValue
            ? $"<p><a href='https://tsuorgdocx.edu.ph/officer/tracker/{documentId}' style='color:#0d9488;'>View document status →</a></p>"
            : "";

        return $"""
            <html>
            <body style="font-family:sans-serif;color:#1e293b;max-width:520px;margin:0 auto;padding:24px">
              <div style="background:#0d9488;color:#fff;padding:14px 24px;border-radius:8px 8px 0 0">
                <strong>TSU-ORGDOCX</strong>
              </div>
              <div style="border:1px solid #e2e8f0;border-top:none;border-radius:0 0 8px 8px;padding:24px">
                <h2 style="margin:0 0 8px">{System.Security.SecurityElement.Escape(title)}</h2>
                <p style="color:#475569">{System.Security.SecurityElement.Escape(message)}</p>
                {trackLink}
                <hr style="border:none;border-top:1px solid #e2e8f0;margin:20px 0"/>
                <p style="font-size:12px;color:#94a3b8">
                  Tarlac State University — Student Organization Document Exchange System<br/>
                  Do not reply to this email.
                </p>
              </div>
            </body>
            </html>
            """;
    }
}
