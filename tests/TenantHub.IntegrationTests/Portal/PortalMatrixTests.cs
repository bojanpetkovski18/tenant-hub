using System.Net;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Common.Auth;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>
/// The portal matrix (SPEC-V2 3.2, criterion 13): for every portal endpoint, A1's contact using another client's ids
/// (same organization, then another organization) gets 404, nothing else changes, and no foreign data appears in any
/// body. A staff cookie on a portal route, and a portal cookie on a staff route, are both refused.
/// </summary>
public sealed class PortalMatrixTests(PostgresFixture postgres) : IAsyncLifetime
{
    private PortalTenants _tenants = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Cases() => new(PortalEndpointCase.All.Select(c => c.Key));

    public static TheoryData<string> ResourceCases() => new(PortalEndpointCase.All.Where(c => c.Resource != PortalResource.None).Select(c => c.Key));

    public async ValueTask InitializeAsync() => _tenants = await PortalTenants.CreateAsync(postgres);

    public async ValueTask DisposeAsync() => await _tenants.DisposeAsync();

    [Theory]
    [MemberData(nameof(ResourceCases))]
    public async Task Another_clients_ids_are_not_found_change_nothing_and_leak_nothing(string key)
    {
        var endpoint = PortalEndpointCase.All.Single(c => c.Key == key);
        using var contact = await _tenants.SignInAsync(_tenants.A1);

        var own = await endpoint.SendAsync(contact, _tenants.A1, _tenants.A1);
        Assert.Equal(endpoint.Allowed, own.StatusCode);

        foreach (var foreign in new[] { _tenants.A2, _tenants.B1 })
        {
            var before = await _tenants.SnapshotExceptAsync(_tenants.A1);
            var response = await endpoint.SendAsync(contact, _tenants.A1, foreign);
            var body = await response.Content.ReadAsStringAsync(Ct);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.NotFound, endpoint.NotFoundCode);
            Assert.Equal(before, await _tenants.SnapshotExceptAsync(_tenants.A1));
            Assert.DoesNotContain(foreign.JobTitle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(foreign.Name, body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_endpoint_shows_the_contact_nothing_of_another_client(string key)
    {
        var endpoint = PortalEndpointCase.All.Single(c => c.Key == key);
        using var contact = await _tenants.SignInAsync(_tenants.A1);
        var before = await _tenants.SnapshotExceptAsync(_tenants.A1);

        var response = await endpoint.SendAsync(contact, _tenants.A1, _tenants.A1);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(endpoint.Allowed, response.StatusCode);
        if (endpoint.ShowsOwn is { } own)
        {
            Assert.Contains(own(_tenants.A1), body, StringComparison.Ordinal);
        }

        foreach (var other in new[] { _tenants.A2, _tenants.B1, _tenants.B2 })
        {
            Assert.DoesNotContain(other.JobTitle, body, StringComparison.Ordinal);
            Assert.DoesNotContain(other.ContactEmail, body, StringComparison.Ordinal);
            Assert.DoesNotContain(other.RequestTitle, body, StringComparison.Ordinal);
        }

        Assert.Equal(before, await _tenants.SnapshotExceptAsync(_tenants.A1));
    }

    public static TheoryData<string> AuditedCases() => new(PortalEndpointCase.All.Where(c => c.Audited).Select(c => c.Key));

    [Fact]
    public void Every_portal_write_is_marked_audited() =>
        Assert.All(PortalEndpointCase.All.Where(c => c.Method != "GET"), c => Assert.True(c.Audited, $"{c.Key} changes data and must be Audited."));

    [Theory]
    [MemberData(nameof(AuditedCases))]
    public async Task A_portal_write_is_audited_as_the_contact(string key)
    {
        var endpoint = PortalEndpointCase.All.Single(c => c.Key == key);
        using var contact = await _tenants.SignInAsync(_tenants.A1);

        var response = await endpoint.SendAsync(contact, _tenants.A1, _tenants.A1);

        Assert.Equal(endpoint.Allowed, response.StatusCode);
        await using var db = PostgresFixture.CreateDbContext(_tenants.ConnectionString);
        var entries = await db.AuditEntries.AcrossTenants()
            .Where(a => a.OrganizationId == _tenants.A1.OrganizationId && a.ActorRef == _tenants.A1.ContactId)
            .ToListAsync(Ct);
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal((ActorKind.Contact, (Guid?)null, $"{_tenants.A1.Name} Contact"), (e.ActorKind, e.ActorUserId, e.ActorName)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Without_a_portal_session_or_with_a_staff_one_every_portal_endpoint_is_refused(string key)
    {
        var endpoint = PortalEndpointCase.All.Single(c => c.Key == key);
        using var nobody = new BrowserClient(_tenants.Factory);
        var staff = _tenants.OwnerOf(_tenants.A1.OrganizationId); // Owner of A1's organization: still no portal session

        Assert.Equal(HttpStatusCode.Unauthorized, (await endpoint.SendAsync(nobody, _tenants.A1, _tenants.A1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await endpoint.SendAsync(staff, _tenants.A1, _tenants.A1)).StatusCode);
    }

    [Fact]
    public async Task A_portal_session_is_not_a_staff_session()
    {
        using var contact = await _tenants.SignInAsync(_tenants.A1);
        var organization = _tenants.A1.OrganizationId;

        var clients = await contact.Http.GetAsync($"/api/orgs/{organization}/clients", Ct);
        var session = await contact.SessionAsync();
        // The portal cookie is scoped to the portal API; even sent by hand to a staff route it is not a staff session.
        var cookie = contact.Cookies.GetCookies(new Uri(BrowserClient.BaseAddress, PortalAuth.ApiPath + "/"))[PortalAuth.CookieName];
        using var forged = new HttpRequestMessage(HttpMethod.Get, $"/api/orgs/{organization}/clients");
        forged.Headers.Add("Cookie", $"{PortalAuth.CookieName}={cookie!.Value}");
        using var bare = new BrowserClient(_tenants.Factory);
        var forgedResponse = await bare.Http.SendAsync(forged, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, clients.StatusCode);
        Assert.Null(session.User);
        Assert.Equal(HttpStatusCode.Unauthorized, forgedResponse.StatusCode);
        Assert.Equal((true, PortalAuth.ApiPath), (cookie.HttpOnly, cookie.Path));
    }
}
