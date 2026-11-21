using System.ComponentModel.DataAnnotations;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Notifications;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Notifications;

/// <summary>Either the ids to mark, or <c>All</c>.</summary>
public sealed record MarkReadRequest([property: MaxLength(200)] IReadOnlyList<Guid>? Ids, bool All = false);

public sealed record UnreadCount(int Unread);

/// <summary>
/// The caller's notifications in the current organization (US-100, SPEC-V2 7). Every member reads their own, so the
/// permission is <c>org.read</c>; the handler keeps each person to theirs.
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var notifications = app.MapTenantGroup().MapGroup("/notifications").WithTags("Notifications");

        notifications.MapGet("/", async (int? page, int? pageSize, NotificationInbox inbox, CancellationToken ct) =>
                TypedResults.Ok(await inbox.ListAsync(page, pageSize, ct)))
            .RequirePermission(Permissions.OrgRead);

        notifications.MapPost("/read", async (MarkReadRequest request, NotificationInbox inbox, CancellationToken ct) =>
                TypedResults.Ok(new UnreadCount(await inbox.MarkReadAsync(request.Ids, request.All, ct))))
            .RequirePermission(Permissions.OrgRead);

        return app;
    }
}
