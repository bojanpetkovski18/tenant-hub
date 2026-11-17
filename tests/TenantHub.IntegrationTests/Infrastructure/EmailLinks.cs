using TenantHub.Application.Abstractions;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>Reads the links out of captured emails, the way a person clicks them.</summary>
public static class EmailLinks
{
    public const string VerificationSubject = "Confirm your email address for TenantHub";
    public const string ResetSubject = "Reset your TenantHub password";

    /// <summary>The token of the link to the SPA page <paramref name="page"/> (<c>/page#token</c>) in the text body.</summary>
    public static string TokenFrom(EmailMessage message, string page)
    {
        var marker = $"/{page}#";
        var link = message.TextBody.Split(' ', '\n', '\r').Single(word => word.Contains(marker, StringComparison.Ordinal));
        return Uri.UnescapeDataString(link[(link.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
    }

    /// <summary>The latest email with this subject to <paramref name="to"/>, after the outbox has been delivered.</summary>
    public static async Task<EmailMessage> LatestAsync(ApiFactory factory, CapturedEmails emails, string to, string subject)
    {
        await factory.DispatchOutboxAsync();
        return emails.Sent.Last(m => m.To == to && m.Subject == subject);
    }

    /// <summary>Follows the verification email the sign-up queued, as its owner would.</summary>
    public static async Task VerifyAsync(ApiFactory factory, CapturedEmails emails, BrowserClient browser, string address)
    {
        var message = await LatestAsync(factory, emails, address, VerificationSubject);
        var response = await browser.PostJsonAsync("/api/auth/verify-email", new { token = TokenFrom(message, "verify-email") });
        response.EnsureSuccessStatusCode();
    }
}
