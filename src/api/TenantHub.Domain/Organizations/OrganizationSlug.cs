using System.Globalization;
using System.Text;

namespace TenantHub.Domain.Organizations;

/// <summary>
/// URL slug rules (SPEC 5.1): lowercase <c>a-z</c>, <c>0-9</c> and single hyphens, 3–40 characters, not a reserved
/// word. The slug is derived from the name once, at creation; collisions get <c>-2</c>, <c>-3</c>, … appended.
/// </summary>
public static class OrganizationSlug
{
    public const int MinLength = 3;
    public const int MaxLength = 40;

    /// <summary>Words the SPA or API use as path segments, so an organization can never shadow a route.</summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(["api", "app", "admin", "new", "settings", "invite", "orgs", "o"], StringComparer.Ordinal);

    public static bool IsValid(string slug) =>
        slug.Length is >= MinLength and <= MaxLength
        && !Reserved.Contains(slug)
        && slug[0] != '-'
        && slug[^1] != '-'
        && !slug.Contains("--", StringComparison.Ordinal)
        && slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>The slug to try first, then the numbered alternatives, all valid and at most <see cref="MaxLength"/> long.</summary>
    public static IEnumerable<string> Candidates(string organizationName, int count = 20)
    {
        var baseSlug = BaseFrom(organizationName);
        yield return baseSlug;

        for (var n = 2; n <= count; n++)
        {
            var suffix = $"-{n.ToString(CultureInfo.InvariantCulture)}";
            yield return baseSlug[..Math.Min(baseSlug.Length, MaxLength - suffix.Length)].TrimEnd('-') + suffix;
        }
    }

    internal static string BaseFrom(string organizationName)
    {
        // "Café Ünïon" → "cafe-union": strip accents, then turn every run of other characters into one hyphen.
        var decomposed = organizationName.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString();
        slug = slug[..Math.Min(slug.Length, MaxLength)].Trim('-');

        if (slug.Length == 0)
        {
            return "organization";
        }

        // Too short or reserved: make it a valid, still recognizable slug.
        return slug.Length < MinLength || Reserved.Contains(slug) ? $"{slug}-org" : slug;
    }
}
