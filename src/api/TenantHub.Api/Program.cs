using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using TenantHub.Api.Common;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Features.Account;
using TenantHub.Api.Features.Auth;
using TenantHub.Api.Features.Organizations;
using TenantHub.Application;
using TenantHub.Infrastructure;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Seed;

var builder = WebApplication.CreateBuilder(args);
builder.AddDevelopmentDotEnv();

builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
}
else
{
    builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApiProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddValidation();
// Enums travel as camelCase strings ("owner"), matching the SPA's types.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
builder.AddAppAuth();
builder.Services.AddAppRateLimiting(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

var app = builder.Build();

await DatabaseSetup.MigrateIfEnabledAsync(app.Services);
await DemoSeeder.SeedIfEnabledAsync(app.Services, app.Configuration);

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseAppAuth();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

// Liveness: the process answers. Readiness: the database answers too (used by Compose and the host, M11).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapOrganizationEndpoints();
foreach (var mapper in app.Services.GetServices<IEndpointMapper>())
{
    mapper.Map(app);
}

// Unmatched routes: 404 problem details. Without this endpoint the fallback authorization policy would answer
// 401 for any unknown URL. Serving the SPA's own routes comes with the production image (M11).
app.MapFallback("{**path}", () => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound))
    .AllowAnonymous()
    .DisableAntiforgery()
    .ExcludeFromDescription();

await app.RunAsync();

/// <summary>Entry point, public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
