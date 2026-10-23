using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Jobs;
using TenantHub.Application.Organizations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Organizations;

/// <summary>US-51 through the API: rename and re-zone (with its effect on overdue), and deleting an organization.</summary>
public sealed class OrganizationSettingsTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_admin_renames_and_re_zones_and_overdue_follows_the_new_zone()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var org = $"/api/orgs/{tenants.OrgA}";

        // Honolulu (UTC-10) and Kiritimati (UTC+14) are exactly a day apart and neither observes daylight saving, so a
        // job due today in Honolulu is due yesterday in Kiritimati: overdue there, not here, at any moment.
        var due = Domain.Organizations.Organization.TodayIn("Pacific/Honolulu", DateTimeOffset.UtcNow);
        var job = await (await admin.PostJsonAsync($"{org}/jobs", new { title = "Zone check", clientId = tenants.ClientA, dueOn = due })).Content.ReadFromJsonAsync<JobView>(BrowserClient.Json, Ct);

        var invalid = await admin.PatchJsonAsync(org, new { name = "x", timeZone = "Mars/Olympus_Mons" });
        var honolulu = await (await admin.PatchJsonAsync(org, new { name = "  Alpha   Cleaning Services ", timeZone = "Pacific/Honolulu" })).Content.ReadFromJsonAsync<OrganizationOverview>(BrowserClient.Json, Ct);
        var inHonolulu = await admin.Http.GetFromJsonAsync<JobView>($"{org}/jobs/{job!.Id}", BrowserClient.Json, Ct);
        await admin.PatchJsonAsync(org, new { name = "Alpha Cleaning Services", timeZone = "Pacific/Kiritimati" });
        var inKiritimati = await admin.Http.GetFromJsonAsync<JobView>($"{org}/jobs/{job.Id}", BrowserClient.Json, Ct);

        await ProblemAssert.HasFieldErrorsAsync(invalid, "name", "timeZone");
        Assert.Equal(("Alpha Cleaning Services", "Pacific/Honolulu", "alpha-cleaning"), (honolulu!.Name, honolulu.TimeZone, honolulu.Slug));
        Assert.False(inHonolulu!.IsOverdue);
        Assert.True(inKiritimati!.IsOverdue);

        var session = await admin.SessionAsync();
        Assert.Equal("Alpha Cleaning Services", session.User!.Memberships.Single(m => m.OrganizationId == tenants.OrgA).Name);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var updates = await db.AuditEntries.AcrossTenants().Where(a => a.OrganizationId == tenants.OrgA && a.Action == "organization.updated").OrderBy(a => a.Id).ToListAsync(Ct);
        Assert.Equal(
            ["Updated the organization: renamed it to “Alpha Cleaning Services” and set the time zone to Pacific/Honolulu", "Updated the organization: set the time zone to Pacific/Kiritimati"],
            updates.Select(u => u.Summary));
    }

    [Fact]
    public async Task Deleting_needs_the_name_typed_exactly()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);

        foreach (var typed in new[] { "alpha cleaning", "Alpha", "" })
        {
            await ProblemAssert.HasFieldErrorsAsync(await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}", new { confirmName = typed }), "confirmName");
        }

        await ProblemAssert.HasFieldErrorsAsync(await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}", null), "confirmName");
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.GetAsync($"/api/orgs/{tenants.OrgA}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Deleting_removes_every_tenant_owned_row_and_nothing_of_another_organization()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var member = await tenants.ClientForAsync(Role.Member);
        await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        await tenants.CreateInvitationAsync(tenants.OrgA);
        var bBefore = await tenants.SnapshotOfBAsync();
        var tables = TenantOwnedTables(tenants.ConnectionString);
        Assert.All(tables, table => Assert.True(CountAsync(tenants.ConnectionString, table, tenants.OrgA).Result > 0, $"precondition: {table} has rows of A"));

        var deleted = await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}", new { confirmName = "  Alpha   Cleaning " });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        foreach (var table in tables)
        {
            Assert.Equal(0, await CountAsync(tenants.ConnectionString, table, tenants.OrgA));
        }

        await using (var db = PostgresFixture.CreateDbContext(tenants.ConnectionString))
        {
            Assert.False(await db.Organizations.AnyAsync(o => o.Id == tenants.OrgA, Ct));
            Assert.True(await db.Users.AnyAsync(u => u.Email == tenants.EmailsA[Role.Member], Ct), "accounts stay; they may belong elsewhere");
        }

        Assert.Equal(bBefore, await tenants.SnapshotOfBAsync());
        await ProblemAssert.HasCodeAsync(await member.Http.GetAsync($"/api/orgs/{tenants.OrgA}/jobs", Ct), HttpStatusCode.NotFound, TenantResolutionFilter.OrganizationNotFoundCode);
        Assert.DoesNotContain((await member.SessionAsync()).User!.Memberships, m => m.OrganizationId == tenants.OrgA);
        Assert.Contains(tenants.Factory.Logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains($"Organization {tenants.OrgA} (alpha-cleaning) with 5 active members was deleted by user", StringComparison.Ordinal));
    }

    /// <summary>Every tenant-owned table, from the model: a new tenant-owned entity is checked without editing this test.</summary>
    private static List<string> TenantOwnedTables(string connectionString)
    {
        using var db = PostgresFixture.CreateDbContext(connectionString);
        return
        [
            .. db.Model.GetEntityTypes()
                .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType))
                .Select(t => t.GetTableName()!)
                .Order(StringComparer.Ordinal),
        ];
    }

    private static async Task<long> CountAsync(string connectionString, string table, Guid organizationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        // The table name comes from the EF model above, never from input; the organization is a parameter.
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\" WHERE organization_id = @org", connection);
        command.Parameters.AddWithValue("org", organizationId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
