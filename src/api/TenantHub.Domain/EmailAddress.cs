using System.Text.RegularExpressions;

namespace TenantHub.Domain;

/// <summary>The one email rule used everywhere in the domain: deliberately simple, deliverability is not checked.</summary>
public static partial class EmailAddress
{
    public const int MaxLength = 254;

    public static bool IsValid(string email) => email.Length <= MaxLength && Pattern().IsMatch(email);

    /// <summary>For comparisons: invitations match accounts case-insensitively (SPEC 5.3).</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Pattern();
}
