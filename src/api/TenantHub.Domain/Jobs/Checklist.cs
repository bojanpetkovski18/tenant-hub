namespace TenantHub.Domain.Jobs;

/// <summary>
/// One item of a job's checklist (SPEC-V2 5.6). Part of the <see cref="Job"/> aggregate: the job decides who may change
/// it and when, and the Completed precondition reads it. Ticking records who and when; unticking clears both.
/// </summary>
public sealed class ChecklistItem : ITenantOwned
{
    private ChecklistItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid JobId { get; private set; }

    /// <summary>0-based and dense within the job; the job keeps it so when items move or go.</summary>
    public int Position { get; internal set; }

    public string Text { get; private set; } = "";

    public bool Required { get; private set; }

    public DateTimeOffset? DoneAt { get; private set; }

    public Guid? DoneByUserId { get; private set; }

    public bool IsDone => DoneAt is not null;

    internal static ChecklistItem Create(Job job, int position, ChecklistEntry entry, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OrganizationId = job.OrganizationId,
        JobId = job.Id,
        Position = position,
        Text = entry.Text,
        Required = entry.Required,
    };

    internal void Apply(ChecklistEntry entry) => (Text, Required) = (entry.Text, entry.Required);

    internal void Tick(bool done, Guid userId, DateTimeOffset now)
    {
        DoneAt = done ? now : null;
        DoneByUserId = done ? userId : null;
    }
}

/// <summary>An item's text and whether it must be done, validated; also an agreement template's line (SPEC-V2 5.6).</summary>
public sealed record ChecklistEntry(string Text, bool Required)
{
    public const int TextMaxLength = 200;

    public static ChecklistEntry Validate(string? text, bool? required, string field = "text")
    {
        var normalized = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is 0 or > TextMaxLength)
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { [field] = [$"Give the item 1–{TextMaxLength} characters."] });
        }

        return new ChecklistEntry(normalized, required ?? false);
    }

    /// <summary>A whole list, for an agreement's template: at most <see cref="Checklists.MaxItems"/> valid lines.</summary>
    public static IReadOnlyList<ChecklistEntry> ValidateList(IReadOnlyList<(string? Text, bool? Required)>? lines, string field = "checklist")
    {
        if (lines is null || lines.Count == 0)
        {
            return [];
        }

        if (lines.Count > Checklists.MaxItems)
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { [field] = [$"A checklist has at most {Checklists.MaxItems} items."] });
        }

        return [.. lines.Select((line, index) => Validate(line.Text, line.Required, $"{field}[{index}].text"))];
    }
}

/// <summary>What a request may change on an item; null leaves that part as it is.</summary>
public sealed record ChecklistChange(string? Text, bool? Required, bool? Done, int? Position);

/// <summary>The checklist and photo rules as numbers and words (SPEC-V2 5.6).</summary>
public static class Checklists
{
    public const int MaxItems = 30;

    public const int MaxPhotos = 10;

    public static string Describe(int items) => items == 1 ? "1 item" : $"{items} items";

    /// <summary>Why work on a job with these items cannot be completed yet, or null when every required item is done.</summary>
    public static string? WhyNotComplete(IEnumerable<ChecklistItem> items)
    {
        var open = items.Where(i => i.Required && !i.IsDone).OrderBy(i => i.Position).Select(i => i.Text).ToList();
        return open.Count == 0 ? null : $"Tick the required checklist items first: {string.Join("; ", open)}.";
    }
}

/// <summary>Completing with a required item still open (409 <c>checklist-incomplete</c>); carries the open items.</summary>
public sealed class ChecklistIncompleteException(string message, IReadOnlyList<ChecklistItem> open)
    : DomainException("checklist-incomplete", message)
{
    public IReadOnlyList<ChecklistItem> Open { get; } = open;
}
