using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Organizations;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public const string ActiveNameIndex = "ux_clients_active_name";

    public void Configure(EntityTypeBuilder<Client> client)
    {
        client.ToTable("clients");
        client.HasKey(c => c.Id);
        client.Property(c => c.Id).ValueGeneratedNever();
        client.Ignore(c => c.Details);
        client.Ignore(c => c.DomainEvents);

        // The target of composite foreign keys (jobs → clients, M06): a row can only point at a client of its own
        // organization, enforced by the database (SPEC 3.3, point 4).
        client.HasAlternateKey(c => new { c.OrganizationId, c.Id }).HasName("ak_clients_organization_id_id");

        // citext: names compare, sort and stay unique case-insensitively without lower() expressions.
        client.Property(c => c.Name).HasColumnType("citext");
        client.HasIndex(c => new { c.OrganizationId, c.Name })
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName(ActiveNameIndex);

        client.Property(c => c.ContactName).HasMaxLength(ClientDetails.ContactNameMaxLength);
        client.Property(c => c.Email).HasMaxLength(ClientDetails.EmailMaxLength);
        client.Property(c => c.Phone).HasMaxLength(ClientDetails.PhoneMaxLength);
        client.Property(c => c.Address).HasMaxLength(ClientDetails.AddressMaxLength);
        client.Property(c => c.Notes).HasMaxLength(ClientDetails.NotesMaxLength);
        client.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);

        // PostgreSQL's xmin system column as the concurrency token (D004).
        client.Property(c => c.Version).IsRowVersion();

        client.HasOne<Organization>().WithMany().HasForeignKey(c => c.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}
