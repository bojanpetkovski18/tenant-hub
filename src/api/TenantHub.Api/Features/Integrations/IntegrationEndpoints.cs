using System.ComponentModel.DataAnnotations;
using Microsoft.Net.Http.Headers;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Exports;
using TenantHub.Application.Integrations;
using TenantHub.Domain.Integrations;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Integrations;

public sealed record CreateApiKeyRequest([property: MaxLength(200)] string? Name, ApiKeyAccess? Access, DateTimeOffset? ExpiresAt);

public sealed record WebhookRequest([property: MaxLength(1000)] string? Url, [property: MaxLength(20)] string[]? Events, bool Enabled = true, uint? Version = null)
{
    public WebhookInput ToInput() => new(Url, Events, Enabled, Version);
}

/// <summary>API keys and webhooks (SPEC-V2 7): Settings → Integrations, for those with <c>integrations.manage</c>.</summary>
public static class IntegrationEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var keys = app.MapTenantGroup().MapGroup("/api-keys").WithTags("Integrations");

        keys.MapGet("/", async (ApiKeyCommands commands, CancellationToken ct) => TypedResults.Ok(await commands.ListAsync(ct)))
            .RequirePermission(Permissions.IntegrationsManage);

        // The answer is the only time the key is shown (SPEC-V2 5.8); responses are never cached (no-store on /api).
        keys.MapPost("/", async (CreateApiKeyRequest request, ApiKeyCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.CreateAsync(request.Name, request.Access, request.ExpiresAt, ct)))
            .RequirePermission(Permissions.IntegrationsManage);

        keys.MapDelete("/{id:guid}", async (Guid id, ApiKeyCommands commands, CancellationToken ct) =>
            {
                await commands.RevokeAsync(id, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.IntegrationsManage);

        var webhooks = app.MapTenantGroup().MapGroup("/webhooks").WithTags("Integrations");

        webhooks.MapGet("/", async (WebhookCommands commands, CancellationToken ct) => TypedResults.Ok(await commands.ListAsync(ct)))
            .RequirePermission(Permissions.IntegrationsManage);

        webhooks.MapPost("/", async (WebhookRequest request, WebhookCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.CreateAsync(request.ToInput(), ct)))
            .RequirePermission(Permissions.IntegrationsManage);

        webhooks.MapPut("/{id:guid}", async (Guid id, WebhookRequest request, WebhookCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.UpdateAsync(id, request.ToInput(), ct)))
            .RequirePermission(Permissions.IntegrationsManage);

        webhooks.MapDelete("/{id:guid}", async (Guid id, WebhookCommands commands, CancellationToken ct) =>
            {
                await commands.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.IntegrationsManage);

        webhooks.MapPost("/{id:guid}/rotate-secret", async (Guid id, WebhookCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.RotateSecretAsync(id, ct)))
            .RequirePermission(Permissions.IntegrationsManage);

        webhooks.MapGet("/{id:guid}/deliveries", async (Guid id, int? page, int? pageSize, WebhookCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.DeliveriesAsync(id, page, pageSize, ct)))
            .RequirePermission(Permissions.IntegrationsManage);

        webhooks.MapPost("/{id:guid}/deliveries/{deliveryId:guid}/redeliver", async (Guid id, Guid deliveryId, WebhookCommands commands, CancellationToken ct) =>
            {
                await commands.RedeliverAsync(id, deliveryId, ct);
                return TypedResults.Accepted((string?)null);
            })
            .RequirePermission(Permissions.IntegrationsManage);

        // "Send a test event" (US-111): a webhook.ping through the same signing, guard and log.
        webhooks.MapPost("/{id:guid}/test", async (Guid id, WebhookCommands commands, CancellationToken ct) =>
            {
                await commands.SendTestAsync(id, ct);
                return TypedResults.Accepted((string?)null);
            })
            .RequirePermission(Permissions.IntegrationsManage);

        return app;
    }
}

/// <summary>
/// A CSV download, streamed (SPEC-V2 5.8): rows go to the response as the database returns them, through a buffered
/// writer, so memory stays flat however long the export is. The export was checked before this runs; a failure after
/// the first bytes can only abort the response.
/// </summary>
public sealed class CsvResult(CsvExport export) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.ContentType = CsvWriter.ContentType;
        response.Headers.CacheControl = "no-store";
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(export.FileName);
        response.Headers.ContentDisposition = disposition.ToString();

        await using var writer = new StreamWriter(response.Body, CsvWriter.Utf8WithBom, bufferSize: 16 * 1024, leaveOpen: true);
        await export.WriteAsync(writer, httpContext.RequestAborted);
        await writer.FlushAsync(httpContext.RequestAborted);
    }
}
