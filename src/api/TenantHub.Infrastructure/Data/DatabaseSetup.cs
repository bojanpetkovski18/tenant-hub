using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Data;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Apply pending migrations at startup (development and containers).</summary>
    public bool MigrateOnStartup { get; init; }
}

public static class DatabaseSetup
{
    public const string ConnectionStringName = "Database";

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

    /// <summary>Applies pending migrations when <see cref="DatabaseOptions.MigrateOnStartup"/> is on.</summary>
    public static async Task MigrateIfEnabledAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // On an empty database the migrator first queries a history table that does not exist yet and logs that
        // failure as an error. Creating the table first (CREATE TABLE IF NOT EXISTS) avoids the misleading log;
        // Fitness Merchant found this (its K3).
        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
