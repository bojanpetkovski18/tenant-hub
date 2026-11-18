using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Jobs;
using TenantHub.Application.Organizations;
using TenantHub.Application.Paging;
using TenantHub.Domain;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Clients;

public sealed record WorkRequestSummary(
    Guid Id,
    Guid ClientId,
    string ClientName,
    string ContactName,
    string Title,
    string? Description,
    DateOnly? PreferredOn,
    WorkRequestStatus Status,
    DateTimeOffset CreatedAt,
    Guid? JobId,
    string? JobLabel,
    string? DeclineReason);

/// <summary>
/// The staff side of work requests (US-84, SPEC-V2 5.5): the Requests inbox with an open count, converting a request
/// into a job (the job form, prefilled, saved together with the conversion and limit-checked like any new job), and
/// declining with a reason. All of it is part of the portal feature.
/// </summary>
public sealed class WorkRequestCommands(
    IAppDbContext db,
    ITenantContext tenant,
    PlanLimitsCheck limits,
    JobCommands jobs,
    TimeProvider time)
{
    public const string NotFoundCode = "request-not-found";

    public async Task<PagedList<WorkRequestSummary>> ListAsync(string? status, Guid? clientId, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Portal, cancellationToken);
        var paging = PageRequest.From(page, pageSize);
        var query = db.WorkRequests.AsNoTracking();
        if (Enum.TryParse<WorkRequestStatus>(status, ignoreCase: true, out var wanted))
        {
            query = query.Where(r => r.Status == wanted);
        }

        if (clientId is { } client)
        {
            query = query.Where(r => r.ClientId == client);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await (from r in query
                          join c in db.Clients on r.ClientId equals c.Id
                          join contact in db.ClientContacts on r.ContactId equals contact.Id
                          join j in db.Jobs on r.JobId equals (Guid?)j.Id into linked
                          from j in linked.DefaultIfEmpty()
                          orderby r.Status == WorkRequestStatus.Open ? 0 : 1, r.CreatedAt descending
                          select new { Request = r, Client = c.Name, Contact = contact.Name, JobNumber = j == null ? (int?)null : j.Number })
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedList<WorkRequestSummary>(
            [.. rows.Select(x => new WorkRequestSummary(
                x.Request.Id, x.Request.ClientId, x.Client, x.Contact, x.Request.Title, x.Request.Description, x.Request.PreferredOn,
                x.Request.Status, x.Request.CreatedAt, x.Request.JobId, x.JobNumber is { } n ? Job.FormatNumber(n) : null, x.Request.DeclineReason))],
            paging.Page,
            paging.PageSize,
            total);
    }

    /// <summary>For the sidebar's count; zero (not 403) on a plan without the portal, so the shell needs no special case.</summary>
    public async Task<int> OpenCountAsync(CancellationToken cancellationToken) =>
        PlanFeatures.Includes(await limits.PlanAsync(cancellationToken), PlanFeature.Portal)
            ? await db.WorkRequests.CountAsync(r => r.Status == WorkRequestStatus.Open, cancellationToken)
            : 0;

    /// <summary>
    /// Creates the job from the (prefilled, possibly edited) form and marks the request converted, in one transaction.
    /// The job is always for the request's client: the contact must be able to follow it.
    /// </summary>
    public async Task<JobView> ConvertAsync(Guid id, JobInput input, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Portal, cancellationToken);
        if (!tenant.HasPermission(Domain.Memberships.Permissions.JobsCreate))
        {
            throw new DomainException("permission-denied", "Converting a request creates a job, which you may not do.", DomainErrorKind.Forbidden);
        }

        var request = await LoadAsync(id, cancellationToken);
        if (request.Status != WorkRequestStatus.Open)
        {
            throw new DomainException("request-not-open", $"This request was already {request.Status.ToString().ToLowerInvariant()}.");
        }

        return await jobs.CreateAsync(input with { ClientId = request.ClientId }, cancellationToken, job =>
        {
            request.Convert(job.Id, job.Label, tenant.UserId!.Value, time.GetUtcNow());
            return Task.CompletedTask;
        });
    }

    public async Task<WorkRequestSummary> DeclineAsync(Guid id, string? reason, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Portal, cancellationToken);
        var request = await LoadAsync(id, cancellationToken);
        request.Decline(reason, tenant.UserId!.Value, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException("request-not-open", "Someone else answered this request first. Reload to see what they did.");
        }

        var page = await ListAsync(null, request.ClientId, 1, PageRequest.MaxPageSize, cancellationToken);
        return page.Items.Single(r => r.Id == id);
    }

    private async Task<WorkRequest> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.WorkRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new DomainException(NotFoundCode, "Request not found.", DomainErrorKind.NotFound);
}
