using System.Globalization;
using TenantHub.Domain.Notifications;

namespace TenantHub.Domain.Tests.Notifications;

/// <summary>SPEC-V2 5.7: the digest is at 07:00 local, whatever daylight saving does to the UTC hour.</summary>
public sealed class DigestScheduleTests
{
    [Theory]
    // Amsterdam: clocks go forward on 28 March 2027 and back on 31 October 2027.
    [InlineData("Europe/Amsterdam", "2027-03-27T06:00:00Z", "2027-03-28T05:00:00Z")]
    [InlineData("Europe/Amsterdam", "2027-03-26T05:59:59Z", "2027-03-26T06:00:00Z")]
    [InlineData("Europe/Amsterdam", "2027-10-30T05:00:00Z", "2027-10-31T06:00:00Z")]
    // New York: forward on 14 March 2027, back on 7 November 2027.
    [InlineData("America/New_York", "2027-03-13T12:00:00Z", "2027-03-14T11:00:00Z")]
    [InlineData("America/New_York", "2027-11-06T11:00:00Z", "2027-11-07T12:00:00Z")]
    // Auckland is ahead of UTC, and leaves daylight time at 03:00 on 4 April 2027: 07:00 NZST is 19:00 UTC the day before.
    [InlineData("Pacific/Auckland", "2027-04-03T10:00:00Z", "2027-04-03T19:00:00Z")]
    public void The_next_digest_is_the_next_seven_oclock_where_the_organization_is(string zone, string now, string expected)
    {
        var next = DigestSchedule.Next(zone, DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), next);
    }

    [Fact]
    public void Email_is_on_by_default_except_for_changed_visits()
    {
        Assert.Equal([NotificationType.VisitChanged], NotificationTypes.All.Where(t => !NotificationTypes.EmailByDefault(t)));
        Assert.Equal(7, NotificationTypes.All.Select(NotificationTypes.Label).Distinct().Count());
    }

    [Fact]
    public void A_long_text_is_cut_to_the_limit()
    {
        var notification = Notification.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationType.Assigned, new NotificationContent(new string('x', 600), NotificationLink.Job, null), Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(Notification.TextMaxLength, notification.Content.Text.Length);
        Assert.EndsWith("…", notification.Content.Text, StringComparison.Ordinal);
        Assert.True(notification.MarkRead(DateTimeOffset.UtcNow));
        Assert.False(notification.MarkRead(DateTimeOffset.UtcNow));
    }
}
