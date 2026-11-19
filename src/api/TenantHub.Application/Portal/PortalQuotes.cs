using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;

namespace TenantHub.Application.Portal;

/// <summary>A quote as the contact sees it: the amount to decide on, or the one that was approved.</summary>
public sealed record PortalQuoteView(
    Guid JobId,
    string Label,
    string Title,
    DateOnly? ScheduledOn,
    decimal Amount,
    JobApprovalStatus Status,
    DateTimeOffset? SentAt,
    DateTimeOffset? DecidedAt,
    string? DecidedByName,
    string? Comment);

/// <summary>
/// Quotes in the portal (US-83, SPEC-V2 5.4): the client's pending quotes and those decided in the last 90 days; a
/// contact approves or declines a pending one once. The client comes from the session; another client's job, or a
/// job never sent, is "not found".
/// </summary>
public sealed class PortalQuotes(IAppDbContext db, PortalContext portal, TimeProvider time)
{
    public const string NotFoundCode = "quote-not-found";
    private static readonly TimeSpan DecidedWindow = TimeSpan.FromDays(90);

    public async Task<IReadOnlyList<PortalQuoteView>> ListAsync(CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow() - DecidedWindow;
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.ClientId == portal.ClientId
                && (j.ApprovalStatus == JobApprovalStatus.Pending
                    || ((j.ApprovalStatus == JobApprovalStatus.Approved || j.ApprovalStatus == JobApprovalStatus.Declined) && j.ApprovalDecidedAt >= since)))
            .OrderBy(j => j.ApprovalStatus == JobApprovalStatus.Pending ? 0 : 1)
            .ThenByDescending(j => j.ApprovalDecidedAt ?? j.ApprovalSentAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        var names = await ContactNamesAsync(rows.Select(r => r.ApprovalDecidedByContactId), cancellationToken);
        return [.. rows.Select(j => View(j, names))];
    }

    /// <summary>Approves or declines; a second decision, or one racing another contact's, is 409 <c>quote-already-decided</c>.</summary>
    public async Task<PortalQuoteView> DecideAsync(Guid jobId, bool approve, string? comment, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(
                j => j.Id == jobId && j.ClientId == portal.ClientId && j.ApprovalStatus != JobApprovalStatus.NotRequested,
                cancellationToken)
            ?? throw NotFound();
        job.Decide(approve, portal.ContactId, comment, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The version check: someone else (another contact, or staff changing the amount) got there first.
            throw new DomainException("quote-already-decided", "This quote changed while you were deciding. Reload to see where it stands.");
        }

        return View(job, await ContactNamesAsync([job.ApprovalDecidedByContactId], cancellationToken));
    }

    public static DomainException NotFound() => new(NotFoundCode, "Quote not found.", DomainErrorKind.NotFound);

    private static PortalQuoteView View(Job j, IReadOnlyDictionary<Guid, string> names) => new(
        j.Id,
        j.Label,
        j.Title,
        j.ScheduledOn,
        j.ApprovedAmount ?? j.QuotedAmount ?? 0m,
        j.ApprovalStatus,
        j.ApprovalSentAt,
        j.ApprovalDecidedAt,
        j.ApprovalDecidedByContactId is { } contact ? names.GetValueOrDefault(contact) : null,
        j.ApprovalComment);

    private async Task<IReadOnlyDictionary<Guid, string>> ContactNamesAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.ClientContacts.AsNoTracking().Where(c => wanted.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
    }
}
