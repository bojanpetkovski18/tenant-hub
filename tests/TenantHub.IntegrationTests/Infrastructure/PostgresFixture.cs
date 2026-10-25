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
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateDatabase = "th_template";

    // Every test has its own database and therefore its own connection pool; extra headroom for parallel runs.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteOnServerAsync($"CREATE DATABASE {TemplateDatabase}");

        await using (var db = CreateDbContext(ConnectionStringFor(TemplateDatabase)))
        {
            await db.Database.MigrateAsync();
        }

        // A template cannot be cloned while anything is connected to it.
        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A new database with the current schema and no rows.</summary>
    public Task<string> CreateDatabaseAsync() => CreateDatabaseAsync(fromTemplate: true);

    /// <summary>A new, completely empty database (no schema), for migration tests.</summary>
    public Task<string> CreateEmptyDatabaseAsync() => CreateDatabaseAsync(fromTemplate: false);

    /// <summary>A context for arranging and inspecting data. Without a tenant, tenant-owned queries return nothing
    /// (use <c>AcrossTenants()</c> to inspect) and tenant-owned writes are refused, exactly as in the app.</summary>
    public static AppDbContext CreateDbContext(string connectionString, ITenantContext? tenant = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseSetup.Configure(options, connectionString);
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
    /// Small pools that drop idle connections after about 2 s, so pools of finished tests do not pile up until
    /// PostgreSQL refuses new clients (Fitness Merchant hit 53300 "too many clients" past ~160 tests).
    /// </summary>
    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;

    private async Task ExecuteOnServerAsync(string sql)
    {
        // Database names are generated above, never taken from input; CREATE DATABASE cannot be parameterized.
        await using var connection = new NpgsqlConnection(ConnectionStringFor("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
