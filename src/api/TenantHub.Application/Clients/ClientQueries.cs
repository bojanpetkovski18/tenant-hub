using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Paging;
using TenantHub.Domain;
using TenantHub.Domain.Clients;

namespace TenantHub.Application.Clients;

public sealed record ClientView(
    Guid Id, string Name, string? ContactName, string? Email, string? Phone, string? Address, string? Notes,
    ClientStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version)
{
    public static readonly Expression<Func<Client, ClientView>> FromClient = c =>
        new ClientView(c.Id, c.Name, c.ContactName, c.Email, c.Phone, c.Address, c.Notes, c.Status, c.CreatedAt, c.UpdatedAt, c.Version);
}

public sealed record ClientListItem(Guid Id, string Name, string? ContactName, string? Email, string? Phone, ClientStatus Status, DateTimeOffset UpdatedAt);

public enum ClientStatusFilter
{
    Active,
    Archived,
    All,
}

public sealed record ListClientsRequest(string? Search, ClientStatusFilter Status, string? Sort, PageRequest Paging);

/// <summary>
/// The clients list (US-20): search, status filter, sort and paging, all in SQL. Sorting comes from a fixed
/// allow-list; an unknown value falls back to the default rather than failing, so a hand-edited URL still works.
/// </summary>
public sealed class ListClientsQuery(IAppDbContext db)
{
    public const string DefaultSort = "name";

    public static readonly IReadOnlyList<string> Sorts = ["name", "-name", "updated", "-updated", "created", "-created"];

    public async Task<PagedList<ClientListItem>> HandleAsync(ListClientsRequest request, CancellationToken cancellationToken)
    {
        var clients = db.Clients.AsNoTracking();

        clients = request.Status switch
        {
            ClientStatusFilter.Active => clients.Where(c => c.Status == ClientStatus.Active),
            ClientStatusFilter.Archived => clients.Where(c => c.Status == ClientStatus.Archived),
            _ => clients,
        };

        if (request.Search?.Trim() is { Length: > 0 } search)
        {
            // Parameterized; lower() on both sides so the text columns match case-insensitively like the name.
            var term = search.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(...) and strpos; never runs as .NET string code.
            clients = clients.Where(c =>
                c.Name.ToLower().Contains(term)
                || (c.ContactName != null && c.ContactName.ToLower().Contains(term))
                || (c.Email != null && c.Email.ToLower().Contains(term)));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await clients.CountAsync(cancellationToken);
        var sorted = (Sorts.Contains(request.Sort) ? request.Sort : DefaultSort) switch
        {
            "-name" => clients.OrderByDescending(c => c.Name).ThenByDescending(c => c.Id),
            "updated" => clients.OrderBy(c => c.UpdatedAt).ThenBy(c => c.Id),
            "-updated" => clients.OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id),
            "created" => clients.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id),
            "-created" => clients.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id),
            _ => clients.OrderBy(c => c.Name).ThenBy(c => c.Id),
        };

        var items = await sorted
            .Skip(request.Paging.Skip)
            .Take(request.Paging.PageSize)
            .Select(c => new ClientListItem(c.Id, c.Name, c.ContactName, c.Email, c.Phone, c.Status, c.UpdatedAt))
            .ToListAsync(cancellationToken);

        return new PagedList<ClientListItem>(items, request.Paging.Page, request.Paging.PageSize, total);
    }
}

public sealed class GetClientQuery(IAppDbContext db)
{
    public async Task<ClientView> HandleAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Clients.AsNoTracking().Where(c => c.Id == id).Select(ClientView.FromClient).SingleOrDefaultAsync(cancellationToken)
            ?? throw ClientErrors.NotFound();
}

internal static class ClientErrors
{
    public static DomainException NotFound() =>
        new("client-not-found", "Client not found.", DomainErrorKind.NotFound);

    public static DomainException HasJobs() =>
        new("client-has-jobs", "This client has jobs, so it cannot be deleted. Archive it instead.", DomainErrorKind.Conflict);

    public static DomainException NameTaken() =>
        new("client-name-taken", "Another active client already has this name.", DomainErrorKind.Conflict);

    public static DomainException ConcurrencyConflict() =>
        new("concurrency-conflict", "Someone else changed this client after you opened it. Reload to see their changes.", DomainErrorKind.Conflict);
}
