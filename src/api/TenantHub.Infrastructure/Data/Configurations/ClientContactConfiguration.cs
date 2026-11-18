using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class ClientContactConfiguration : IEntityTypeConfiguration<ClientContact>
{
    public const string ActiveEmailIndex = "ux_client_contacts_active_email";

    public void Configure(EntityTypeBuilder<ClientContact> contact)
    {
        contact.ToTable("client_contacts");
        contact.HasKey(c => c.Id);
        contact.Property(c => c.Id).ValueGeneratedNever();
        contact.Ignore(c => c.DomainEvents);
        contact.Ignore(c => c.IsActive);

        // The target of composite foreign keys from sign-in tokens and work requests: they point at a contact of their
        // own organization.
        contact.HasAlternateKey(c => new { c.OrganizationId, c.Id }).HasName("ak_client_contacts_organization_id_id");

        contact.Property(c => c.Name).HasMaxLength(ClientContact.NameMaxLength);
        contact.Property(c => c.Email).HasMaxLength(EmailAddress.MaxLength);
        contact.Property(c => c.NormalizedEmail).HasMaxLength(EmailAddress.MaxLength);

        // SPEC-V2 5.3: an email once among a client's active contacts; the domain checks first, this is the backstop.
        contact.HasIndex(c => new { c.OrganizationId, c.ClientId, c.NormalizedEmail })
            .IsUnique()
            .HasFilter("removed_at IS NULL")
            .HasDatabaseName(ActiveEmailIndex);

        // Sign-in looks a contact up by organization and email (SPEC-V2 5.3).
        contact.HasIndex(c => new { c.OrganizationId, c.NormalizedEmail }).HasDatabaseName("ix_client_contacts_organization_id_normalized_email");

        contact.HasOne<Organization>().WithMany().HasForeignKey(c => c.OrganizationId).OnDelete(DeleteBehavior.Cascade);

        // Deleting a client deletes its contacts (SPEC-V2 5.3); the composite key keeps a contact inside its organization.
        contact.HasOne<Client>().WithMany()
            .HasForeignKey(c => new { c.OrganizationId, c.ClientId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
        contact.HasOne<AppUser>().WithMany().HasForeignKey(c => c.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
