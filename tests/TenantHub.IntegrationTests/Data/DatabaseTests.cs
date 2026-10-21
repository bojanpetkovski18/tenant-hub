using System.Net;
using Microsoft.EntityFrameworkCore;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Data;

public sealed class DatabaseTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Model_matches_the_migrations()
    {
        await using var db = PostgresFixture.CreateDbContext(await postgres.CreateDatabaseAsync());

        // Fails when an entity or configuration changed without `dotnet ef migrations add`.
        Assert.False(db.Database.HasPendingModelChanges(), "The model has changes that no migration captures.");
    }

    [Fact]
    public async Task Migrations_create_the_schema_on_an_empty_database()
    {
        await using var db = PostgresFixture.CreateDbContext(await postgres.CreateEmptyDatabaseAsync());

        await db.Database.MigrateAsync(Ct);

        var tables = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync(Ct);
        Assert.Superset(new HashSet<string> { "users", "organizations", "memberships" }, tables.ToHashSet());
        Assert.DoesNotContain(tables, table => table.StartsWith("asp_net", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_api_applies_migrations_on_startup_when_enabled()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, new Dictionary<string, string?>
        {
            ["Database:MigrateOnStartup"] = "true",
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Readiness_is_healthy_when_the_database_answers()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_but_liveness_is_fine_without_a_database()
    {
        await using var factory = new OfflineApiFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
    }

    [Fact]
    public async Task Startup_fails_fast_without_a_connection_string()
    {
        await using var factory = new ApiFactory("");

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings__Database", error.Message, StringComparison.Ordinal);
    }
}
