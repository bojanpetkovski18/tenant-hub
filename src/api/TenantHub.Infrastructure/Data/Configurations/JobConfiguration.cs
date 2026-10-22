using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> job)
    {
        job.ToTable("jobs");
        job.HasKey(j => j.Id);
        job.Property(j => j.Id).ValueGeneratedNever();
        job.Ignore(j => j.Label);
        job.Ignore(j => j.Details);
        job.Ignore(j => j.DomainEvents);

        job.Property(j => j.Title).HasMaxLength(JobDetails.TitleMaxLength);
        job.Property(j => j.Description).HasMaxLength(JobDetails.DescriptionMaxLength);
        job.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
        job.Property(j => j.Priority).HasConversion<string>().HasMaxLength(20);
        job.Property(j => j.ScheduledOn).HasColumnType("date");
        job.Property(j => j.DueOn).HasColumnType("date");
        job.Property(j => j.QuotedAmount).HasColumnType("numeric(12,2)");

        // PostgreSQL's xmin system column as the concurrency token (D004).
        job.Property(j => j.Version).IsRowVersion();

        // Unique per organization; also the tenant-first index every job query starts with.
        job.HasIndex(j => new { j.OrganizationId, j.Number }).IsUnique().HasDatabaseName("ux_jobs_number");

        job.HasOne<Organization>().WithMany().HasForeignKey(j => j.OrganizationId).OnDelete(DeleteBehavior.Cascade);

        // Composite foreign keys: a job can only point at a client and an assignee of its own organization, enforced
        // by the database whatever the application does (SPEC 3.3, point 4). NO ACTION rather than RESTRICT, so that
        // deleting the organization can cascade through jobs, clients and memberships in one statement.
        job.HasOne<Client>().WithMany()
            .HasForeignKey(j => new { j.OrganizationId, j.ClientId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.NoAction);
        // The memberships primary key is (organization_id, user_id), so no alternate key is needed on that side.
        job.HasOne<Membership>().WithMany()
            .HasForeignKey(j => new { j.OrganizationId, j.AssigneeUserId })
            .OnDelete(DeleteBehavior.NoAction);
        job.HasOne<AppUser>().WithMany().HasForeignKey(j => j.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
