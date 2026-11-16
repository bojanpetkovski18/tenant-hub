using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantHub.Application.Abstractions;
using TenantHub.Infrastructure.Data;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(TenantHub.IntegrationTests.Infrastructure.PostgresFixture))]

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container per test run (same image as docker-compose.dev.yml). A template database is
/// migrated once; each test then clones it with <c>CREATE DATABASE ... TEMPLATE</c>, which takes milliseconds
/// and gives every test an isolated database without a reset library.
/// Two users, as in production (D038): the container's superuser owns the schema and runs migrations, and the API
/// connects as <see cref="RuntimeUser"/>, which row-level security applies to. Connection strings handed to tests
/// are the API's; <see cref="CreateDbContext"/> connects as the owner, so arranging and inspecting see every row.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string RuntimeUser = "tenant_hub_app";
    private const string RuntimePassword = "tenant-hub-app-test";
    private const string TemplateDatabase = "th_template";

    // The container's owner credentials, for turning a test's connection string into the owner's (one container per run).
    private static string? s_adminUser;
    private static string? s_adminPassword;

    // Every test has its own database and therefore its own connection pool; extra headroom for parallel runs.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var admin = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        s_adminUser = admin.Username;
        s_adminPassword = admin.Password;
        await ExecuteOnServerAsync($"CREATE DATABASE {TemplateDatabase}");

        await using (var db = CreateDbContext(ConnectionStringFor(TemplateDatabase)))
        {
            await db.Database.MigrateAsync();
        }

        // Grants live in the database, so every clone of the template has them; the role is created once.
        await DatabaseRoles.ProvisionRuntimeRoleAsync(AdminConnectionStringFor(ConnectionStringFor(TemplateDatabase)), ConnectionStringFor(TemplateDatabase));

        // A template cannot be cloned while anything is connected to it.
        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A new database with the current schema and no rows; the connection string is the API's user.</summary>
    public Task<string> CreateDatabaseAsync() => CreateDatabaseAsync(fromTemplate: true);

    /// <summary>A new, completely empty database (no schema), for migration tests; the API's user.</summary>
    public Task<string> CreateEmptyDatabaseAsync() => CreateDatabaseAsync(fromTemplate: false);

    /// <summary>The same database as the owner, which row-level security does not apply to.</summary>
    public static string AdminConnectionStringFor(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = s_adminUser ?? throw new InvalidOperationException("The PostgreSQL fixture has not started."),
            Password = s_adminPassword,
        }.ConnectionString;

    /// <summary>A context for arranging and inspecting data, as the owner. Without a tenant, tenant-owned queries
    /// return nothing (use <c>AcrossTenants()</c> to inspect) and tenant-owned writes are refused, exactly as in the
    /// app; row-level security does not apply to the owner, so inspection sees every organization's rows.</summary>
    public static AppDbContext CreateDbContext(string connectionString, ITenantContext? tenant = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseSetup.Configure(options, AdminConnectionStringFor(connectionString));
        return new AppDbContext(options.Options, tenant);
    }

    private async Task<string> CreateDatabaseAsync(bool fromTemplate)
    {
        var name = $"t_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync(fromTemplate
            ? $"CREATE DATABASE {name} TEMPLATE {TemplateDatabase}"
            : $"CREATE DATABASE {name}");
        return ConnectionStringFor(name);
    }

    /// <summary>
    /// The API's user. Small pools that drop idle connections after about 2 s, so pools of finished tests do not
    /// pile up until PostgreSQL refuses new clients (Fitness Merchant hit 53300 "too many clients" past ~160 tests).
    /// </summary>
    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = RuntimeUser,
            Password = RuntimePassword,
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;

    private async Task ExecuteOnServerAsync(string sql)
    {
        // Database names are generated above, never taken from input; CREATE DATABASE cannot be parameterized.
        await using var connection = new NpgsqlConnection(AdminConnectionStringFor(ConnectionStringFor("postgres")));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
