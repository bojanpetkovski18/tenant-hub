using System.Text.Encodings.Web;
using TenantHub.Application.Abstractions;

namespace TenantHub.Application.Notifications;

/// <summary>
/// Notification emails (SPEC-V2 5.7), plain text with a simple HTML twin, in the style of the invitation email. They
/// carry no secret (only links into the app, which needs a sign-in) and no tracking: no pixels, no redirecting links.
/// </summary>
public static class NotificationEmails
{
    public static readonly OutboxMessageType Message = new("email.notification", CarriesSecret: false);

    /// <summary>One notification: what happened, where to look, and where to turn such emails off.</summary>
    public static EmailMessage Build(string to, string name, string organizationName, string text, string action, string link, string preferencesLink) => Build(
        to,
        subject: $"{organizationName}: {Shorten(text, 120)}",
        name,
        organizationName,
        [text],
        action,
        link,
        preferencesLink);

    public static EmailMessage Build(
        string to,
        string subject,
        string name,
        string organizationName,
        IReadOnlyList<string> lines,
        string action,
        string link,
        string preferencesLink)
    {
        var footer = $"You get this email because of your notification settings in {organizationName}.";
        var text = $"""
            Hello {name},

            {string.Join("\n", lines)}

            {action}: {link}

            {footer} Change them here: {preferencesLink}
            """;

        var html = HtmlEncoder.Default;
        var items = lines.Count == 1
            ? $"<p>{html.Encode(lines[0])}</p>"
            : $"<p>{html.Encode(lines[0])}</p><ul>{string.Concat(lines.Skip(1).Select(l => $"<li>{html.Encode(l)}</li>"))}</ul>";
        var body = $"""
            <!doctype html>
            <html lang="en">
            <body style="font-family: system-ui, sans-serif; line-height: 1.5; color: #0f172a;">
                <p>Hello {html.Encode(name)},</p>
                {items}
                <p><a href="{html.Encode(link)}">{html.Encode(action)}</a></p>
                <p style="color: #475569; font-size: 0.875rem;">{html.Encode(footer)} <a href="{html.Encode(preferencesLink)}">Change them</a>.</p>
            </body>
            </html>
            """;

        return new EmailMessage(to, subject, text, body);
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
