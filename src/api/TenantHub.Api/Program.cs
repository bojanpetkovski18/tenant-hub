using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using TenantHub.Api.Common;
using TenantHub.Infrastructure.Data;

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
builder.Services.AddOpenApi();
builder.Services.AddAppDatabase(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

var app = builder.Build();

await DatabaseSetup.MigrateIfEnabledAsync(app.Services);

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Liveness: the process answers. Readiness: the database answers too (used by Compose and the host, M11).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync();

/// <summary>Entry point, public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
