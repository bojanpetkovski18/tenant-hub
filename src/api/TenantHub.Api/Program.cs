using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using TenantHub.Api.Common;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Api.Features.Account;
using TenantHub.Api.Features.Audit;
using TenantHub.Api.Features.Auth;
using TenantHub.Api.Features.Clients;
using TenantHub.Api.Features.Demo;
using TenantHub.Api.Features.Invitations;
using TenantHub.Api.Features.Jobs;
using TenantHub.Api.Features.Organizations;
using TenantHub.Application;
using TenantHub.Infrastructure;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Seed;

if (args.Contains(HealthProbe.Argument))
{
    return await HealthProbe.RunAsync();
}

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
builder.Services.AddAppForwardedHeaders(builder.Configuration);
builder.Services.AddAppRateLimiting(builder.Configuration);
if (builder.Configuration.GetValue("DEMO_MODE", false))
{
    builder.Services.AddHostedService<DemoResetService>();
}

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

var app = builder.Build();

await DatabaseSetup.MigrateIfEnabledAsync(app.Services);
await DemoSeeder.SeedIfEnabledAsync(app.Services, app.Configuration);

app.UseAppForwardedHeaders();
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
    app.MapOpenApi().Public();
    app.MapScalarApiReference().Public();
}

// Liveness: the process answers. Readiness: the database answers too (used by Compose and the host, M11).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).Public();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).Public();

app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapOrganizationEndpoints();
app.MapClientEndpoints();
app.MapInvitationEndpoints();
app.MapJobEndpoints();
app.MapAuditEndpoints();
app.MapDashboardEndpoints();
app.MapDemoEndpoints();
foreach (var mapper in app.Services.GetServices<IEndpointMapper>())
{
    mapper.Map(app);
}

// The SPA's files from wwwroot, pre-compressed (Brotli, gzip) at publish. Only the production image has the build
// there (M11); in development the SPA has its own server. Public: scripts and styles load before anyone signs in.
if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
{
    app.MapStaticAssets().Public();
}

app.MapFallback("{**path}", SpaFallback.Handle)
    .Public()
    .DisableAntiforgery()
    .ExcludeFromDescription();

await app.RunAsync();
return 0;

/// <summary>Entry point, public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
