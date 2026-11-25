using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Integrations;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

/// <summary>API keys (SPEC-V2 8): the hash, never the key.</summary>
internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> key)
    {
        key.ToTable("api_keys");
        key.HasKey(k => k.Id);
        key.Property(k => k.Id).ValueGeneratedNever();
        key.Ignore(k => k.DomainEvents);
        key.Ignore(k => k.ActorName);

        key.Property(k => k.Name).HasMaxLength(ApiKey.NameMaxLength);
        key.Property(k => k.Prefix).HasMaxLength(12);
        key.Property(k => k.KeyHash).HasMaxLength(32);
        key.Property(k => k.Access).HasConversion<string>().HasMaxLength(20);

        // A presented key is found by its hash, inside the organization the route names (SPEC-V2 3.3).
        key.HasIndex(k => k.KeyHash).IsUnique().HasDatabaseName("ux_api_keys_key_hash");
        key.HasIndex(k => new { k.OrganizationId, k.CreatedAt }).HasDatabaseName("ix_api_keys_organization_created");

        key.HasOne<Organization>().WithMany().HasForeignKey(k => k.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        key.HasOne<AppUser>().WithMany().HasForeignKey(k => k.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Webhook endpoints (SPEC-V2 8): the secret encrypted, the events as a text array.</summary>
internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> endpoint)
    {
        endpoint.ToTable("webhook_endpoints");
        endpoint.HasKey(e => e.Id);
        endpoint.Property(e => e.Id).ValueGeneratedNever();
        endpoint.Ignore(e => e.DomainEvents);
        endpoint.Ignore(e => e.IsEnabled);
        endpoint.Ignore(e => e.Host);

        // The target of the deliveries' composite foreign key: a delivery belongs to an endpoint of its organization.
        endpoint.HasAlternateKey(e => new { e.OrganizationId, e.Id }).HasName("ak_webhook_endpoints_organization_id_id");

        endpoint.Property(e => e.Url).HasMaxLength(WebhookEndpoint.UrlMaxLength);
        endpoint.Property(e => e.Events).HasColumnType("text[]");

        // PostgreSQL's xmin system column as the concurrency token (D004).
        endpoint.Property(e => e.Version).IsRowVersion();

        endpoint.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>The delivery log (SPEC-V2 8): one row per attempt, kept 30 days, gone with its endpoint.</summary>
internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> delivery)
    {
        delivery.ToTable("webhook_deliveries");
        delivery.HasKey(d => d.Id);
        delivery.Property(d => d.Id).ValueGeneratedNever();
        delivery.Property(d => d.EventType).HasMaxLength(40);
        delivery.Property(d => d.Error).HasMaxLength(WebhookDelivery.ErrorMaxLength);

        // An endpoint's log, newest first.
        delivery.HasIndex(d => new { d.OrganizationId, d.EndpointId, d.CreatedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_webhook_deliveries_endpoint_created");
        delivery.HasIndex(d => new { d.OrganizationId, d.CreatedAt }).HasDatabaseName("ix_webhook_deliveries_organization_created");

        delivery.HasOne<Organization>().WithMany().HasForeignKey(d => d.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        delivery.HasOne<WebhookEndpoint>().WithMany()
            .HasForeignKey(d => new { d.OrganizationId, d.EndpointId })
            .HasPrincipalKey(e => new { e.OrganizationId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
