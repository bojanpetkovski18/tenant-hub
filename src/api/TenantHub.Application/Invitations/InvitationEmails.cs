using System.Text.Encodings.Web;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Invitations;

/// <summary>The invitation email: short, plain text plus simple accessible HTML, with every value HTML-encoded.</summary>
internal static class InvitationEmails
{
    public static EmailMessage Build(string to, string inviterName, string organizationName, Role role, string link, DateTimeOffset expiresAt)
    {
        var subject = $"{inviterName} invited you to {organizationName} on TenantHub";
        var expires = expiresAt.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var text = $"""
            {inviterName} invited you to join {organizationName} on TenantHub as {role}.

            Accept the invitation: {link}

            The link works once and expires on {expires} (UTC). If you were not expecting this, you can ignore it.
            """;

        var html = HtmlEncoder.Default;
        var body = $"""
            <!doctype html>
            <html lang="en">
            <body style="font-family: system-ui, sans-serif; line-height: 1.5; color: #0f172a;">
                <p>{html.Encode(inviterName)} invited you to join <strong>{html.Encode(organizationName)}</strong> on TenantHub as {html.Encode(role.ToString())}.</p>
                <p><a href="{html.Encode(link)}">Accept the invitation</a></p>
                <p>The link works once and expires on {html.Encode(expires)} (UTC). If you were not expecting this, you can ignore it.</p>
            </body>
            </html>
            """;

        return new EmailMessage(to, subject, text, body);
    }
}
