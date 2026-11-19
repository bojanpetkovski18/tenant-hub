using System.Text.Encodings.Web;
using TenantHub.Application.Abstractions;

namespace TenantHub.Application.Portal;

/// <summary>
/// The portal's emails (SPEC-V2 4.4). The invitation carries no secret, only the portal's address; the sign-in link
/// signs the contact in, so it is a secret outbox message: encrypted at rest and deleted once sent (D039).
/// </summary>
public static class PortalEmails
{
    public static readonly OutboxMessageType Invitation = new("email.portal-invitation", CarriesSecret: false);

    public static readonly OutboxMessageType SignIn = new("email.portal-sign-in", CarriesSecret: true);

    public static EmailMessage BuildInvitation(string to, string contactName, string organizationName, string clientName, string portalLink) => Build(
        to,
        subject: $"{organizationName} gave you access to its client portal",
        greeting: contactName,
        lines:
        [
            $"{organizationName} added you as a contact for {clientName}. In its client portal you can see your visits, approve quotes and request work.",
            "There is no password: enter your email address on the portal page and we will send you a link that signs you in.",
        ],
        action: "Open the portal",
        portalLink,
        footer: $"If you did not expect this, you can ignore this email, or tell {organizationName}.");

    public static EmailMessage BuildSignIn(string to, string contactName, string organizationName, string link) => Build(
        to,
        subject: $"Your sign-in link for {organizationName}",
        greeting: contactName,
        lines: [$"Use this link to sign in to the client portal of {organizationName}."],
        action: "Sign in",
        link,
        footer: "The link works once and expires in 15 minutes. If you did not ask for it, ignore this email; nobody can sign in without it.");

    private static EmailMessage Build(string to, string subject, string greeting, string[] lines, string action, string link, string footer)
    {
        var text = $"""
            Hello {greeting},

            {string.Join("\n\n", lines)}

            {action}: {link}

            {footer}
            """;

        var html = HtmlEncoder.Default;
        var paragraphs = string.Concat(lines.Select(line => $"<p>{html.Encode(line)}</p>"));
        var body = $"""
            <!doctype html>
            <html lang="en">
            <body style="font-family: system-ui, sans-serif; line-height: 1.5; color: #0f172a;">
                <p>Hello {html.Encode(greeting)},</p>
                {paragraphs}
                <p><a href="{html.Encode(link)}">{html.Encode(action)}</a></p>
                <p>{html.Encode(footer)}</p>
            </body>
            </html>
            """;

        return new EmailMessage(to, subject, text, body);
    }
}
