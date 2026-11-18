using System.Text.Encodings.Web;
using TenantHub.Application.Abstractions;

namespace TenantHub.Application.Accounts;

/// <summary>
/// The account emails (SPEC-V2 4.1). Both carry a link that acts for the account, so both are secret outbox
/// messages: encrypted at rest and deleted once sent (D039).
/// </summary>
public static class AccountEmails
{
    public static readonly OutboxMessageType PasswordReset = new("email.password-reset", CarriesSecret: true, PerOrganization: false);

    public static readonly OutboxMessageType Verification = new("email.verification", CarriesSecret: true, PerOrganization: false);

    public static EmailMessage BuildPasswordReset(string to, string displayName, string link) => Build(
        to,
        subject: "Reset your TenantHub password",
        greeting: displayName,
        lines: ["Someone asked to reset the password of your TenantHub account. If it was you, choose a new password:"],
        action: "Choose a new password",
        link,
        footer: "The link works once and expires in 30 minutes. Choosing a new password signs you out everywhere. " +
            "If you did not ask for this, ignore this email; your password stays the same.");

    public static EmailMessage BuildVerification(string to, string displayName, string link) => Build(
        to,
        subject: "Confirm your email address for TenantHub",
        greeting: displayName,
        lines: ["Please confirm that this is your email address. Until you do, you cannot invite people to your organization."],
        action: "Confirm my email address",
        link,
        footer: "The link expires in 7 days; you can ask for a new one from the banner in the app. " +
            "If you did not create a TenantHub account, ignore this email.");

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
