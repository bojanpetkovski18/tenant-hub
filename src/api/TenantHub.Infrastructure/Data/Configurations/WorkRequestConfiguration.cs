using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Data.Configurations;

internal sealed class WorkRequestConfiguration : IEntityTypeConfiguration<WorkRequest>
{
    public void Configure(EntityTypeBuilder<WorkRequest> request)
    {
        request.ToTable("work_requests");
        request.HasKey(r => r.Id);
        request.Property(r => r.Id).ValueGeneratedNever();
        request.Ignore(r => r.DomainEvents);

        request.Property(r => r.Title).HasMaxLength(WorkRequest.TitleMaxLength);
        request.Property(r => r.Description).HasMaxLength(WorkRequest.DescriptionMaxLength);
        request.Property(r => r.PreferredOn).HasColumnType("date");
        request.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        request.Property(r => r.DeclineReason).HasMaxLength(WorkRequest.ReasonMaxLength);

        // PostgreSQL's xmin system column as the concurrency token (D004).
        request.Property(r => r.Version).IsRowVersion();

        // The staff inbox lists by status, newest first; the portal by client.
        request.HasIndex(r => new { r.OrganizationId, r.Status, r.CreatedAt }).HasDatabaseName("ix_work_requests_status_created");

        request.HasOne<Organization>().WithMany().HasForeignKey(r => r.OrganizationId).OnDelete(DeleteBehavior.Cascade);

        // Composite foreign keys keep the client, contact and job inside the request's organization (SPEC 3.3, point 4).
        // A client's requests and contacts go with it; a converted request's job keeps the client from being deleted.
        request.HasOne<Client>().WithMany()
            .HasForeignKey(r => new { r.OrganizationId, r.ClientId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
        request.HasOne<ClientContact>().WithMany()
            .HasForeignKey(r => new { r.OrganizationId, r.ContactId })
            .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
        request.HasOne<Job>().WithMany()
            .HasForeignKey(r => new { r.OrganizationId, r.JobId })
            .HasPrincipalKey(j => new { j.OrganizationId, j.Id })
            .OnDelete(DeleteBehavior.NoAction);
        request.HasOne<AppUser>().WithMany().HasForeignKey(r => r.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
