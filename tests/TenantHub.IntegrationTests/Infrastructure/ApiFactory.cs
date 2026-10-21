using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Api.Common;
using TenantHub.Infrastructure.Data;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// The real API in the <c>Testing</c> environment (production behaviour: JSON logs, no API explorer), pointed at a
/// database chosen by the test. Migrations at startup are off; databases come prepared from
/// <see cref="PostgresFixture"/>. Two test-only routes go through the real pipeline: <see cref="ThrowingPath"/>
/// throws, and <see cref="UnannotatedPath"/> has no authorization metadata of its own.
/// </summary>
public class ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public const string ThrowingPath = "/api/test/throw";
    public const string UnannotatedPath = "/api/test/unannotated";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{DatabaseSetup.ConnectionStringName}", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        // Raised so only the test that targets the limit can hit it.
        builder.UseSetting("RateLimiting:AuthPermitLimit", "1000");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton<IEndpointMapper, TestEndpoints>());
    }

    private sealed class TestEndpoints : IEndpointMapper
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet(ThrowingPath, IResult () => throw new InvalidOperationException("Test failure with a secret detail."))
                .AllowAnonymous();
            app.MapGet(UnannotatedPath, () => Results.Ok("signed-in"));
        }
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
