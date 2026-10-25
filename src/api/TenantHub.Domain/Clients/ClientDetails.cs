namespace TenantHub.Domain.Clients;

/// <summary>
/// The editable fields of a client (SPEC 5.6), normalized: whitespace trimmed and collapsed in the name, empty
/// optional fields stored as null. <see cref="Validate"/> reports every invalid field at once, keyed by the API's
/// property names.
/// </summary>
public sealed record ClientDetails(string Name, string? ContactName, string? Email, string? Phone, string? Address, string? Notes)
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 120;
    public const int ContactNameMaxLength = 120;
    public const int EmailMaxLength = EmailAddress.MaxLength;
    public const int PhoneMaxLength = 40;
    public const int AddressMaxLength = 300;
    public const int NotesMaxLength = 2000;

    /// <summary>Human-readable field names for audit summaries.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["name"] = "name",
        ["contactName"] = "contact person",
        ["email"] = "email",
        ["phone"] = "phone",
        ["address"] = "address",
        ["notes"] = "notes",
    };

    public static ClientDetails Validate(string? name, string? contactName, string? email, string? phone, string? address, string? notes)
    {
        var normalized = new ClientDetails(
            string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            Optional(contactName),
            Optional(email),
            Optional(phone),
            Optional(address),
            Optional(notes, keepLineBreaks: true));

        var errors = new Dictionary<string, string[]>();
        void Check(bool valid, string field, string message)
        {
            if (!valid)
            {
                errors[field] = [message];
            }
        }

        Check(normalized.Name.Length is >= NameMinLength and <= NameMaxLength, "name", $"Enter a name of {NameMinLength}–{NameMaxLength} characters.");
        Check(normalized.ContactName is not { Length: > ContactNameMaxLength }, "contactName", $"Use at most {ContactNameMaxLength} characters.");
        Check(normalized.Email is null || EmailAddress.IsValid(normalized.Email), "email", "Enter a valid email address.");
        Check(normalized.Phone is not { Length: > PhoneMaxLength }, "phone", $"Use at most {PhoneMaxLength} characters.");
        Check(normalized.Address is not { Length: > AddressMaxLength }, "address", $"Use at most {AddressMaxLength} characters.");
        Check(normalized.Notes is not { Length: > NotesMaxLength }, "notes", $"Use at most {NotesMaxLength} characters.");

        return errors.Count == 0 ? normalized : throw new DomainValidationException(errors);
    }

    /// <summary>The fields that differ from <paramref name="other"/>, in a fixed order.</summary>
    internal IReadOnlyList<FieldChange> ChangesTo(ClientDetails other) =>
    [
        .. new (string Field, string? From, string? To)[]
        {
            ("name", Name, other.Name),
            ("contactName", ContactName, other.ContactName),
            ("email", Email, other.Email),
            ("phone", Phone, other.Phone),
            ("address", Address, other.Address),
            ("notes", Notes, other.Notes),
        }
        .Where(f => !string.Equals(f.From, f.To, StringComparison.Ordinal))
        .Select(f => new FieldChange(f.Field, f.From, f.To)),
    ];

    private static string? Optional(string? value, bool keepLineBreaks = false)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return keepLineBreaks ? trimmed : string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
