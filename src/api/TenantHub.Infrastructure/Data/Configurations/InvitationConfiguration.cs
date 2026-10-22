using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain;
using TenantHub.Domain.Invitations;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public const string OpenEmailIndex = "ux_invitations_open_email";

    public void Configure(EntityTypeBuilder<Invitation> invitation)
    {
        invitation.ToTable("invitations");
        invitation.HasKey(i => i.Id);
        invitation.Property(i => i.Id).ValueGeneratedNever();
        invitation.Ignore(i => i.DomainEvents);

        invitation.Property(i => i.Email).HasMaxLength(EmailAddress.MaxLength);
        invitation.Property(i => i.NormalizedEmail).HasMaxLength(EmailAddress.MaxLength);
        invitation.Property(i => i.Role).HasConversion<string>().HasMaxLength(20);

        // Only the SHA-256 of the link's token is stored (D011); the link is found by its hash.
        invitation.Property(i => i.TokenHash).HasColumnType("bytea");
        invitation.HasIndex(i => i.TokenHash).IsUnique().HasDatabaseName("ux_invitations_token_hash");

        // One open (not accepted, not revoked) invitation per email and organization (SPEC 5.3).
        invitation.HasIndex(i => new { i.OrganizationId, i.NormalizedEmail })
            .IsUnique()
            .HasFilter("accepted_at IS NULL AND revoked_at IS NULL")
            .HasDatabaseName(OpenEmailIndex);

        invitation.HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        invitation.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        invitation.HasOne<AppUser>().WithMany().HasForeignKey(i => i.AcceptedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
