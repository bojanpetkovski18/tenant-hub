using System.Net;
using Npgsql;
using TenantHub.Infrastructure.Data;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Data;

/// <summary>
/// The two database users (D038): row-level security means nothing if the API connects as a user that skips it,
/// so startup refuses such a user, and migrating on startup provisions the API's own one.
/// </summary>
public sealed class DatabaseRoleTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_api_refuses_to_start_as_a_user_that_can_bypass_row_security()
    {
        // The container's superuser, as v1 connected: superusers skip every policy.
        var asOwner = PostgresFixture.AdminConnectionStringFor(await postgres.CreateDatabaseAsync());
        await using var factory = new ApiFactory(asOwner);

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("is a superuser", error.Message, StringComparison.Ordinal);
        Assert.Contains("owns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrating_on_startup_provisions_the_apis_own_user_without_bypass_powers()
    {
        var connectionString = await postgres.CreateEmptyDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, new Dictionary<string, string?> { ["Database:MigrateOnStartup"] = "true" });

        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        Assert.Empty(await DatabaseRoles.FindBypassPowersAsync(connectionString, Ct));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using (var users = new NpgsqlCommand("SELECT count(*) FROM users", connection))
        {
            Assert.Equal(0L, await users.ExecuteScalarAsync(Ct));
        }

        // Data access only: not the migration history, and no schema changes.
        await using var history = new NpgsqlCommand("""SELECT count(*) FROM "__EFMigrationsHistory" """, connection);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => history.ExecuteScalarAsync(Ct))).SqlState);
        await using var ddl = new NpgsqlCommand("ALTER TABLE users ADD COLUMN smuggled text", connection);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync(Ct))).SqlState);
    }

    [Fact]
    public async Task Migrating_on_startup_needs_the_owners_connection_string()
    {
        await using var factory = new ApiFactory(await postgres.CreateEmptyDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Database:MigrateOnStartup"] = "true",
            [$"ConnectionStrings:{DatabaseSetup.AdminConnectionStringName}"] = "",
        });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings__DatabaseAdmin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_user_for_both_the_api_and_the_owner_is_refused()
    {
        var asOwner = PostgresFixture.AdminConnectionStringFor(await postgres.CreateDatabaseAsync());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseRoles.ProvisionRuntimeRoleAsync(asOwner, asOwner, Ct));

        Assert.Contains("both use the database user", error.Message, StringComparison.Ordinal);
    }
}
