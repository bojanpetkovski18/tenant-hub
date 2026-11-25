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

    /// <summary>
    /// A portal session's client (SPEC-V2 3.2): every client-owned read and write is limited to it, by the handlers, the
    /// save guard and row-level security. Null for staff, the system and outside a tenant.
    /// </summary>
    Guid? ClientId { get; }

    /// <summary>The signed-in client contact of a portal session; the audit names them.</summary>
    Guid? ContactId { get; }

    /// <summary>
    /// The API key of a machine request (SPEC-V2 3.3). <see cref="UserId"/> is then the key's creator, whom records that
    /// need a user name; the audit names the key (<see cref="ApiKeyName"/>).
    /// </summary>
    Guid? ApiKeyId { get; }

    string? ApiKeyName { get; }

    bool HasPermission(string permission);
}

/// <summary>
/// A tenant-owned row was about to be written outside the current organization. Always a bug: the guard stops
/// the save, the API logs a security event and answers 500 without details.
/// </summary>
public sealed class TenantIsolationException(string message) : Exception(message);
