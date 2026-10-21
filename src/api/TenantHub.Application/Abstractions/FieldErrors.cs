namespace TenantHub.Application.Abstractions;

/// <summary>Validation messages per request field (JSON property name); the API returns them as a validation problem.</summary>
public sealed class FieldErrors
{
    public static readonly FieldErrors None = new(new Dictionary<string, string[]>());

    private FieldErrors(IReadOnlyDictionary<string, string[]> errors) => Errors = errors;

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public bool IsEmpty => Errors.Count == 0;

    public static FieldErrors For(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });

    public static FieldErrors From(IEnumerable<(string Field, string Message)> errors) =>
        new(errors
            .GroupBy(e => e.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).Distinct().ToArray(), StringComparer.Ordinal));
}

/// <summary>Thrown by handlers for input the server refuses; the API maps it to a 400 validation problem.</summary>
public sealed class RequestValidationException(FieldErrors errors)
    : Exception("The request has invalid fields.")
{
    public FieldErrors Errors { get; } = errors;
}
