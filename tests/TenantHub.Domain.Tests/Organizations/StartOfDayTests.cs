using System.Globalization;
using TenantHub.Domain.Organizations;

namespace TenantHub.Domain.Tests.Organizations;

/// <summary>Date-range filters use the organization's days: a day starts at local midnight, in UTC terms.</summary>
public sealed class StartOfDayTests
{
    [Theory]
    [InlineData("UTC", "2026-10-22", "2026-10-22T00:00:00Z")]
    [InlineData("Pacific/Auckland", "2026-10-22", "2026-10-21T11:00:00Z")]
    [InlineData("America/Los_Angeles", "2026-10-22", "2026-10-22T07:00:00Z")]
    [InlineData("Europe/Amsterdam", "2026-10-25", "2026-10-24T22:00:00Z")]
    [InlineData("Europe/Amsterdam", "2026-10-26", "2026-10-25T23:00:00Z")]
    public void A_day_starts_at_local_midnight(string zone, string day, string expected) =>
        Assert.Equal(
            DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture),
            Organization.StartOfDayIn(zone, DateOnly.Parse(day, CultureInfo.InvariantCulture)));

    [Fact]
    public void A_day_whose_midnight_does_not_exist_starts_at_the_first_valid_local_time()
    {
        // Chile moves its clocks forward at midnight: on 6 September 2026 the local day begins at 01:00.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        var day = new DateOnly(2026, 9, 6);
        Assert.True(zone.IsInvalidTime(day.ToDateTime(TimeOnly.MinValue)), "precondition: midnight is skipped that day");

        var start = Organization.StartOfDayIn("America/Santiago", day);

        Assert.Equal(new TimeOnly(1, 0), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime));
        Assert.Equal(day, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime));
    }
}
