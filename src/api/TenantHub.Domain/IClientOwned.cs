namespace TenantHub.Domain;

/// <summary>
/// Tenant-owned data that also belongs to one client of the organization (SPEC-V2 3.2). In a portal session the
/// database shows only the session's client's rows of these tables (row-level security on <c>app.client_id</c>), and
/// the coverage tests fail for a table without that policy.
/// </summary>
public interface IClientOwned : ITenantOwned
{
    Guid ClientId { get; }
}
