using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> membership)
    {
        membership.ToTable("memberships");
        // The organization comes first, so the key also serves every "members of this organization" query.
        membership.HasKey(m => new { m.OrganizationId, m.UserId });
        membership.HasIndex(m => m.UserId);
        membership.Ignore(m => m.IsActive);
        membership.Ignore(m => m.DomainEvents);
        membership.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);

        membership.HasOne<Organization>().WithMany().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        membership.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        membership.HasOne<AppUser>().WithMany().HasForeignKey(m => m.InvitedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
