using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the API in memory in the <c>Testing</c> environment, so production-only behaviour (JSON logs, no API
/// explorer) is what the tests see. <see cref="ThrowingPath"/> is an extra route that throws, to test the
/// unhandled-exception response.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ThrowingPath = "/test/throw";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
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
