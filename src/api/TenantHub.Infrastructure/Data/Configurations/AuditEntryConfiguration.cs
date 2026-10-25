using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantHub.Domain;
using TenantHub.Domain.Audit;
using TenantHub.Domain.Organizations;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<AuditEntry> entry)
    {
        entry.ToTable("audit_entries");
        entry.HasKey(a => a.Id);
        entry.Property(a => a.Id).UseIdentityAlwaysColumn();

        entry.Property(a => a.ActorName).HasMaxLength(80);
        entry.Property(a => a.Action).HasMaxLength(64);
        entry.Property(a => a.EntityType).HasMaxLength(32);
        entry.Property(a => a.Summary).HasMaxLength(500);
        entry.Property(a => a.TraceId).HasMaxLength(64);

        // The changed fields as one jsonb document: read as a whole, never queried field by field.
        entry.Property(a => a.Changes)
            .HasColumnType("jsonb")
            .HasConversion(
                new ValueConverter<IReadOnlyList<FieldChange>, string>(
                    v => JsonSerializer.Serialize(v, Json),
                    v => JsonSerializer.Deserialize<List<FieldChange>>(v, Json) ?? new List<FieldChange>()),
                new ValueComparer<IReadOnlyList<FieldChange>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, change) => HashCode.Combine(hash, change.GetHashCode())),
                    v => v.ToList()));

        // The viewer pages newest first by keyset (occurred_at, id): with id in the index the next page is a
        // range scan at any depth (0.2 ms at 50,000 entries, D030). The second index serves an entity's history.
        entry.HasIndex(a => new { a.OrganizationId, a.OccurredAt, a.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_audit_entries_timeline");
        entry.HasIndex(a => new { a.OrganizationId, a.EntityType, a.EntityId });

        entry.HasOne<Organization>().WithMany().HasForeignKey(a => a.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}
