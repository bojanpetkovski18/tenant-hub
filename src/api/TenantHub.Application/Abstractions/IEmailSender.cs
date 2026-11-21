namespace TenantHub.Application.Abstractions;

/// <summary>An email to one recipient. <c>MessageId</c> (an RFC 5322 Message-ID) is set when a repeat must be recognizable.</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody, string? MessageId = null);

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

    string PasswordReset(string token);

    string VerifyEmail(string token);

    /// <summary>The organization's portal, where a contact asks for a sign-in link (SPEC-V2 4.4).</summary>
    string Portal(string organizationSlug);

    /// <summary>The one-time sign-in link; the token goes in the fragment (D028).</summary>
    string PortalSignIn(string organizationSlug, string token);

    /// <summary>A page of the app ("o/brightline/jobs/…"), for links in notification emails.</summary>
    string App(string path);
}
