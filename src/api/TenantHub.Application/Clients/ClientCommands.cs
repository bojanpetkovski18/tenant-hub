using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Clients;

namespace TenantHub.Application.Clients;

public sealed record ClientInput(string? Name, string? ContactName, string? Email, string? Phone, string? Address, string? Notes)
{
    public ClientDetails ToDetails() => ClientDetails.Validate(Name, ContactName, Email, Phone, Address, Notes);
}

/// <summary>
/// Client commands (US-20). Each loads the client through the tenant filter (another organization's id is simply
/// "not found"), applies one domain method and saves once; the audit entry is written in the same save (D012).
/// Unique-name and concurrency conflicts are read from the database's answer, which is race-free.
/// </summary>
public sealed class ClientCommands(IAppDbContext db, ITenantContext tenant, IDatabaseErrors errors, TimeProvider time)
{
    public const string ActiveNameIndex = "ux_clients_active_name";

    public async Task<ClientView> CreateAsync(ClientInput input, CancellationToken cancellationToken)
    {
        var client = Client.Create(tenant.OrganizationId!.Value, input.ToDetails(), time.GetUtcNow());
        db.Clients.Add(client);
        await SaveAsync(cancellationToken);
        return ToView(client);
    }

    public async Task<ClientView> UpdateAsync(Guid id, uint version, ClientInput input, CancellationToken cancellationToken)
    {
        var client = await LoadAsync(id, cancellationToken);
        // The version the user saw: if the row changed since, the update matches nothing and EF reports a conflict.
        db.Entry(client).Property(c => c.Version).OriginalValue = version;
        if (client.Update(input.ToDetails(), time.GetUtcNow()))
        {
            await SaveAsync(cancellationToken);
        }
        else if (client.Version != version)
        {
            throw ClientErrors.ConcurrencyConflict();
        }

        return ToView(client);
    }

    public async Task<ClientView> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var client = await LoadAsync(id, cancellationToken);
        client.Archive(time.GetUtcNow());
        await SaveAsync(cancellationToken);
        return ToView(client);
    }

    public async Task<ClientView> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var client = await LoadAsync(id, cancellationToken);
        client.Restore(time.GetUtcNow());
        await SaveAsync(cancellationToken);
        return ToView(client);
    }

    /// <summary>Clients with jobs are archived instead (SPEC 5.6); that check arrives with jobs in M06.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var client = await LoadAsync(id, cancellationToken);
        client.MarkDeleted();
        db.Clients.Remove(client);
        await SaveAsync(cancellationToken);
    }

    private async Task<Client> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Clients.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw ClientErrors.NotFound();

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ClientErrors.ConcurrencyConflict();
        }
        catch (DbUpdateException exception) when (errors.IsUniqueViolation(exception, ActiveNameIndex))
        {
            throw ClientErrors.NameTaken();
        }
    }

    private static ClientView ToView(Client c) =>
        new(c.Id, c.Name, c.ContactName, c.Email, c.Phone, c.Address, c.Notes, c.Status, c.CreatedAt, c.UpdatedAt, c.Version);
}
