using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Background;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> message)
    {
        message.ToTable("outbox_messages");
        message.HasKey(m => m.Id);
        message.Property(m => m.Id).ValueGeneratedNever();
        message.Property(m => m.Type).HasMaxLength(100);
        message.Property(m => m.Payload).HasColumnType("jsonb");
        message.Property(m => m.LastError).HasMaxLength(200);

        // The dispatcher's claim: oldest due first, among the messages still waiting.
        message.HasIndex(m => m.AvailableAt)
            .HasFilter("processed_at IS NULL AND dead_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_pending");

        // A message about an organization goes with it: it may name its people.
        message.HasOne<Organization>().WithMany().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScheduledTaskConfiguration : IEntityTypeConfiguration<ScheduledTask>
{
    public void Configure(EntityTypeBuilder<ScheduledTask> task)
    {
        task.ToTable("scheduled_tasks");
        task.HasKey(t => t.Id);
        task.Property(t => t.Id).ValueGeneratedNever();
        task.Property(t => t.Name).HasMaxLength(100);
        task.Property(t => t.LastError).HasMaxLength(200);

        // SPEC-V2 8 asks for the key (name, organization_id), but a primary key cannot hold the null of an
        // application-wide task. A unique index that treats nulls as equal gives the same guarantee (D039).
        task.HasIndex(t => new { t.Name, t.OrganizationId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_scheduled_tasks_name_organization");

        // The claim takes the earliest due row: with one row per organization that has agreements (M16), an index keeps
        // each claim an index scan instead of a scan of the whole table (measured in PROGRESS: 0.96 ms → 0.07 ms at 10,000 rows).
        task.HasIndex(t => t.DueAt).HasDatabaseName("ix_scheduled_tasks_due");

        task.HasOne<Organization>().WithMany().HasForeignKey(t => t.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}
