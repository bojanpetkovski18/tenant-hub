using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Audit;

/// <summary>
/// The audit log's filters (US-40). Dates are days in the organization's time zone; <see cref="Before"/> is the
/// cursor from the previous page (<see cref="AuditPage.NextCursor"/>). Unknown or malformed values match nothing or
/// are ignored, like the other lists, so a hand-edited link still shows something sensible.
/// </summary>
public sealed record AuditListRequest(
    Guid? ActorUserId,
    string? Action,
    string? EntityType,
    Guid? EntityId,
    DateOnly? From,
    DateOnly? To,
    string? Before,
    int? PageSize);

/// <summary>An entry, with whether the thing it is about still exists (the viewer links only to those).</summary>
public sealed record AuditEntryView(
    long Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string ActorName,
    string Action,
    string EntityType,
    Guid EntityId,
    string Summary,
    IReadOnlyList<FieldChange> Changes,
    bool EntityExists);

/// <summary>One page, newest first; <see cref="NextCursor"/> is null on the last page. No total: counting is a scan (D030).</summary>
public sealed record AuditPage(IReadOnlyList<AuditEntryView> Items, string? NextCursor);

/// <summary>
/// The organization's activity (SPEC 7, <c>GET /audit</c>), newest first, by keyset on (occurred_at, id): every page
/// costs the same however deep it is, measured at 50,000 entries (D030). The tenant filter scopes it like any query.
/// </summary>
public sealed class ListAuditEntriesQuery(IAppDbContext db, ITenantContext tenant)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public async Task<AuditPage> HandleAsync(AuditListRequest request, CancellationToken cancellationToken)
    {
        var pageSize = request.PageSize is { } size and > 0 ? Math.Min(size, MaxPageSize) : DefaultPageSize;
        var entries = db.AuditEntries.AsNoTracking();

        if (request.ActorUserId is { } actor)
        {
            entries = entries.Where(a => a.ActorUserId == actor);
        }

        if (request.Action?.Trim() is { Length: > 0 } action)
        {
            entries = entries.Where(a => a.Action == action);
        }

        if (request.EntityType?.Trim() is { Length: > 0 } entityType)
        {
            entries = entries.Where(a => a.EntityType == entityType);
        }

        if (request.EntityId is { } entityId)
        {
            entries = entries.Where(a => a.EntityId == entityId);
        }

        if (request.From is not null || request.To is not null)
        {
            var zone = await db.Organizations
                .Where(o => o.Id == tenant.OrganizationId)
                .Select(o => o.TimeZone)
                .SingleAsync(cancellationToken);
            if (request.From is { } from)
            {
                var start = Organization.StartOfDayIn(zone, from);
                entries = entries.Where(a => a.OccurredAt >= start);
            }

            if (request.To is { } to)
            {
                // Through the whole last day: before the next one starts.
                var end = Organization.StartOfDayIn(zone, to.AddDays(1));
                entries = entries.Where(a => a.OccurredAt < end);
            }
        }

        if (Cursor.TryParse(request.Before, out var at, out var id))
        {
            // The first condition is implied by the second; it gives the index a bound to start from.
            entries = entries.Where(a => a.OccurredAt <= at && (a.OccurredAt < at || (a.OccurredAt == at && a.Id < id)));
        }

        var rows = await entries
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.Id)
            .Take(pageSize + 1)
            .Select(a => new { a.Id, a.OccurredAt, a.ActorUserId, a.ActorName, a.Action, a.EntityType, a.EntityId, a.Summary, a.Changes })
            .ToListAsync(cancellationToken);

        var page = rows.Take(pageSize).ToList();
        var existing = await ExistingAsync([.. page.Select(r => (r.EntityType, r.EntityId))], cancellationToken);
        var items = page
            .Select(r => new AuditEntryView(
                r.Id, r.OccurredAt, r.ActorUserId, r.ActorName, r.Action, r.EntityType, r.EntityId, r.Summary, r.Changes,
                existing.Contains((r.EntityType, r.EntityId))))
            .ToList();
        var next = rows.Count > pageSize ? Cursor.Format(page[^1].OccurredAt, page[^1].Id) : null;
        return new AuditPage(items, next);
    }

    /// <summary>Which entities of this page still exist: one query per type that appears, all tenant-filtered.</summary>
    private async Task<HashSet<(string Type, Guid Id)>> ExistingAsync(List<(string Type, Guid Id)> entities, CancellationToken cancellationToken)
    {
        var found = new HashSet<(string Type, Guid Id)>();
        List<Guid> Of(string type) => [.. entities.Where(e => e.Type == type).Select(e => e.Id).Distinct()];

        var jobs = Of("job");
        if (jobs.Count > 0)
        {
            found.UnionWith((await db.Jobs.Where(j => jobs.Contains(j.Id)).Select(j => j.Id).ToListAsync(cancellationToken)).Select(i => ("job", i)));
        }

        var clients = Of("client");
        if (clients.Count > 0)
        {
            found.UnionWith((await db.Clients.Where(c => clients.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken)).Select(i => ("client", i)));
        }

        // A member "exists" while active; an invitation while it is open (the members page shows both).
        var members = Of("member");
        if (members.Count > 0)
        {
            found.UnionWith((await db.Memberships.Where(m => members.Contains(m.UserId) && m.RemovedAt == null).Select(m => m.UserId).ToListAsync(cancellationToken)).Select(i => ("member", i)));
        }

        var invitations = Of("invitation");
        if (invitations.Count > 0)
        {
            found.UnionWith((await db.Invitations.Where(i => invitations.Contains(i.Id) && i.AcceptedAt == null && i.RevokedAt == null).Select(i => i.Id).ToListAsync(cancellationToken)).Select(i => ("invitation", i)));
        }

        return found;
    }

    /// <summary>"{ticks}-{id}": opaque to clients, but simple to read in a URL and to check.</summary>
    internal static class Cursor
    {
        public static string Format(DateTimeOffset at, long id) =>
            string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks}-{id}");

        public static bool TryParse(string? text, out DateTimeOffset at, out long id)
        {
            at = default;
            id = 0;
            var parts = text?.Split('-');
            if (parts is not [var ticks, var key]
                || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var utcTicks)
                || !long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out id)
                || utcTicks < DateTimeOffset.MinValue.UtcTicks
                || utcTicks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            at = new DateTimeOffset(utcTicks, TimeSpan.Zero);
            return true;
        }
    }
}
