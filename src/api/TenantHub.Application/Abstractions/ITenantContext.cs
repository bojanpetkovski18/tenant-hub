using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Abstractions;

/// <summary>
/// The organization the current request works in, and the caller's role and permissions there (SPEC 3.2).
/// Resolved from the route and an active membership, never from request content. Unresolved (sign-up, the
/// session, the seed before it picks an organization) means every tenant-owned query returns nothing.
/// </summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }

    Guid? UserId { get; }

    Role? Role { get; }

    IReadOnlySet<string> Permissions { get; }

    bool HasPermission(string permission);
}

/// <summary>
/// A tenant-owned row was about to be written outside the current organization. Always a bug: the guard stops
/// the save, the API logs a security event and answers 500 without details.
/// </summary>
public sealed class TenantIsolationException(string message) : Exception(message);
