using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Features.Auth;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Organizations;

public sealed class CreateOrganizationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_in_user_creates_another_organization_and_owns_it()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        await browser.SignUpAsync(BrowserClient.NewEmail(), organizationName: "Brightline Cleaning Co.");

        var response = await browser.CreateOrganizationAsync("Harbor & Pine Landscaping", "America/New_York");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipResponse>(BrowserClient.Json, Ct);
        Assert.Equal(("harbor-pine-landscaping", Role.Owner), (membership!.Slug, membership.Role));
        Assert.Equal($"/api/orgs/{membership.OrganizationId}", response.Headers.Location?.OriginalString);

        var session = await browser.SessionAsync();
        Assert.Equal(["Brightline Cleaning Co.", "Harbor & Pine Landscaping"], session.User!.Memberships.Select(m => m.Name));
    }

    [Fact]
    public async Task Invalid_name_or_time_zone_is_a_validation_problem()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        await browser.SignUpAsync(BrowserClient.NewEmail());

        await ProblemAssert.HasCodeAsync(await browser.CreateOrganizationAsync("X"), HttpStatusCode.BadRequest, "invalid-organization-name");
        await ProblemAssert.HasCodeAsync(await browser.CreateOrganizationAsync("Acme", "Nowhere/City"), HttpStatusCode.BadRequest, "invalid-time-zone");
    }

    [Fact]
    public async Task The_sixth_owned_organization_is_refused()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        await browser.SignUpAsync(BrowserClient.NewEmail(), organizationName: "Org One");
        for (var n = 2; n <= 5; n++)
        {
            (await browser.CreateOrganizationAsync($"Org {n}")).EnsureSuccessStatusCode();
        }

        var sixth = await browser.CreateOrganizationAsync("Org Six");

        await ProblemAssert.HasCodeAsync(sixth, HttpStatusCode.Conflict, "owned-organization-limit");
        Assert.Equal(5, (await browser.SessionAsync()).User!.Memberships.Count);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_exceed_the_owned_organization_limit()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString);
        using var browser = new BrowserClient(factory);
        await browser.SignUpAsync(BrowserClient.NewEmail(), organizationName: "Org One");
        await browser.SessionAsync();

        // Four slots left, eight requests at once: without the row lock several would pass the count together.
        var responses = await Task.WhenAll(Enumerable.Range(1, 8).Select(n => browser.CreateOrganizationAsync($"Parallel {n}")));

        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(5, await db.Organizations.CountAsync(Ct));
    }
}
