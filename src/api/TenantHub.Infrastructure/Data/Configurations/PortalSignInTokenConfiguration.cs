using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Organizations;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class PortalSignInTokenConfiguration : IEntityTypeConfiguration<PortalSignInToken>
{
    public void Configure(EntityTypeBuilder<PortalSignInToken> token)
    {
        token.ToTable("portal_sign_in_tokens");
        token.HasKey(t => t.Id);
        token.Property(t => t.Id).ValueGeneratedNever();

        // Looked up by hash inside the organization the link names (SPEC-V2 5.3); unique, like invitation tokens.
        token.Property(t => t.TokenHash).HasMaxLength(32);
        token.HasIndex(t => new { t.OrganizationId, t.TokenHash }).IsUnique().HasDatabaseName("ux_portal_sign_in_tokens_token_hash");

        // The per-contact cooldown reads the newest link of a contact.
        token.HasIndex(t => new { t.OrganizationId, t.ContactId, t.CreatedAt }).HasDatabaseName("ix_portal_sign_in_tokens_contact_created");

        token.HasOne<Organization>().WithMany().HasForeignKey(t => t.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        token.HasOne<ClientContact>().WithMany()
            .HasForeignKey(t => new { t.OrganizationId, t.ContactId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
