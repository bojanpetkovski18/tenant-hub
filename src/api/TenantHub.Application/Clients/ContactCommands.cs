using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Accounts;
using TenantHub.Application.Organizations;
using TenantHub.Application.Portal;
using TenantHub.Domain;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Clients;

public sealed record ContactView(Guid Id, Guid ClientId, string Name, string Email, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

/// <summary>
/// A client's portal contacts (US-80, SPEC-V2 5.3). The portal is a plan feature, so every call checks it first
/// (403 <c>plan-feature-unavailable</c>). The client is loaded through the tenant filter: another organization's
/// client is "not found".
/// </summary>
public sealed class ContactCommands(
    IAppDbContext db,
    ITenantContext tenant,
    IDatabaseLocks locks,
    IDatabaseErrors errors,
    PlanLimitsCheck limits,
    IOutbox outbox,
    IAppLinks links,
    EmailVerification verification,
    TimeProvider time)
{
    public const string ActiveEmailIndex = "ux_client_contacts_active_email";

    public async Task<IReadOnlyList<ContactView>> ListAsync(Guid clientId, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Portal, cancellationToken);
        if (!await db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken))
        {
            throw ClientErrors.NotFound();
        }

        return await db.ClientContacts
            .AsNoTracking()
            .Where(c => c.ClientId == clientId && c.RemovedAt == null)
            .OrderBy(c => c.Name)
            .Select(c => new ContactView(c.Id, c.ClientId, c.Name, c.Email, c.CreatedAt, c.LastSignInAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Adds a contact and emails them where the portal is; the email leaves through the outbox with the change.</summary>
    public async Task<ContactView> AddAsync(Guid clientId, string? name, string? email, CancellationToken cancellationToken)
    {
        // Sends mail on the organization's behalf, so it needs a proven address, like an invitation (SPEC-V2 4.1).
        await verification.EnsureVerifiedAsync(tenant.UserId!.Value, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serializes additions in the organization, so the five-contact limit and the email check cannot race.
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);
        await limits.EnsureFeatureAsync(PlanFeature.Portal, cancellationToken);
        var client = await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken) ?? throw ClientErrors.NotFound();

        var active = db.ClientContacts.Where(c => c.ClientId == clientId && c.RemovedAt == null);
        var normalized = EmailAddress.Normalize(email ?? "");
        var contact = ClientContact.Add(
            client,
            name,
            email,
            await active.CountAsync(cancellationToken),
            await active.AnyAsync(c => c.NormalizedEmail == normalized, cancellationToken),
            tenant.UserId!.Value,
            time.GetUtcNow());
        db.ClientContacts.Add(contact);

        var organization = await db.Organizations.AsNoTracking()
            .Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new { o.Name, o.Slug })
            .SingleAsync(cancellationToken);
        outbox.Add(
            PortalEmails.Invitation,
            PortalEmails.BuildInvitation(contact.Email, contact.Name, organization.Name, client.Name, links.Portal(organization.Slug)));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (errors.IsUniqueViolation(exception, ActiveEmailIndex))
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["email"] = ["This client already has a contact with this email address."] });
        }

        await transaction.CommitAsync(cancellationToken);
        return ToView(contact);
    }

    /// <summary>Removes a contact; their portal session ends on its next request (SPEC-V2 3.2).</summary>
    public async Task RemoveAsync(Guid contactId, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Portal, cancellationToken);
        var contact = await db.ClientContacts.SingleOrDefaultAsync(c => c.Id == contactId && c.RemovedAt == null, cancellationToken)
            ?? throw ContactNotFound();
        var clientName = await db.Clients.Where(c => c.Id == contact.ClientId).Select(c => c.Name).SingleAsync(cancellationToken);
        contact.Remove(clientName, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public static DomainException ContactNotFound() => new("contact-not-found", "Contact not found.", DomainErrorKind.NotFound);

    private static ContactView ToView(ClientContact c) => new(c.Id, c.ClientId, c.Name, c.Email, c.CreatedAt, c.LastSignInAt);
}
