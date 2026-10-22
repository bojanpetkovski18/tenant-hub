using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Email;

/// <summary>
/// SMTP through <c>System.Net.Mail</c> (D011): enough for one relay and plain messages, without a dependency.
/// Failures are logged with the recipient's domain only; the message is never logged, since it carries the link.
/// </summary>
internal sealed partial class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public bool IsEnabled => true;

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(options.From, options.FromName),
            Subject = message.Subject,
            Body = message.TextBody,
            IsBodyHtml = false,
        };
        mail.To.Add(message.To);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, null, MediaTypeNames.Text.Html));

        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            EnableSsl = options.SmtpUseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(options.SmtpUserName))
        {
            client.Credentials = new NetworkCredential(options.SmtpUserName, options.SmtpPassword);
        }

        try
        {
            await client.SendMailAsync(mail, cancellationToken);
            return true;
        }
        catch (SmtpException exception)
        {
            LogFailed(logger, Domain(message.To), exception.StatusCode);
            return false;
        }
    }

    private static string Domain(string address) => address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email to a recipient at {Domain} failed ({Status}); the inviter gets the link instead.")]
    private static partial void LogFailed(ILogger logger, string domain, SmtpStatusCode status);
}

internal sealed class DisabledEmailSender : IEmailSender
{
    public bool IsEnabled => false;

    public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed class AppLinks(AppOptions options) : IAppLinks
{
    // A trailing slash keeps a path prefix (https://host/app/) when the relative link is resolved against it.
    private readonly Uri _baseUrl = new(options.PublicUrl!.ToString().TrimEnd('/') + "/");

    // The token goes in the fragment, which browsers never send: it stays out of server, proxy and access logs and
    // out of Referer headers (D028).
    public string Invitation(string token) => new Uri(_baseUrl, $"invite#{Uri.EscapeDataString(token)}").ToString();
}
