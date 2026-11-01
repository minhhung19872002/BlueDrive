using BlueDrive.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BlueDrive.Infrastructure.Email;

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
    public bool UseSsl { get; set; } = true;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

/// <summary>Sends mail via SMTP (MailKit). When SMTP is not configured the message is only logged (subject, recipient).</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.IsConfigured)
        {
            logger.LogWarning("Chưa cấu hình SMTP - bỏ qua gửi email \"{Subject}\" tới {To}", subject, to);
            return;
        }
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(o.From));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        var socket = o.Port == 465 ? SecureSocketOptions.SslOnConnect : o.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(o.Host, o.Port, socket, ct);
        if (!string.IsNullOrEmpty(o.User)) await client.AuthenticateAsync(o.User, o.Password, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("Đã gửi email \"{Subject}\" tới {To}", subject, to);
    }
}
