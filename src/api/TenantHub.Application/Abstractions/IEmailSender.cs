namespace TenantHub.Application.Abstractions;

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

/// <summary>
/// Outgoing email (D011). SMTP in development (Mailpit) and wherever configured; disabled on the public demo,
/// where the inviter gets the link to copy instead, so the demo never emails real people.
/// </summary>
public interface IEmailSender
{
    bool IsEnabled { get; }

    /// <summary>False when the server refused the message or could not be reached; the sender logs why, never the body.</summary>
    Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Absolute links into the SPA, built from configuration, never from the request's Host header.</summary>
public interface IAppLinks
{
    string Invitation(string token);
}
