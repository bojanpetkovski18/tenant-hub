using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Auth;

public sealed class SignUpTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_up_creates_the_user_organization_and_owner_membership_and_signs_in()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString);
        using var browser = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();

        var response = await browser.SignUpAsync(email, organizationName: "  Brightline   Cleaning Co. ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SessionResponse>(BrowserClient.Json, Ct);
        var membership = Assert.Single(created!.User!.Memberships);
        Assert.Equal("brightline-cleaning-co", membership.Slug);
        Assert.Equal("Brightline Cleaning Co.", membership.Name);
        Assert.Equal(Role.Owner, membership.Role);

        var session = await browser.SessionAsync();
        Assert.Equal(email, session.User!.Email);
        Assert.Equal("Olivia Bennett", session.User.DisplayName);

        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var organization = await db.Organizations.SingleAsync(Ct);
        Assert.Equal(Plan.Free, organization.Plan);
        Assert.Equal("Europe/Amsterdam", organization.TimeZone);
        Assert.Equal(session.User.Id, organization.CreatedByUserId);
        var row = await db.Memberships.AcrossTenants().SingleAsync(Ct);
        Assert.Equal((organization.Id, session.User.Id, Role.Owner), (row.OrganizationId, row.UserId, row.Role));
    }

    [Fact]
    public async Task Invalid_input_returns_every_field_error_and_creates_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString);
        using var browser = new BrowserClient(factory);

        var response = await browser.SignUpAsync(
            BrowserClient.NewEmail(), organizationName: "A", password: "short", displayName: "   ", timeZone: "Mars/Olympus_Mons");

        await ProblemAssert.HasFieldErrorsAsync(response, "organizationName", "timeZone", "displayName", "password");
        await AssertEmptyAsync(connectionString);
    }

    [Fact]
    public async Task A_failure_after_the_user_was_created_rolls_everything_back()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString);
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), organizationName: "Acme")).EnsureSuccessStatusCode();
        // Take every slug candidate for "Acme", so creating the organization fails after the user insert succeeded.
        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            var ownerId = (await db.Memberships.AcrossTenants().SingleAsync(Ct)).UserId;
            foreach (var slug in OrganizationSlug.Candidates("Acme").Skip(1))
            {
                db.Organizations.Add(Organization.Create("Acme", slug, "UTC", ownerId, DateTimeOffset.UtcNow));
            }
            await db.SaveChangesAsync(Ct);
        }

        using var browser = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        var response = await browser.SignUpAsync(email, organizationName: "Acme");

        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, "organization-name-taken");
        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.False(await check.Users.AnyAsync(u => u.Email == email, Ct));
        Assert.Null((await browser.SessionAsync()).User);
    }

    [Fact]
    public async Task A_registered_email_is_refused_case_insensitively()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var first = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await first.SignUpAsync(email)).EnsureSuccessStatusCode();

        using var second = new BrowserClient(factory);
        var response = await second.SignUpAsync(email.ToUpperInvariant());

        var errors = await ProblemAssert.HasFieldErrorsAsync(response, "email");
        Assert.Contains("Sign in instead", errors.GetProperty("email")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_organization_with_the_same_name_gets_a_numbered_slug()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var first = new BrowserClient(factory);
        using var second = new BrowserClient(factory);

        await first.SignUpAsync(BrowserClient.NewEmail(), organizationName: "Harbor & Pine");
        var response = await second.SignUpAsync(BrowserClient.NewEmail(), organizationName: "Harbor & Pine");

        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(BrowserClient.Json, Ct);
        Assert.Equal("harbor-pine-2", Assert.Single(session!.User!.Memberships).Slug);
    }

    [Fact]
    public async Task Missing_fields_are_rejected_by_request_validation()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);

        var response = await browser.PostJsonAsync("/api/auth/sign-up", new { email = "not-an-email" });

        // Keys use the JSON names, like the handler errors, so the SPA maps both the same way.
        await ProblemAssert.HasFieldErrorsAsync(response, "displayName", "password", "organizationName", "timeZone");
    }

    private static async Task AssertEmptyAsync(string connectionString)
    {
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await db.Users.CountAsync(Ct));
        Assert.Equal(0, await db.Organizations.CountAsync(Ct));
        Assert.Equal(0, await db.Memberships.AcrossTenants().CountAsync(Ct));
    }
}
