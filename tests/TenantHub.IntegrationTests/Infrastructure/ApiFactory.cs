using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Infrastructure.Data;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// The real API in the <c>Testing</c> environment (production behaviour: JSON logs, no API explorer), pointed at a
/// database chosen by the test. Migrations at startup are off; databases come prepared from
/// <see cref="PostgresFixture"/>. <see cref="ThrowingPath"/> is an extra route that throws, to test the
/// unhandled-exception response.
/// </summary>
public class ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public const string ThrowingPath = "/test/throw";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{DatabaseSetup.ConnectionStringName}", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, ThrowingRouteStartupFilter>());
    }

    private sealed class ThrowingRouteStartupFilter : IStartupFilter
    {
        // Added after the app's pipeline, so it runs inside the app's exception handler like an endpoint would.
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use((context, nextMiddleware) => context.Request.Path == ThrowingPath
                ? throw new InvalidOperationException("Test failure with a secret detail.")
                : nextMiddleware(context));
        };
    }
}

/// <summary>
/// For tests that never touch the database: the connection string points at a closed port, so anything that
/// does try to connect fails fast instead of hanging.
/// </summary>
public sealed class OfflineApiFactory() : ApiFactory(UnreachableDatabase)
{
    public const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2";
}
