using System.Globalization;
using TenantHub.Domain.Agreements;

namespace TenantHub.Domain.Tests.Agreements;

/// <summary>
/// SPEC-V2 5.1 and criterion 14: each recurrence over a full year, month ends, 29 February 2028, intervals across a
/// year end, and a start in the middle of a cycle. Calendar anchors used below: 1 January 2027 is a Friday,
/// 1 January 2028 a Saturday, and 29 February 2028 a Tuesday.
/// </summary>
public sealed class RecurrenceTests
{
    private static DateOnly D(string date) => DateOnly.Parse(date, CultureInfo.InvariantCulture);

    private static string[] Dates(IEnumerable<DateOnly> dates) =>
        [.. dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];

    [Fact]
    public void Weekly_on_two_days_covers_every_such_day_of_the_year()
    {
        var rule = Recurrence.Weekly([DayOfWeek.Thursday, DayOfWeek.Monday]);

        var dates = rule.Occurrences(D("2027-01-01"), D("2027-01-01"), D("2027-12-31")).ToList();

        Assert.Equal(104, dates.Count); // 52 Mondays and 52 Thursdays in 2027
        Assert.All(dates, d => Assert.Contains(d.DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Thursday }));
        Assert.Equal(["2027-01-04", "2027-01-07", "2027-01-11", "2027-01-14"], Dates(dates.Take(4)));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Thursday], rule.Weekdays);
    }

    [Fact]
    public void Every_two_weeks_from_a_midweek_start_skips_the_earlier_day_of_the_first_week()
    {
        // Wednesday 6 January 2027: the Monday of that week is before the start; the Thursday is the first visit.
        var rule = Recurrence.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday], everyWeeks: 2);

        var dates = rule.Occurrences(D("2027-01-06"), D("2027-01-01"), D("2027-02-05"));

        Assert.Equal(["2027-01-07", "2027-01-18", "2027-01-21", "2027-02-01", "2027-02-04"], Dates(dates));
    }

    [Fact]
    public void Every_three_weeks_runs_across_the_year_end()
    {
        var rule = Recurrence.Weekly([DayOfWeek.Monday], everyWeeks: 3);

        var dates = rule.Occurrences(D("2027-12-13"), D("2027-12-01"), D("2028-02-20"));

        Assert.Equal(["2027-12-13", "2028-01-03", "2028-01-24", "2028-02-14"], Dates(dates));
    }

    [Fact]
    public void A_later_window_keeps_the_cycle_of_the_start()
    {
        var rule = Recurrence.Weekly([DayOfWeek.Monday], everyWeeks: 3);

        // Asking from mid-January must not restart the count: 3 and 24 January are the visits, not 10 or 17.
        var dates = rule.Occurrences(D("2027-12-13"), D("2028-01-05"), D("2028-02-20"));

        Assert.Equal(["2028-01-24", "2028-02-14"], Dates(dates));
    }

    [Fact]
    public void Monthly_on_a_day_gives_twelve_dates_a_year()
    {
        var dates = Recurrence.MonthlyOnDay(15).Occurrences(D("2027-01-01"), D("2027-01-01"), D("2027-12-31")).ToList();

        Assert.Equal(12, dates.Count);
        Assert.All(dates, d => Assert.Equal(15, d.Day));
        Assert.Equal(Enumerable.Range(1, 12), dates.Select(d => d.Month));
    }

    [Fact]
    public void Monthly_on_a_day_starting_after_that_day_begins_next_month()
    {
        var dates = Recurrence.MonthlyOnDay(10).Occurrences(D("2027-03-20"), D("2027-01-01"), D("2027-06-30"));

        Assert.Equal(["2027-04-10", "2027-05-10", "2027-06-10"], Dates(dates));
    }

    [Fact]
    public void Every_five_months_on_the_28th_runs_across_year_ends()
    {
        var dates = Recurrence.MonthlyOnDay(28, everyMonths: 5).Occurrences(D("2027-11-20"), D("2027-11-01"), D("2029-03-31"));

        Assert.Equal(["2027-11-28", "2028-04-28", "2028-09-28", "2029-02-28"], Dates(dates));
    }

    [Fact]
    public void The_last_day_of_the_month_includes_29_February_in_a_leap_year()
    {
        var dates = Recurrence.MonthlyOnLastDay().Occurrences(D("2028-01-15"), D("2028-01-01"), D("2028-12-31"));

        Assert.Equal(
            ["2028-01-31", "2028-02-29", "2028-03-31", "2028-04-30", "2028-05-31", "2028-06-30",
            "2028-07-31", "2028-08-31", "2028-09-30", "2028-10-31", "2028-11-30", "2028-12-31"],
            Dates(dates));
    }

    [Fact]
    public void The_last_day_in_a_common_year_is_28_February()
    {
        var dates = Recurrence.MonthlyOnLastDay(everyMonths: 2).Occurrences(D("2027-02-01"), D("2027-01-01"), D("2027-06-30"));

        Assert.Equal(["2027-02-28", "2027-04-30", "2027-06-30"], Dates(dates));
    }

    [Fact]
    public void The_first_monday_of_each_month_over_a_year()
    {
        var dates = Recurrence.MonthlyOnWeekday(WeekOfMonth.First, DayOfWeek.Monday)
            .Occurrences(D("2027-01-01"), D("2027-01-01"), D("2027-12-31"));

        Assert.Equal(
            ["2027-01-04", "2027-02-01", "2027-03-01", "2027-04-05", "2027-05-03", "2027-06-07",
            "2027-07-05", "2027-08-02", "2027-09-06", "2027-10-04", "2027-11-01", "2027-12-06"],
            Dates(dates));
    }

    [Theory]
    [InlineData(WeekOfMonth.First)]
    [InlineData(WeekOfMonth.Second)]
    [InlineData(WeekOfMonth.Third)]
    [InlineData(WeekOfMonth.Fourth)]
    [InlineData(WeekOfMonth.Last)]
    public void The_nth_weekday_matches_its_definition_in_every_month_of_2027_and_2028(WeekOfMonth week)
    {
        foreach (var weekday in Enum.GetValues<DayOfWeek>())
        {
            var dates = Recurrence.MonthlyOnWeekday(week, weekday)
                .Occurrences(D("2027-01-01"), D("2027-01-01"), D("2028-12-31")).ToList();

            Assert.Equal(24, dates.Count);
            foreach (var date in dates)
            {
                // Defined independently: the nth one has n-1 earlier ones in the month; the last has none after it.
                Assert.Equal(weekday, date.DayOfWeek);
                if (week == WeekOfMonth.Last)
                {
                    Assert.True(date.Day + 7 > DateTime.DaysInMonth(date.Year, date.Month), $"{date} is not the last {weekday}");
                }
                else
                {
                    Assert.Equal((int)week - 1, (date.Day - 1) / 7);
                }
            }
        }
    }

    [Fact]
    public void The_last_friday_of_january_2027_is_the_29th()
    {
        var first = Recurrence.MonthlyOnWeekday(WeekOfMonth.Last, DayOfWeek.Friday)
            .Occurrences(D("2027-01-01"), D("2027-01-01"), D("2027-01-31"));

        Assert.Equal(["2027-01-29"], Dates(first));
    }

    [Fact]
    public void Every_three_months_on_the_second_tuesday_counts_months_from_the_start()
    {
        var dates = Recurrence.MonthlyOnWeekday(WeekOfMonth.Second, DayOfWeek.Tuesday, everyMonths: 3)
            .Occurrences(D("2027-11-15"), D("2027-11-15"), D("2028-08-31"));

        // November's second Tuesday (the 9th) is before the start, so the first visit is in February.
        Assert.Equal(["2028-02-08", "2028-05-09", "2028-08-08"], Dates(dates));
    }

    [Fact]
    public void A_number_of_visits_counts_from_the_start_and_an_end_date_cuts_the_rest()
    {
        var weekly = Recurrence.Weekly([DayOfWeek.Monday]);

        var counted = new Schedule(weekly, D("2027-01-04"), null, 3);
        var ended = new Schedule(weekly, D("2027-01-04"), D("2027-01-15"), null);

        Assert.Equal(["2027-01-11", "2027-01-18"], Dates(counted.Occurrences(D("2027-01-10"), D("2027-12-31"))));
        Assert.Equal(D("2027-01-18"), counted.LastOccurrence());
        Assert.Equal(["2027-01-04", "2027-01-11"], Dates(ended.Occurrences(D("2027-01-01"), D("2027-12-31"))));
        Assert.Equal(D("2027-01-11"), ended.LastOccurrence());
        Assert.Null(new Schedule(weekly, D("2027-01-04"), null, null).LastOccurrence());
    }

    [Theory]
    [InlineData(RecurrenceKind.Weekly, 0, "recurrence.interval")]
    [InlineData(RecurrenceKind.Weekly, 9, "recurrence.interval")]
    [InlineData(RecurrenceKind.MonthlyByDay, 13, "recurrence.interval")]
    public void Intervals_outside_the_range_are_refused(RecurrenceKind kind, int interval, string field)
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            Recurrence.Validate(kind, interval, [DayOfWeek.Monday], 1, false, WeekOfMonth.First, DayOfWeek.Monday));

        Assert.Contains(field, error.Errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(31)]
    public void Days_after_the_28th_are_refused_in_favour_of_the_last_day(int day)
    {
        var error = Assert.Throws<DomainValidationException>(() => Recurrence.MonthlyOnDay(day));

        Assert.Equal(["recurrence.day"], error.Errors.Keys);
    }

    [Fact]
    public void Every_missing_field_is_reported_at_once()
    {
        Assert.Equal(
            ["recurrence.interval", "recurrence.weekdays"],
            Assert.Throws<DomainValidationException>(() => Recurrence.Validate(RecurrenceKind.Weekly, null, [], null, false, null, null)).Errors.Keys.Order());
        Assert.Equal(
            ["recurrence.week", "recurrence.weekday"],
            Assert.Throws<DomainValidationException>(() => Recurrence.Validate(RecurrenceKind.MonthlyByWeekday, 1, null, null, false, null, null)).Errors.Keys.Order());
        Assert.Equal(
            ["recurrence.kind"],
            Assert.Throws<DomainValidationException>(() => Recurrence.Validate(null, 1, null, null, false, null, null)).Errors.Keys);
    }

    [Fact]
    public void A_schedule_refuses_both_ends_an_end_before_the_start_and_too_many_visits()
    {
        var weekly = Recurrence.Weekly([DayOfWeek.Monday]);

        Assert.Equal(["endsOn"], Assert.Throws<DomainValidationException>(() => Schedule.Validate(weekly, D("2027-01-04"), D("2027-02-01"), 3)).Errors.Keys);
        Assert.Equal(["endsOn"], Assert.Throws<DomainValidationException>(() => Schedule.Validate(weekly, D("2027-01-04"), D("2027-01-01"), null)).Errors.Keys);
        Assert.Equal(["maxVisits"], Assert.Throws<DomainValidationException>(() => Schedule.Validate(weekly, D("2027-01-04"), null, 501)).Errors.Keys);
        Assert.Equal(["startsOn"], Assert.Throws<DomainValidationException>(() => Schedule.Validate(weekly, null, null, null)).Errors.Keys);
    }

    [Fact]
    public void Equal_rules_are_equal_and_describe_themselves()
    {
        Assert.Equal(Recurrence.Weekly([DayOfWeek.Thursday, DayOfWeek.Monday], 2), Recurrence.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday], 2));
        Assert.NotEqual(Recurrence.Weekly([DayOfWeek.Monday]), Recurrence.Weekly([DayOfWeek.Monday], 2));
        Assert.Equal("Every 2 weeks on Mon, Thu", Recurrence.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday], 2).Describe());
        Assert.Equal("Monthly on the last day", Recurrence.MonthlyOnLastDay().Describe());
        Assert.Equal("Every 3 months on day 5", Recurrence.MonthlyOnDay(5, 3).Describe());
        Assert.Equal("Monthly on the last Friday", Recurrence.MonthlyOnWeekday(WeekOfMonth.Last, DayOfWeek.Friday).Describe());
    }
}
