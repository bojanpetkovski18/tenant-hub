using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Paging;
using TenantHub.Domain;
using TenantHub.Domain.Notifications;

namespace TenantHub.Application.Notifications;

public sealed record NotificationView(Guid Id, NotificationType Type, string Text, NotificationLink Link, Guid? LinkId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>A page of the caller's notifications, newest first, and how many are unread (the bell's count).</summary>
public sealed record NotificationList(IReadOnlyList<NotificationView> Items, int Page, int PageSize, int Total, int Unread);

/// <summary>
/// The caller's own notifications in the current organization (US-100): nobody reads or marks anyone else's. The tenant
/// filter and row-level security keep them in the organization; the user condition is this class's.
/// </summary>
public sealed class NotificationInbox(IAppDbContext db, ITenantContext tenant, TimeProvider time)
{
    public async Task<NotificationList> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var paging = PageRequest.From(page, pageSize);
        var mine = Mine();
        var total = await mine.CountAsync(cancellationToken);
        var unread = await mine.CountAsync(n => n.ReadAt == null, cancellationToken);
        var rows = await mine
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(cancellationToken);
        return new NotificationList(
            [.. rows.Select(n => new NotificationView(n.Id, n.Type, n.Content.Text, n.Content.Link, n.Content.LinkId, n.CreatedAt, n.ReadAt))],
            paging.Page,
            paging.PageSize,
            total,
            unread);
    }

    /// <summary>Marks the given ones (or all) as read; ids that are not the caller's are ignored. Returns the unread count.</summary>
    public async Task<int> MarkReadAsync(IReadOnlyList<Guid>? ids, bool all, CancellationToken cancellationToken)
    {
        if (!all && (ids is null || ids.Count == 0))
        {
            throw new RequestValidationException(FieldErrors.For("ids", "Name the notifications to mark as read, or mark all."));
        }

        var now = time.GetUtcNow();
        var unread = Mine().Where(n => n.ReadAt == null);
        await (all ? unread : unread.Where(n => ids!.Contains(n.Id)))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), cancellationToken);
        return await Mine().CountAsync(n => n.ReadAt == null, cancellationToken);
    }

    private IQueryable<Notification> Mine()
    {
        var user = tenant.UserId ?? throw new InvalidOperationException("Notifications are a signed-in member's.");
        return db.Notifications.AsNoTracking().Where(n => n.UserId == user);
    }
}

public sealed record NotificationPreferenceView(NotificationType Type, string Label, bool Email);

/// <summary>
/// The caller's email choices per type (US-101): user-scoped, the same in every organization. A missing row is the
/// default from SPEC-V2 5.7; saving writes every type the request names.
/// </summary>
public sealed class NotificationPreferences(IAppDbContext db)
{
    public async Task<IReadOnlyList<NotificationPreferenceView>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var chosen = await db.NotificationPreferences.AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.Type, p => p.Email, cancellationToken);
        return [.. NotificationTypes.All.Select(t => new NotificationPreferenceView(t, NotificationTypes.Label(t), chosen.GetValueOrDefault(t, NotificationTypes.EmailByDefault(t))))];
    }

    public async Task<IReadOnlyList<NotificationPreferenceView>> SetAsync(Guid userId, IReadOnlyList<(NotificationType? Type, bool Email)> choices, CancellationToken cancellationToken)
    {
        if (choices.Any(c => c.Type is null))
        {
            throw new RequestValidationException(FieldErrors.For("preferences", "Each preference names a notification type."));
        }

        var existing = await db.NotificationPreferences.Where(p => p.UserId == userId).ToDictionaryAsync(p => p.Type, cancellationToken);
        foreach (var (type, email) in choices)
        {
            if (existing.TryGetValue(type!.Value, out var row))
            {
                row.Set(email);
            }
            else
            {
                db.NotificationPreferences.Add(NotificationPreference.For(userId, type.Value, email));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(userId, cancellationToken);
    }
}
