using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace TsuOrg.Infrastructure.Services;

/// <summary>
/// Architecture box 9 – Notification Module: email delivery via MailKit.
/// Configuration keys: Email:SmtpHost, Email:SmtpPort, Email:UseSsl,
///   Email:Username, Email:Password, Email:FromAddress, Email:FromName.
/// Set Email:Enabled=false to disable without removing the service registration.
/// </summary>
public sealed class EmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsEnabled =>
        bool.TryParse(_config["Email:Enabled"], out var v) && v;

    public async Task SendAsync(
        string toAddress,
        string toName,
        string subject,
        string htmlBody,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var host     = _config["Email:SmtpHost"] ?? "localhost";
        var port     = int.TryParse(_config["Email:SmtpPort"], out var p) ? p : 587;
        var useSsl   = bool.TryParse(_config["Email:UseSsl"], out var s) && s;
        var username = _config["Email:Username"];
        var password = _config["Email:Password"];
        var fromAddr = _config["Email:FromAddress"] ?? "noreply@tsuorgdocx.edu.ph";
        var fromName = _config["Email:FromName"]    ?? "TSU-ORGDOCX";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddr));
        message.To.Add(new MailboxAddress(toName, toAddress));
        message.Subject = subject;
        message.Body = new TextPart(MimeKit.Text.TextFormat.Html) { Text = htmlBody };

        try
        {
            using var client = new SmtpClient();
            var secureSocketOptions = useSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(host, port, secureSocketOptions, ct);

            if (!string.IsNullOrWhiteSpace(username))
                await client.AuthenticateAsync(username, password ?? string.Empty, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _logger.LogInformation("Email sent to {To} — {Subject}", toAddress, subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email delivery failed for {To} — {Subject}", toAddress, subject);
        }
    }
}
