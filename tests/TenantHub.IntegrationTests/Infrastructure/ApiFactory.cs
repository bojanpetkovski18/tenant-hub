using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Api.Common;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Data;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// The real API in the <c>Testing</c> environment (production behaviour: JSON logs, no API explorer), pointed at a
/// database chosen by the test. Migrations at startup are off; databases come prepared from
/// <see cref="PostgresFixture"/>. Two test-only routes go through the real pipeline: <see cref="ThrowingPath"/>
/// throws, and <see cref="UnannotatedPath"/> has no authorization metadata of its own.
/// </summary>
public class ApiFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    public const string ThrowingPath = "/api/test/throw";
    public const string UnannotatedPath = "/api/test/unannotated";

    /// <summary>Tenant-scoped, needs <c>billing.manage</c> (Owner only): exercises the 403 path before real endpoints need it.</summary>
    public const string OwnerOnlyPattern = "/test/owner-only";

    /// <summary>Tenant-scoped; tries to write a membership of another organization, which the guard must stop.</summary>
    public const string IsolationViolationPattern = "/test/isolation-violation";

    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{DatabaseSetup.ConnectionStringName}", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Seed:Enabled", "false");
        builder.UseSetting("App:PublicUrl", "https://app.tenanthub.example/");
        // Raised so only the test that targets the limit can hit it.
        builder.UseSetting("RateLimiting:AuthPermitLimit", "1000");
        builder.UseSetting("RateLimiting:InvitationPermitLimit", "1000");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(testServices =>
        {
            testServices.AddSingleton<IEndpointMapper, TestEndpoints>();
            services?.Invoke(testServices);
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }

    private sealed class TestEndpoints : IEndpointMapper
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet(ThrowingPath, IResult () => throw new InvalidOperationException("Test failure with a secret detail."))
                .Public();
            // Classified, but with no authorization metadata of its own: only the fallback policy protects it.
            app.MapGet(UnannotatedPath, () => Results.Ok("signed-in")).UserScoped();

            var tenant = app.MapTenantGroup();
            tenant.MapGet(OwnerOnlyPattern, (ITenantContext context) => Results.Ok(new { context.OrganizationId }))
                .RequirePermission(Permissions.BillingManage);
            tenant.MapPost(IsolationViolationPattern, async (Guid targetOrganizationId, AppDbContext db) =>
                {
                    // A deliberate bug: an organization taken from input instead of the tenant context.
                    var target = await db.Organizations.SingleAsync(o => o.Id == targetOrganizationId);
                    db.Memberships.Add(target.AddMember(Guid.NewGuid(), Role.Admin, DateTimeOffset.UtcNow, null));
                    await db.SaveChangesAsync();
                    return Results.Ok();
                })
                .RequirePermission(Permissions.OrgRead);
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
