namespace TenantHub.Infrastructure.Email;

public enum EmailMode
{
    /// <summary>No email leaves the API; inviters get the link to pass on (the public demo, and tests).</summary>
    Disabled,
    Smtp,
}

/// <summary>Configuration section <c>Email</c> (D011). Unset means disabled, so nothing is ever sent by accident.</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public EmailMode Mode { get; init; } = EmailMode.Disabled;

    public string SmtpHost { get; init; } = "localhost";

    public int SmtpPort { get; init; } = 25;

    /// <summary>Off for Mailpit on the loopback interface; a real relay needs TLS and credentials (M11).</summary>
    public bool SmtpUseTls { get; init; }

    public string? SmtpUserName { get; init; }

    public string? SmtpPassword { get; init; }

    public string From { get; init; } = "no-reply@tenanthub.example";

    public string FromName { get; init; } = "TenantHub";
}

/// <summary>Configuration section <c>App</c>.</summary>
public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>
    /// Where the SPA is reached, for links in emails. Required, and never taken from the request's Host header,
    /// which a client controls (a forged Host would otherwise put an attacker's domain in invitation emails).
    /// </summary>
    public Uri? PublicUrl { get; init; }
}
