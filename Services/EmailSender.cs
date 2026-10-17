using System.Net;
using System.Net.Mail;

namespace DoForYou.API.Services;

public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>SMTP delivery configured exclusively through environment-backed settings.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    public SmtpEmailSender(IConfiguration configuration) => _configuration = configuration;

    public async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
    {
        var host = Environment.GetEnvironmentVariable("SMTP_HOST") ?? _configuration["Email:SmtpHost"];
        var portText = Environment.GetEnvironmentVariable("SMTP_PORT") ?? _configuration["Email:SmtpPort"];
        var username = Environment.GetEnvironmentVariable("SMTP_USERNAME") ?? _configuration["Email:SmtpUsername"];
        var password = Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? _configuration["Email:SmtpPassword"];
        var from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? _configuration["Email:From"];
        var sslText = Environment.GetEnvironmentVariable("SMTP_ENABLE_SSL") ?? _configuration["Email:EnableSsl"];

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("SMTP is not configured. Set SMTP_HOST, SMTP_USERNAME, SMTP_PASSWORD and SMTP_FROM.");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("SMTP_PORT must be a valid TCP port.");

        using var message = new MailMessage
        {
            From = new MailAddress(from),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(recipient));

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = !string.Equals(sslText, "false", StringComparison.OrdinalIgnoreCase),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(username, password)
        };
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message);
    }
}
