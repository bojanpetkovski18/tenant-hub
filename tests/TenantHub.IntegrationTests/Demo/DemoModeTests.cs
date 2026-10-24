using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Api.Features.Demo;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Demo;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Demo;

/// <summary>SPEC 4.1 demo mode and US-61: one-click roles, protections for the shared accounts, the reset and its schedule.</summary>
public sealed class DemoModeTests(PostgresFixture postgres)
{
    private const string Password = "Seed-Password-77";
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> DemoSettings(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?> { ["DEMO_MODE"] = "true", [DemoSeeder.PasswordSetting] = Password, ["DEMO_RESET_INTERVAL_HOURS"] = "6" };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    private async Task<(ApiFactory Factory, MovingClock Clock, string ConnectionString)> DemoAsync(string? connectionString = null, DateTimeOffset? now = null, params (string Key, string Value)[] extra)
    {
        var clock = new MovingClock(now ?? Start);
        connectionString ??= await postgres.CreateDatabaseAsync();
        var factory = new ApiFactory(connectionString, DemoSettings(extra), s => s.AddSingleton<TimeProvider>(clock));
        using var warmUp = factory.CreateClient(); // starts the app, which seeds
        return (factory, clock, connectionString);
    }

    private static async Task<HttpResponseMessage> DemoSignInAsync(BrowserClient browser, string role)
    {
        await browser.SessionAsync(); // the XSRF cookie, like the SPA on start
        return await browser.PostJsonAsync("/api/demo/sign-in", new { role });
    }

    [Fact]
    public async Task Outside_demo_mode_the_demo_endpoints_do_not_exist()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string?> { ["Seed:Enabled"] = "true", [DemoSeeder.PasswordSetting] = Password });
        using var browser = new BrowserClient(factory);

