namespace TenantHub.Domain.Agreements;

public enum RecurrenceKind
{
    /// <summary>On chosen weekdays, every N weeks (N 1–8).</summary>
    Weekly,

    /// <summary>On day D of the month (1–28) or the last day, every N months (N 1–12).</summary>
    MonthlyByDay,

    /// <summary>On the first to fourth, or last, given weekday of the month, every N months (N 1–12).</summary>
    MonthlyByWeekday,
}

public enum WeekOfMonth
{
    First = 1,
    Second = 2,
    Third = 3,
    Fourth = 4,
    Last = 5,
}

/// <summary>
/// When an agreement's visits fall (SPEC-V2 5.1, D041): three patterns over dates in the organization's time zone,
/// not RFC 5545. Intervals count from the agreement's start: weeks from the Monday of the start's week, months from
/// the start's month. Only the fields of the kind are kept, and equal rules compare equal.
/// </summary>
public sealed record Recurrence
{
    public const int MaxWeeklyInterval = 8;
    public const int MaxMonthlyInterval = 12;
    public const int MaxDayOfMonth = 28;

    private Recurrence()
    {
    }

    public RecurrenceKind Kind { get; private init; }

    public int Interval { get; private init; }

    /// <summary>Weekly: the chosen weekdays, Monday first, no repeats.</summary>
    public IReadOnlyList<DayOfWeek> Weekdays { get; private init; } = [];

    /// <summary>Monthly by day: 1–28, or null with <see cref="LastDay"/>.</summary>
    public int? Day { get; private init; }

    public bool LastDay { get; private init; }

    /// <summary>Monthly by weekday: which one in the month.</summary>
    public WeekOfMonth? Week { get; private init; }

    public DayOfWeek? Weekday { get; private init; }

    public static Recurrence Weekly(IEnumerable<DayOfWeek> weekdays, int everyWeeks = 1) =>
        Validate(RecurrenceKind.Weekly, everyWeeks, [.. weekdays], null, false, null, null);

    public static Recurrence MonthlyOnDay(int day, int everyMonths = 1) =>
        Validate(RecurrenceKind.MonthlyByDay, everyMonths, [], day, false, null, null);

    public static Recurrence MonthlyOnLastDay(int everyMonths = 1) =>
        Validate(RecurrenceKind.MonthlyByDay, everyMonths, [], null, true, null, null);

    public static Recurrence MonthlyOnWeekday(WeekOfMonth week, DayOfWeek weekday, int everyMonths = 1) =>
        Validate(RecurrenceKind.MonthlyByWeekday, everyMonths, [], null, false, week, weekday);

