using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public const string OccurrenceIndex = "ux_jobs_agreement_occurrence";

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

        // Chosen from EXPLAIN ANALYZE on 10,000 jobs in one organization (PROGRESS, Measured results; D029):
        // the due-date sort reads this index instead of sorting every row (4.1 ms → 0.24 ms), and the partial index
        // answers "open" and "overdue" counts (dashboard, open-job plan limit) from the index alone (1.8 ms → 0.4 ms).
        // The default list and most filters already use ux_jobs_number backwards; the foreign keys index
        // (organization_id, client_id) and (organization_id, assignee_user_id).
        job.HasIndex(j => new { j.OrganizationId, j.DueOn, j.Number }).HasDatabaseName("ix_jobs_due");
        job.HasIndex(j => new { j.OrganizationId, j.DueOn })
            .HasFilter("status <> 'Completed' AND status <> 'Cancelled'")
            .HasDatabaseName("ix_jobs_open_due");

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

        // The quote's approval (SPEC-V2 5.4). The deciding contact is one of the job's organization (composite key);
        // contacts are never deleted while their client has jobs, so NO ACTION never blocks anything but a bug.
        job.Property(j => j.ApprovalStatus).HasConversion<string>().HasMaxLength(20);
        job.Property(j => j.ApprovedAmount).HasColumnType("numeric(12,2)");
        job.Property(j => j.ApprovalComment).HasMaxLength(JobQuotes.CommentMaxLength);
        job.HasOne<AppUser>().WithMany().HasForeignKey(j => j.ApprovalSentByUserId).OnDelete(DeleteBehavior.Restrict);
        job.HasOne<ClientContact>().WithMany()
            .HasForeignKey(j => new { j.OrganizationId, j.ApprovalDecidedByContactId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.NoAction);

        // Visits of agreements (SPEC-V2 8): each occurrence once per agreement, whatever runs generation and however
        // often (the backstop under the organization lock, D041). Also the index of the composite foreign key.
        job.Property(j => j.OccurrenceOn).HasColumnType("date");
        job.HasIndex(j => new { j.OrganizationId, j.AgreementId, j.OccurrenceOn })
            .IsUnique()
            .HasFilter("agreement_id IS NOT NULL")
            .HasDatabaseName(OccurrenceIndex);
        job.HasOne<Agreement>().WithMany()
            .HasForeignKey(j => new { j.OrganizationId, j.AgreementId })
            .HasPrincipalKey(a => new { a.OrganizationId, a.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}
