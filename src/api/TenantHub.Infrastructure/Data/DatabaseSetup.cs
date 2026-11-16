using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Data;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Apply pending migrations at startup (development and containers).</summary>
    public bool MigrateOnStartup { get; init; }
}

public static partial class DatabaseSetup
{
    /// <summary>The API's own database user, which row-level security applies to (D038).</summary>
    public const string ConnectionStringName = "Database";

    /// <summary>The schema owner; used only to migrate and provision at startup, so only needed with MigrateOnStartup.</summary>
    public const string AdminConnectionStringName = "DatabaseAdmin";

    public static IServiceCollection AddAppDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail fast with a clear message instead of a connection error on the first request.
            throw new InvalidOperationException(
                $"Missing required setting 'ConnectionStrings:{ConnectionStringName}' " +
                $"(environment variable ConnectionStrings__{ConnectionStringName}). See .env.example.");
        }

        services.AddDbContext<AppDbContext>(options => Configure(options, connectionString));
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section));

        return services;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    /// <summary>
    /// Startup. With <see cref="DatabaseOptions.MigrateOnStartup"/>, migrates as the owner and provisions the API's
    /// user. Then refuses to start if the API's user could bypass row-level security. A database that cannot be
    /// reached does not stop the start: readiness (<c>/health</c>) reports it, as before.
    /// </summary>
    public static async Task PrepareAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var runtimeConnectionString = configuration.GetConnectionString(ConnectionStringName)!;
        if (services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
        {
            var adminConnectionString = configuration.GetConnectionString(AdminConnectionStringName);
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                throw new InvalidOperationException(
                    $"Database:MigrateOnStartup needs 'ConnectionStrings:{AdminConnectionStringName}' (environment " +
                    $"variable ConnectionStrings__{AdminConnectionStringName}): the schema owner that runs the " +
                    "migrations. ConnectionStrings:Database is the API's own user (D038). See .env.example.");
            }

            await MigrateAsync(adminConnectionString, cancellationToken);
            await DatabaseRoles.ProvisionRuntimeRoleAsync(adminConnectionString, runtimeConnectionString, cancellationToken);
        }

        await RefuseBypassingUserAsync(runtimeConnectionString, services.GetRequiredService<ILoggerFactory>(), cancellationToken);
    }

    private static async Task MigrateAsync(string adminConnectionString, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        Configure(options, adminConnectionString);
        await using var db = new AppDbContext(options.Options);
        // On an empty database the migrator first queries a history table that does not exist yet and logs that
        // failure as an error. Creating the table first (CREATE TABLE IF NOT EXISTS) avoids the misleading log;
        // Fitness Merchant found this (its K3).
        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    private static async Task RefuseBypassingUserAsync(string runtimeConnectionString, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> powers;
        try
        {
            powers = await DatabaseRoles.FindBypassPowersAsync(runtimeConnectionString, cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            LogNotChecked(loggers.CreateLogger(typeof(DatabaseSetup)), exception.GetType().Name);
            return;
        }

        if (powers.Count > 0)
        {
            var user = new NpgsqlConnectionStringBuilder(runtimeConnectionString).Username;
            throw new InvalidOperationException(
                $"The API's database user '{user}' {string.Join(" and ", powers)}, so row-level security would not " +
                "apply to it. Use a user that owns nothing and cannot bypass it: set ConnectionStrings:Database to " +
                "the API's own user and ConnectionStrings:DatabaseAdmin to the owner (D038, .env.example).");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database could not be reached at startup ({Error}); the API's database user was not checked. Readiness reports the database.")]
    private static partial void LogNotChecked(ILogger logger, string error);
}
