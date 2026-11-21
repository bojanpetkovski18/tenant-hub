using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Notifications;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

/// <summary>Notifications (SPEC-V2 8): addressed to a member of the organization, so they go with the membership.</summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void Configure(EntityTypeBuilder<Notification> notification)
    {
        notification.ToTable("notifications");
        notification.HasKey(n => n.Id);
        notification.Property(n => n.Id).ValueGeneratedNever();
        notification.Property(n => n.Type).HasConversion<string>().HasMaxLength(30);

        // What it says and where it leads, as one jsonb document (SPEC-V2 8 "data").
        notification.Property(n => n.Content)
            .HasColumnName("data")
            .HasColumnType("jsonb")
            .HasConversion(
                new ValueConverter<NotificationContent, string>(
                    c => JsonSerializer.Serialize(c, Json),
                    json => JsonSerializer.Deserialize<NotificationContent>(json, Json)!),
                new ValueComparer<NotificationContent>((a, b) => a!.Equals(b), c => c.GetHashCode(), c => c));

        // The bell and the list: one person's, newest first (SPEC-V2 8).
        notification.HasIndex(n => new { n.OrganizationId, n.UserId, n.CreatedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_notifications_user_created");

        // A message or digest run notifies each person once, however often it is delivered.
        notification.HasIndex(n => new { n.OrganizationId, n.SourceId, n.UserId }).IsUnique().HasDatabaseName("ux_notifications_source_user");

        notification.HasOne<Organization>().WithMany().HasForeignKey(n => n.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        notification.HasOne<Membership>().WithMany()
            .HasForeignKey(n => new { n.OrganizationId, n.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Email choices (SPEC-V2 8): one row per person and type, kept with the account and gone with it.</summary>
internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> preference)
    {
        preference.ToTable("notification_preferences");
        preference.HasKey(p => new { p.UserId, p.Type });
        preference.Property(p => p.Type).HasConversion<string>().HasMaxLength(30);
        preference.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
