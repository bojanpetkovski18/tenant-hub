using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

/// <summary>Checklist items (SPEC-V2 8 <c>checklist_items</c>): part of the job, deleted with it.</summary>
internal sealed class ChecklistItemConfiguration : IEntityTypeConfiguration<ChecklistItem>
{
    public void Configure(EntityTypeBuilder<ChecklistItem> item)
    {
        item.ToTable("checklist_items");
        item.HasKey(i => i.Id);
        item.Property(i => i.Id).ValueGeneratedNever();
        item.Property(i => i.Text).HasMaxLength(ChecklistEntry.TextMaxLength);

        // Tenant-first, and the order the checklist is read in.
        item.HasIndex(i => new { i.OrganizationId, i.JobId, i.Position }).HasDatabaseName("ix_checklist_items_job");

        item.HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        item.HasOne<AppUser>().WithMany().HasForeignKey(i => i.DoneByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Photos (SPEC-V2 8 <c>attachments</c>): the row holds the keys of the two files, which live in the file store (D043).
/// The rows go with their job by cascade; the files are removed through the outbox.
/// </summary>
internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> attachment)
    {
        attachment.ToTable("attachments");
        attachment.HasKey(a => a.Id);
        attachment.Property(a => a.Id).ValueGeneratedNever();
        attachment.Property(a => a.StorageKey).HasMaxLength(100);
        attachment.Property(a => a.ThumbnailKey).HasMaxLength(100);
        attachment.Property(a => a.ContentType).HasMaxLength(50);
        attachment.HasIndex(a => a.StorageKey).IsUnique().HasDatabaseName("ux_attachments_storage_key");

        // Tenant-first: a job's photos, and the storage sum over the organization.
        attachment.HasIndex(a => new { a.OrganizationId, a.JobId, a.CreatedAt }).HasDatabaseName("ix_attachments_job");

        attachment.HasOne<Organization>().WithMany().HasForeignKey(a => a.OrganizationId).OnDelete(DeleteBehavior.Cascade);

        // A composite key keeps the photo inside its job's organization (SPEC 3.3, point 4).
        attachment.HasOne<Job>().WithMany()
            .HasForeignKey(a => new { a.OrganizationId, a.JobId })
            .HasPrincipalKey(j => new { j.OrganizationId, j.Id })
            .OnDelete(DeleteBehavior.Cascade);
        attachment.HasOne<AppUser>().WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
