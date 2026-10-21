namespace TenantHub.Domain;

/// <summary>
/// A business rule was broken. <see cref="Code"/> is the stable, kebab-case identifier the API returns as the
/// problem-details <c>code</c> (SPEC section 7), so clients can react to it without parsing the message.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        if (!IsValidCode(code))
        {
            throw new ArgumentException($"'{code}' is not a kebab-case error code.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }

    internal static bool IsValidCode(string code) =>
        code.Length is > 0 and <= 64
        && code[0] is >= 'a' and <= 'z'
        && code[^1] != '-'
        && !code.Contains("--", StringComparison.Ordinal)
        && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
