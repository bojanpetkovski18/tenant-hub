using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Clients;
using TenantHub.Application.Paging;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Clients;

public sealed class ClientEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Clients(Guid org) => $"/api/orgs/{org}/clients";

    private static async Task<ClientView> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<ClientView>(body, BrowserClient.Json)!;
    }

    [Fact]
    public async Task A_client_goes_through_its_whole_life_and_every_step_is_audited()
    {
        var tenants = await SharedApiTenants.GetAsync<ClientEndpointTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var name = $"Lifecycle {Guid.NewGuid():N}";

        var created = await ReadAsync(await member.PostJsonAsync(Clients(tenants.OrgA), new { name, email = "desk@lifecycle.example" }), HttpStatusCode.Created);
        var fetched = await member.Http.GetFromJsonAsync<ClientView>($"{Clients(tenants.OrgA)}/{created.Id}", BrowserClient.Json, Ct);
        var updated = await ReadAsync(await member.SendJsonAsync(HttpMethod.Put, $"{Clients(tenants.OrgA)}/{created.Id}",
            new { name, email = "office@lifecycle.example", phone = "+00 000", version = fetched!.Version }), HttpStatusCode.OK);
        var archived = await ReadAsync(await member.PostJsonAsync($"{Clients(tenants.OrgA)}/{created.Id}/archive", new { }), HttpStatusCode.OK);
        var restored = await ReadAsync(await member.PostJsonAsync($"{Clients(tenants.OrgA)}/{created.Id}/restore", new { }), HttpStatusCode.OK);
        var deleted = await owner.SendJsonAsync(HttpMethod.Delete, $"{Clients(tenants.OrgA)}/{created.Id}", null);
        var gone = await member.Http.GetAsync($"{Clients(tenants.OrgA)}/{created.Id}", Ct);

        Assert.Equal((ClientStatus.Active, "office@lifecycle.example"), (updated.Status, updated.Email));
        Assert.NotEqual(fetched.Version, updated.Version);
        Assert.Equal((ClientStatus.Archived, ClientStatus.Active), (archived.Status, restored.Status));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await ProblemAssert.HasCodeAsync(gone, HttpStatusCode.NotFound, "client-not-found");

        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var history = await db.AuditEntries.AcrossTenants()
            .Where(a => a.EntityId == created.Id)
            .OrderBy(a => a.Id)
            .ToListAsync(Ct);
        Assert.Equal(["client.created", "client.updated", "client.archived", "client.restored", "client.deleted"], history.Select(a => a.Action));
        Assert.All(history, a => Assert.Equal(tenants.OrgA, a.OrganizationId));
        Assert.Equal(tenants.EmailsA[Role.Member].Split('@')[0], history[0].ActorName);
        Assert.Equal(tenants.EmailsA[Role.Owner].Split('@')[0], history[^1].ActorName);
        Assert.Equal(["email", "phone"], history[1].Changes.Select(c => c.Field));
        Assert.All(history, a => Assert.False(string.IsNullOrEmpty(a.TraceId)));
    }

    [Fact]
    public async Task Invalid_fields_come_back_together_as_a_validation_problem()
    {
        var tenants = await SharedApiTenants.GetAsync<ClientEndpointTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);

        var response = await member.PostJsonAsync(Clients(tenants.OrgA), new { name = "A", email = "nope", phone = new string('9', 41) });

        var errors = await ProblemAssert.HasFieldErrorsAsync(response, "name", "email", "phone");
        Assert.Equal(3, errors.EnumerateObject().Count());
    }

    [Fact]
    public async Task Active_names_are_unique_case_insensitively_but_archived_names_can_be_reused()
    {
        var tenants = await SharedApiTenants.GetAsync<ClientEndpointTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);
        var name = $"Unique {Guid.NewGuid():N}";
        var first = await ReadAsync(await member.PostJsonAsync(Clients(tenants.OrgA), new { name }), HttpStatusCode.Created);

        await ProblemAssert.HasCodeAsync(await member.PostJsonAsync(Clients(tenants.OrgA), new { name = name.ToUpperInvariant() }), HttpStatusCode.Conflict, "client-name-taken");

        await member.PostJsonAsync($"{Clients(tenants.OrgA)}/{first.Id}/archive", new { });
        await ReadAsync(await member.PostJsonAsync(Clients(tenants.OrgA), new { name }), HttpStatusCode.Created);
        // Restoring the first would make two active clients with the same name.
        await ProblemAssert.HasCodeAsync(await member.PostJsonAsync($"{Clients(tenants.OrgA)}/{first.Id}/restore", new { }), HttpStatusCode.Conflict, "client-name-taken");
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_and_changes_nothing()
    {
        var tenants = await SharedApiTenants.GetAsync<ClientEndpointTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var created = await ReadAsync(await member.PostJsonAsync(Clients(tenants.OrgA), new { name = $"Busy {Guid.NewGuid():N}" }), HttpStatusCode.Created);

        // Both opened the form at the same version; the admin saves first.
        await ReadAsync(await admin.SendJsonAsync(HttpMethod.Put, $"{Clients(tenants.OrgA)}/{created.Id}", new { created.Name, notes = "Admin's note", version = created.Version }), HttpStatusCode.OK);
        var stale = await member.SendJsonAsync(HttpMethod.Put, $"{Clients(tenants.OrgA)}/{created.Id}", new { created.Name, notes = "Member's note", version = created.Version });

        await ProblemAssert.HasCodeAsync(stale, HttpStatusCode.Conflict, "concurrency-conflict");
        var current = await member.Http.GetFromJsonAsync<ClientView>($"{Clients(tenants.OrgA)}/{created.Id}", BrowserClient.Json, Ct);
        Assert.Equal("Admin's note", current!.Notes);
    }

    [Fact]
    public async Task The_list_searches_filters_sorts_and_pages_in_the_database()
    {
        // A brand-new organization, so the list contains exactly the clients this test creates.
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        var signUp = await browser.SignUpAsync(BrowserClient.NewEmail(), organizationName: "List Test Co");
        var org = (await signUp.Content.ReadFromJsonAsync<SessionResponse>(BrowserClient.Json, Ct))!.User!.Memberships.Single().OrganizationId;
        var names = Enumerable.Range(1, 23).Select(i => $"{(i % 2 == 0 ? "Harbor" : "Summit")} Client {i:00}").ToList();
        foreach (var name in names)
        {
            (await browser.PostJsonAsync(Clients(org), new { name, contactName = name.EndsWith("07", StringComparison.Ordinal) ? "Petra Lindqvist" : null })).EnsureSuccessStatusCode();
        }

        var archive = await browser.Http.GetFromJsonAsync<PagedList<ClientListItem>>($"{Clients(org)}?q=client%2003", BrowserClient.Json, Ct);
        await browser.PostJsonAsync($"{Clients(org)}/{archive!.Items.Single().Id}/archive", new { });

        var page2 = await browser.Http.GetFromJsonAsync<PagedList<ClientListItem>>($"{Clients(org)}?sort=-name&page=2&pageSize=5", BrowserClient.Json, Ct);
        var expected = names.Where(n => n != "Summit Client 03").OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).Skip(5).Take(5);
        Assert.Equal(expected, page2!.Items.Select(c => c.Name));
        Assert.Equal((2, 5, 22), (page2.Page, page2.PageSize, page2.Total));

        var harbor = await browser.Http.GetFromJsonAsync<PagedList<ClientListItem>>($"{Clients(org)}?q=HARBOR&pageSize=100", BrowserClient.Json, Ct);
        Assert.Equal(11, harbor!.Total);
        var byContact = await browser.Http.GetFromJsonAsync<PagedList<ClientListItem>>($"{Clients(org)}?q=lindqvist", BrowserClient.Json, Ct);
        Assert.Equal("Summit Client 07", byContact!.Items.Single().Name);
        var archived = await browser.Http.GetFromJsonAsync<PagedList<ClientListItem>>($"{Clients(org)}?status=archived", BrowserClient.Json, Ct);
        Assert.Equal("Summit Client 03", archived!.Items.Single().Name);
        var all = await browser.Http.GetFromJsonAsync<PagedList<ClientListItem>>($"{Clients(org)}?status=all&pageSize=100", BrowserClient.Json, Ct);
        Assert.Equal(23, all!.Total);

        // Unknown sort and status fall back to the defaults (name, active) instead of failing; paging is clamped.
        var fallback = await browser.Http.GetFromJsonAsync<PagedList<ClientListItem>>($"{Clients(org)}?sort=drop%20table&status=bogus&pageSize=1000", BrowserClient.Json, Ct);
        Assert.Equal((22, 100), (fallback!.Total, fallback.PageSize));
        Assert.Equal(names.Where(n => n != "Summit Client 03").Order(StringComparer.OrdinalIgnoreCase).First(), fallback.Items[0].Name);
    }

    [Fact]
    public async Task If_the_audit_entry_cannot_be_written_the_change_is_not_saved_either()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString);
        using var browser = new BrowserClient(factory);
        var signUp = await browser.SignUpAsync(BrowserClient.NewEmail(), organizationName: "Rollback Co");
        var org = (await signUp.Content.ReadFromJsonAsync<SessionResponse>(BrowserClient.Json, Ct))!.User!.Memberships.Single().OrganizationId;
        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE audit_entries RENAME TO audit_entries_unavailable", Ct);
        }

        var response = await browser.PostJsonAsync(Clients(org), new { name = "Never Saved" });

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.InternalServerError);
        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await check.Clients.AcrossTenants().CountAsync(Ct));
    }
}
