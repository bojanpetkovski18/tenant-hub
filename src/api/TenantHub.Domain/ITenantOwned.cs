namespace TenantHub.Domain;

/// <summary>
/// Data that belongs to exactly one organization (SPEC 3). Every implementing entity gets the tenant query filter
/// and the SaveChanges guard automatically; the isolation coverage tests fail if one is missing either, or an
/// index that starts with <see cref="OrganizationId"/>.
/// </summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; }
}
