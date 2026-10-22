using System.ComponentModel.DataAnnotations;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Clients;
using TenantHub.Application.Paging;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Clients;

// Shape only; the client rules (SPEC 5.6) are checked by the domain, which reports every field at once.
public sealed record ClientRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(500)] string Name,
    [property: MaxLength(500)] string? ContactName,
    [property: MaxLength(500)] string? Email,
    [property: MaxLength(200)] string? Phone,
    [property: MaxLength(1000)] string? Address,
    [property: MaxLength(5000)] string? Notes)
{
    public ClientInput ToInput() => new(Name, ContactName, Email, Phone, Address, Notes);
}

/// <summary><see cref="Version"/> is the value the client last read; a newer one in the database means 409.</summary>
public sealed record UpdateClientRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(500)] string Name,
    [property: MaxLength(500)] string? ContactName,
    [property: MaxLength(500)] string? Email,
    [property: MaxLength(200)] string? Phone,
    [property: MaxLength(1000)] string? Address,
    [property: MaxLength(5000)] string? Notes,
    uint Version)
{
    public ClientInput ToInput() => new(Name, ContactName, Email, Phone, Address, Notes);
}

public static class ClientEndpoints
{
    public static IEndpointRouteBuilder MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var clients = app.MapTenantGroup().MapGroup("/clients").WithTags("Clients");

        clients.MapGet("/", async (string? q, string? status, string? sort, int? page, int? pageSize, ListClientsQuery query, CancellationToken ct) =>
            {
                var filter = Enum.TryParse<ClientStatusFilter>(status, ignoreCase: true, out var parsed) ? parsed : ClientStatusFilter.Active;
                return TypedResults.Ok(await query.HandleAsync(new ListClientsRequest(q, filter, sort, PageRequest.From(page, pageSize)), ct));
            })
            .RequirePermission(Permissions.ClientsRead);

        clients.MapPost("/", async (ClientRequest request, HttpContext context, ClientCommands commands, CancellationToken ct) =>
            {
                var client = await commands.CreateAsync(request.ToInput(), ct);
                return TypedResults.Created($"{context.Request.Path.Value!.TrimEnd('/')}/{client.Id}", client);
            })
            .RequirePermission(Permissions.ClientsWrite);

        clients.MapGet("/{id:guid}", async (Guid id, GetClientQuery query, CancellationToken ct) =>
                TypedResults.Ok(await query.HandleAsync(id, ct)))
            .RequirePermission(Permissions.ClientsRead);

        clients.MapPut("/{id:guid}", async (Guid id, UpdateClientRequest request, ClientCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.UpdateAsync(id, request.Version, request.ToInput(), ct)))
            .RequirePermission(Permissions.ClientsWrite);

        clients.MapPost("/{id:guid}/archive", async (Guid id, ClientCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ArchiveAsync(id, ct)))
            .RequirePermission(Permissions.ClientsWrite);

        clients.MapPost("/{id:guid}/restore", async (Guid id, ClientCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.RestoreAsync(id, ct)))
            .RequirePermission(Permissions.ClientsWrite);

        clients.MapDelete("/{id:guid}", async (Guid id, ClientCommands commands, CancellationToken ct) =>
            {
                await commands.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.ClientsDelete);

        return app;
    }
}
