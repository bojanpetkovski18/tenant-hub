using System.Globalization;

namespace TenantHub.Domain.Jobs;

/// <summary>
/// The editable fields of a job (SPEC 5.4), normalized: whitespace trimmed, empty optional text stored as null.
/// <see cref="Validate"/> reports every invalid field at once, keyed by the API's property names.
/// </summary>
public sealed record JobDetails(
    string Title,
    string? Description,
    Guid ClientId,
    Guid? AssigneeUserId,
    JobPriority Priority,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    decimal? QuotedAmount)
{
    public const int TitleMinLength = 2;
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 5000;
    public const decimal MaxQuotedAmount = 1_000_000m;

    public static JobDetails Validate(
        string? title,
        string? description,
        Guid? clientId,
        Guid? assigneeUserId,
        JobPriority? priority,
        DateOnly? scheduledOn,
        DateOnly? dueOn,
        decimal? quotedAmount)
    {
        var normalizedTitle = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        var errors = new Dictionary<string, string[]>();
        void Check(bool valid, string field, string message)
        {
            if (!valid)
            {
                errors[field] = [message];
            }
        }

        Check(normalizedTitle.Length is >= TitleMinLength and <= TitleMaxLength, "title", $"Enter a title of {TitleMinLength}–{TitleMaxLength} characters.");
        Check(normalizedDescription is not { Length: > DescriptionMaxLength }, "description", $"Use at most {DescriptionMaxLength} characters.");
        Check(clientId is not null && clientId != Guid.Empty, "clientId", "Choose a client.");
        Check(scheduledOn is null || dueOn is null || dueOn >= scheduledOn, "dueOn", "The due date cannot be before the scheduled date.");
        Check(
            quotedAmount is null || (quotedAmount is >= 0 and <= MaxQuotedAmount && decimal.Round(quotedAmount.Value, 2) == quotedAmount),
            "quotedAmount",
            "Enter an amount from 0 to 1,000,000.00 with at most two decimals.");

        return errors.Count == 0
            ? new JobDetails(normalizedTitle, normalizedDescription, clientId!.Value, assigneeUserId, priority ?? JobPriority.Normal, scheduledOn, dueOn, quotedAmount)
            : throw new DomainValidationException(errors);
    }

    /// <summary>The fields that differ from <paramref name="other"/>, in a fixed order, with readable values.</summary>
    internal IReadOnlyList<FieldChange> ChangesTo(JobDetails other, IReadOnlyDictionary<Guid, string> names) =>
    [
        .. new (string Field, string? From, string? To)[]
        {
            ("title", Title, other.Title),
            ("description", Description, other.Description),
            ("client", Name(names, ClientId), Name(names, other.ClientId)),
            ("assignee", Name(names, AssigneeUserId), Name(names, other.AssigneeUserId)),
            ("priority", JobLabels.Of(Priority), JobLabels.Of(other.Priority)),
            ("scheduledOn", Date(ScheduledOn), Date(other.ScheduledOn)),
            ("dueOn", Date(DueOn), Date(other.DueOn)),
            ("quotedAmount", Money(QuotedAmount), Money(other.QuotedAmount)),
        }
        .Where(f => !string.Equals(f.From, f.To, StringComparison.Ordinal))
        .Select(f => new FieldChange(f.Field, f.From, f.To)),
    ];

    internal static string? Name(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
        id is { } value ? names.GetValueOrDefault(value, "unknown") : null;

    internal static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string? Money(decimal? amount) => amount?.ToString("0.00", CultureInfo.InvariantCulture);
}