        await ProblemAssert.IsProblemAsync(await browser.Http.GetAsync("/api/demo/accounts", Ct), HttpStatusCode.NotFound);
        await ProblemAssert.IsProblemAsync(await DemoSignInAsync(browser, "owner"), HttpStatusCode.NotFound);
        Assert.Null((await browser.SessionAsync()).User);
    }

    [Fact]
    public async Task Each_role_signs_in_with_one_click_to_the_seeded_organization()
    {
        var (factory, _, _) = await DemoAsync();
        await using var _ = factory;
        using var anonymous = new BrowserClient(factory);

        var info = await anonymous.Http.GetFromJsonAsync<DemoInfoResponse>("/api/demo/accounts", BrowserClient.Json, Ct);

        Assert.Equal((6, Start.AddHours(6)), (info!.ResetIntervalHours, info.NextResetAt));
        Assert.Equal([Role.Owner, Role.Admin, Role.Member, Role.Viewer], info.Accounts.Select(a => a.Role));
        foreach (var account in info.Accounts)
        {
            using var browser = new BrowserClient(factory);
            (await DemoSignInAsync(browser, account.Role.ToString().ToLowerInvariant())).EnsureSuccessStatusCode();
            var user = (await browser.SessionAsync()).User!;
            Assert.Equal(account.Email, user.Email);
            Assert.Contains(user.Memberships, m => m.Slug == DemoSeeder.BrightlineSlug && m.Role == account.Role);
        }

        using var stranger = new BrowserClient(factory);
        await ProblemAssert.IsProblemAsync(await DemoSignInAsync(stranger, "superuser"), HttpStatusCode.BadRequest);
        await ProblemAssert.IsProblemAsync(await stranger.PostJsonAsync("/api/demo/sign-in", new { }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Demo_accounts_keep_their_password_and_places_and_the_demo_organizations_stay()
    {
        var (factory, _, connectionString) = await DemoAsync();
        await using var _ = factory;
        var (brightline, ids) = await BrightlineAsync(connectionString);
        using var owner = new BrowserClient(factory);
        (await DemoSignInAsync(owner, "owner")).EnsureSuccessStatusCode();
        var org = $"/api/orgs/{brightline}";

        await ProblemAssert.HasCodeAsync(
            await owner.PostJsonAsync("/api/me/password", new { currentPassword = Password, newPassword = "Another-Password-88" }),
            HttpStatusCode.Forbidden,
            DemoProtection.Code);
        await ProblemAssert.HasCodeAsync(await owner.SendJsonAsync(HttpMethod.Delete, $"{org}/members/{ids["adam@brightline.example"]}", null), HttpStatusCode.Forbidden, DemoProtection.Code);
        await ProblemAssert.HasCodeAsync(await owner.PatchJsonAsync($"{org}/members/{ids["maya@brightline.example"]}", new { role = "viewer" }), HttpStatusCode.Forbidden, DemoProtection.Code);
        await ProblemAssert.HasCodeAsync(await owner.SendJsonAsync(HttpMethod.Delete, org, new { confirmName = "Brightline Cleaning Co." }), HttpStatusCode.Forbidden, DemoProtection.Code);
        using var member = new BrowserClient(factory);
        (await DemoSignInAsync(member, "member")).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await member.SendJsonAsync(HttpMethod.Delete, $"{org}/members/me", null), HttpStatusCode.Forbidden, DemoProtection.Code);

        // The password still works, and a visitor's own organization is theirs to delete.
        using var check = new BrowserClient(factory);
        (await check.SignInAsync("olivia@brightline.example", Password)).EnsureSuccessStatusCode();
        var created = await (await owner.CreateOrganizationAsync("Olivia Test Co")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(BrowserClient.Json, Ct);
        var visitorOrg = created.GetProperty("organizationId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{visitorOrg}", new { confirmName = "Olivia Test Co" })).StatusCode);
    }

    [Fact]
    public async Task Outside_demo_mode_the_seeded_accounts_are_ordinary()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, new Dictionary<string, string?> { ["Seed:Enabled"] = "true", [DemoSeeder.PasswordSetting] = Password });
        using var owner = new BrowserClient(factory);
        (await owner.SignInAsync("olivia@brightline.example", Password)).EnsureSuccessStatusCode();
        var (brightline, ids) = await BrightlineAsync(connectionString);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{brightline}/members/{ids["victor@brightline.example"]}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostJsonAsync("/api/me/password", new { currentPassword = Password, newPassword = "Another-Password-88" })).StatusCode);
    }

    [Fact]
    public async Task The_reset_restores_the_seed_on_its_stored_schedule_and_removes_old_visitor_organizations()
    {
        var (factory, clock, connectionString) = await DemoAsync();
        var (brightline, _) = await BrightlineAsync(connectionString);
        using var owner = new BrowserClient(factory);
        (await DemoSignInAsync(owner, "owner")).EnsureSuccessStatusCode();
        (await owner.PatchJsonAsync($"/api/orgs/{brightline}", new { name = "Renamed by a visitor", timeZone = "Europe/Amsterdam" })).EnsureSuccessStatusCode();
        (await owner.PostJsonAsync($"/api/orgs/{brightline}/clients", new { name = "Visitor Client" })).EnsureSuccessStatusCode();

        // Visitors' own organizations: one from the first day, one from just before the reset.
        using var visitor = new BrowserClient(factory);
        (await visitor.SignUpAsync(BrowserClient.NewEmail(), "Early Visitor Co")).EnsureSuccessStatusCode();
        clock.Now = Start.AddHours(23);
        using var late = new BrowserClient(factory);
        (await late.SignUpAsync(BrowserClient.NewEmail(), "Late Visitor Co")).EnsureSuccessStatusCode();
        await factory.DisposeAsync();

        // A restart before the interval is over does not reset; one after it does.
        clock.Now = Start.AddHours(5);
        Assert.Equal(Start.AddHours(6), await ResetIfDueAsync(connectionString, clock));
        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            Assert.Equal("Renamed by a visitor", await db.Organizations.Where(o => o.Id == brightline).Select(o => o.Name).SingleAsync(Ct));
        }

        clock.Now = Start.AddHours(25);
        Assert.Equal(Start.AddHours(31), await ResetIfDueAsync(connectionString, clock));
        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            Assert.Equal(
                ["Brightline Cleaning Co.", "Harbor & Pine Landscaping", "Late Visitor Co"],
                await db.Organizations.OrderBy(o => o.Name).Select(o => o.Name).ToListAsync(Ct));
            var reseeded = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
            Assert.Equal(Start.AddHours(25), reseeded.CreatedAt);
            Assert.Equal(40, await db.Clients.AcrossTenants().CountAsync(c => c.OrganizationId == reseeded.Id, Ct));
            Assert.Equal(150, await db.Jobs.AcrossTenants().CountAsync(j => j.OrganizationId == reseeded.Id, Ct));
            Assert.Equal(8, await db.Users.CountAsync(u => u.Email!.EndsWith(".example") && !u.Email.EndsWith("@test.example"), Ct));
        }
    }

    [Fact]
    public async Task Demo_mode_lowers_the_sign_in_limit_and_never_sends_email()
    {
        var (factory, _, _) = await DemoAsync(null, null, ("RateLimiting:DemoAuthPermitLimit", "2"), ("Email:Mode", "Smtp"));
        await using var _ = factory;
        using var browser = new BrowserClient(factory);

        (await DemoSignInAsync(browser, "viewer")).EnsureSuccessStatusCode();
        (await DemoSignInAsync(browser, "viewer")).EnsureSuccessStatusCode();
        await ProblemAssert.IsProblemAsync(await DemoSignInAsync(browser, "viewer"), HttpStatusCode.TooManyRequests);
        Assert.False(factory.Services.GetRequiredService<IEmailSender>().IsEnabled);
    }

    [Fact]
    public async Task Demo_mode_without_the_demo_password_does_not_start()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string?> { ["DEMO_MODE"] = "true" });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(DemoSeeder.PasswordSetting, error.Message, StringComparison.Ordinal);
    }

    /// <summary>A fresh start on the same database at the clock's time (no demo mode, so no background reset races it).</summary>
    private static async Task<DateTimeOffset> ResetIfDueAsync(string connectionString, MovingClock clock)
    {
        await using var factory = new ApiFactory(connectionString, new Dictionary<string, string?>
        {
            [DemoSeeder.PasswordSetting] = Password,
            ["DEMO_RESET_INTERVAL_HOURS"] = "6",
        }, s => s.AddSingleton<TimeProvider>(clock));
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoReset>().ResetIfDueAsync(Ct);
    }

    private static async Task<(Guid Brightline, Dictionary<string, Guid> Ids)> BrightlineAsync(string connectionString)
    {
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var brightline = await db.Organizations.Where(o => o.Slug == DemoSeeder.BrightlineSlug).Select(o => o.Id).SingleAsync(Ct);
        return (brightline, await db.Users.ToDictionaryAsync(u => u.Email!, u => u.Id, Ct));
    }

    private sealed class MovingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
