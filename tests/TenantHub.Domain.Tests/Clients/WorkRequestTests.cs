using TenantHub.Domain.Clients;

namespace TenantHub.Domain.Tests.Clients;

/// <summary>SPEC-V2 5.5: what a request may say, how many may be open, and its only two ways out.</summary>
public sealed class WorkRequestTests
{
    private static readonly Guid Org = Guid.Parse("0199f0a0-0000-7000-8000-0000000000a1");
    private static readonly Guid User = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static ClientContact Contact()
    {
        var client = Client.Create(Org, ClientDetails.Validate("Acme Offices", null, null, null, null, null), Now);
        return ClientContact.Add(client, "Dana Reyes", "dana@acme.example", 0, false, User, Now);
    }

    private static WorkRequest Open() => Submit("Clean the windows on the 3rd floor");

    private static WorkRequest Submit(string title, string? description = null, DateOnly? preferredOn = null, int open = 0) =>
        WorkRequest.Submit(Contact(), title, description, preferredOn, open, Today, Now);

    [Fact]
    public void Submitting_normalizes_and_records_it_for_the_contacts_client()
    {
        var contact = Contact();

        var request = WorkRequest.Submit(contact, "  Clean   the windows ", "  Third floor only  ", Today, 0, Today, Now);

        Assert.Equal(("Clean the windows", "Third floor only", Today, WorkRequestStatus.Open), (request.Title, request.Description, request.PreferredOn!.Value, request.Status));
        Assert.Equal((contact.ClientId, contact.Id), (request.ClientId, request.ContactId));
        Assert.Equal("Requested work: “Clean the windows”", ((WorkRequestEvent)Assert.Single(request.DomainEvents)).Summary);
    }

    [Fact]
    public void Every_invalid_field_is_reported_at_once()
    {
        var error = Assert.Throws<DomainValidationException>(() => Submit("ab", new string('d', 2001), Today.AddDays(-1)));

        Assert.Equal(["description", "preferredOn", "title"], error.Errors.Keys.Order());
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void A_contact_has_at_most_ten_open_requests(int open, bool allowed)
    {
        var error = Record.Exception(() => Submit("Clean the windows", open: open));

        Assert.Equal(allowed, error is null);
        if (!allowed)
        {
            Assert.Equal("request-limit-reached", Assert.IsType<DomainException>(error).Code);
        }
    }

    [Fact]
    public void Converting_links_the_job_and_closes_the_request()
    {
        var request = Open();
        var job = Guid.NewGuid();

        request.Convert(job, "JOB-0042", User, Now);

        Assert.Equal((WorkRequestStatus.Converted, (Guid?)job, (Guid?)User), (request.Status, request.JobId, request.DecidedByUserId));
        Assert.Equal("request-not-open", Assert.Throws<DomainException>(() => request.Decline("No", User, Now)).Code);
    }

    [Fact]
    public void Declining_needs_a_reason_the_contact_can_read()
    {
        var request = Open();

        Assert.Equal(["reason"], Assert.Throws<DomainValidationException>(() => request.Decline("  ", User, Now)).Errors.Keys);
        Assert.Equal(["reason"], Assert.Throws<DomainValidationException>(() => request.Decline(new string('r', 501), User, Now)).Errors.Keys);
        request.Decline("  We do not clean windows above the 2nd floor. ", User, Now);

        Assert.Equal((WorkRequestStatus.Declined, "We do not clean windows above the 2nd floor."), (request.Status, request.DeclineReason));
        Assert.Equal("request-not-open", Assert.Throws<DomainException>(() => request.Convert(Guid.NewGuid(), "JOB-0001", User, Now)).Code);
    }
}
