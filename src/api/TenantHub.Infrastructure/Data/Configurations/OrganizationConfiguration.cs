using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> organization)
    {
        organization.ToTable("organizations");
        organization.HasKey(o => o.Id);
        organization.Property(o => o.Id).ValueGeneratedNever();

        organization.Property(o => o.Name).HasMaxLength(Organization.NameMaxLength);
        organization.Property(o => o.Slug).HasMaxLength(OrganizationSlug.MaxLength);
        organization.HasIndex(o => o.Slug).IsUnique();
        organization.Property(o => o.Plan).HasConversion<string>().HasMaxLength(20);
        organization.Property(o => o.TimeZone).HasMaxLength(64);

        // PostgreSQL's xmin system column as the optimistic-concurrency token (D004); no extra column needed.
        organization.Property<uint>("xmin").IsRowVersion();

        organization.HasOne<AppUser>().WithMany().HasForeignKey(o => o.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