    /// <summary>
    /// Every field as it arrives, checked together; errors are keyed <c>recurrence.*</c> for the form. Fields that do not
    /// belong to the kind are ignored.
    /// </summary>
    public static Recurrence Validate(
        RecurrenceKind? kind,
        int? interval,
        IReadOnlyCollection<DayOfWeek>? weekdays,
        int? day,
        bool lastDay,
        WeekOfMonth? week,
        DayOfWeek? weekday)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool valid, string field, string message)
        {
            if (!valid)
            {
                errors[$"recurrence.{field}"] = [message];
            }
        }

        if (kind is not { } k || !Enum.IsDefined(k))
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["recurrence.kind"] = ["Choose how often the visits repeat."] });
        }

        var max = k == RecurrenceKind.Weekly ? MaxWeeklyInterval : MaxMonthlyInterval;
        var unit = k == RecurrenceKind.Weekly ? "weeks" : "months";
        Check(interval is >= 1 && interval <= max, "interval", $"Repeat every 1–{max} {unit}.");

        var recurrence = new Recurrence { Kind = k, Interval = interval ?? 1 };
        switch (k)
        {
            case RecurrenceKind.Weekly:
                var days = (weekdays ?? []).Where(Enum.IsDefined).Distinct().OrderBy(MondayFirst).ToList();
                Check(days.Count > 0, "weekdays", "Choose at least one weekday.");
                recurrence = recurrence with { Weekdays = days };
                break;
            case RecurrenceKind.MonthlyByDay:
                Check(lastDay || day is >= 1 and <= MaxDayOfMonth, "day", $"Choose a day from 1 to {MaxDayOfMonth}, or the last day of the month.");
                recurrence = recurrence with { Day = lastDay ? null : day, LastDay = lastDay };
                break;
            default:
                Check(week is { } w && Enum.IsDefined(w), "week", "Choose first, second, third, fourth or last.");
                Check(weekday is { } d && Enum.IsDefined(d), "weekday", "Choose a weekday.");
                recurrence = recurrence with { Week = week, Weekday = weekday };
                break;
        }

        return errors.Count == 0 ? recurrence : throw new DomainValidationException(errors);
    }

    /// <summary>
    /// The dates in <c>[from, to]</c> on or after <paramref name="anchor"/>, in order. Pure: the same arguments always
    /// give the same dates.
    /// </summary>
    public IEnumerable<DateOnly> Occurrences(DateOnly anchor, DateOnly from, DateOnly to)
    {
        var start = from > anchor ? from : anchor;
        if (start > to)
        {
            yield break;
        }

        if (Kind == RecurrenceKind.Weekly)
        {
            var anchorWeek = MondayOf(anchor);
            for (var date = start; date <= to; date = date.AddDays(1))
            {
                var weeks = (MondayOf(date).DayNumber - anchorWeek.DayNumber) / 7;
                if (weeks % Interval == 0 && Weekdays.Contains(date.DayOfWeek))
                {
                    yield return date;
                }
            }

            yield break;
        }

        // Months counted from the anchor's month; the first month that can matter is the start's.
        var anchorMonth = MonthIndex(anchor);
        var offset = MonthIndex(start) - anchorMonth;
        var month = anchorMonth + offset + ((Interval - (offset % Interval)) % Interval);
        for (; ; month += Interval)
        {
            var date = DateIn(month / 12, (month % 12) + 1);
            if (date > to)
            {
                yield break;
            }

            if (date >= start)
            {
                yield return date;
            }
        }
    }

    // Value equality including the weekdays (a record compares the list by reference).
    public bool Equals(Recurrence? other) =>
        other is not null && Kind == other.Kind && Interval == other.Interval && Weekdays.SequenceEqual(other.Weekdays)
        && Day == other.Day && LastDay == other.LastDay && Week == other.Week && Weekday == other.Weekday;

    public override int GetHashCode() => HashCode.Combine(Kind, Interval, string.Join(',', Weekdays), Day, LastDay, Week, Weekday);

    /// <summary>A short description for lists and the audit: "Every 2 weeks on Mon, Thu", "Monthly on the last Friday".</summary>
    public string Describe()
    {
        var every = Interval == 1
            ? Kind == RecurrenceKind.Weekly ? "Weekly" : "Monthly"
            : Kind == RecurrenceKind.Weekly ? $"Every {Interval} weeks" : $"Every {Interval} months";
        return Kind switch
        {
            RecurrenceKind.Weekly => $"{every} on {string.Join(", ", Weekdays.Select(d => d.ToString()[..3]))}",
            RecurrenceKind.MonthlyByDay => LastDay ? $"{every} on the last day" : $"{every} on day {Day}",
            _ => $"{every} on the {Week!.Value.ToString().ToLowerInvariant()} {Weekday}",
        };
    }

    private DateOnly DateIn(int year, int month)
    {
        var days = DateTime.DaysInMonth(year, month);
        if (Kind == RecurrenceKind.MonthlyByDay)
        {
            return new DateOnly(year, month, LastDay ? days : Day!.Value);
        }

        if (Week == WeekOfMonth.Last)
        {
            var last = new DateOnly(year, month, days);
            return last.AddDays(-(((int)last.DayOfWeek - (int)Weekday!.Value + 7) % 7));
        }

        var first = new DateOnly(year, month, 1);
        return first.AddDays((((int)Weekday!.Value - (int)first.DayOfWeek + 7) % 7) + (7 * ((int)Week!.Value - 1)));
    }

    private static int MonthIndex(DateOnly date) => (date.Year * 12) + date.Month - 1;

    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-MondayFirst(date.DayOfWeek));

    private static int MondayFirst(DayOfWeek day) => ((int)day + 6) % 7;
}

/// <summary>
/// A recurrence with its start and optional end: an end date, or a number of visits counted from the start
/// (SPEC-V2 5.1). <see cref="Occurrences"/> is what generation asks.
/// </summary>
public sealed record Schedule(Recurrence Recurrence, DateOnly StartsOn, DateOnly? EndsOn, int? MaxVisits)
{
    public const int MaxVisitsLimit = 500;

    public static Schedule Validate(Recurrence recurrence, DateOnly? startsOn, DateOnly? endsOn, int? maxVisits)
    {
        var errors = new Dictionary<string, string[]>();
        if (startsOn is null)
        {
            errors["startsOn"] = ["Choose the date of the first visit."];
        }

        if (endsOn is not null && maxVisits is not null)
        {
            errors["endsOn"] = ["Choose an end date or a number of visits, not both."];
        }
        else if (endsOn is { } end && startsOn is { } start && end < start)
        {
            errors["endsOn"] = ["The end date cannot be before the start."];
        }

        if (maxVisits is not null and not (>= 1 and <= MaxVisitsLimit))
        {
            errors["maxVisits"] = [$"Choose 1–{MaxVisitsLimit} visits."];
        }

        return errors.Count == 0 ? new Schedule(recurrence, startsOn!.Value, endsOn, maxVisits) : throw new DomainValidationException(errors);
    }

    /// <summary>The visit dates in <c>[from, to]</c>, within the end date and the number of visits.</summary>
    public IEnumerable<DateOnly> Occurrences(DateOnly from, DateOnly to)
    {
        var last = EndsOn is { } end && end < to ? end : to;
        if (MaxVisits is not { } max)
        {
            return Recurrence.Occurrences(StartsOn, from, last);
        }

        // The count runs from the start, so earlier visits use up the number even when they are before "from".
        return Recurrence.Occurrences(StartsOn, StartsOn, last).Take(max).Where(d => d >= from);
    }

    /// <summary>The last visit date when the schedule has an end; null when it repeats forever.</summary>
    public DateOnly? LastOccurrence() => MaxVisits is { } max
        ? Recurrence.Occurrences(StartsOn, StartsOn, DateOnly.MaxValue).Take(max).Last()
        : EndsOn is { } end ? Recurrence.Occurrences(StartsOn, StartsOn, end).Cast<DateOnly?>().LastOrDefault() : null;
}
