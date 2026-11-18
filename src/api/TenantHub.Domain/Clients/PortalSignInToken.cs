namespace TenantHub.Domain.Clients;

/// <summary>
/// A one-time portal sign-in link (SPEC-V2 5.3): only the SHA-256 of its 32 random bytes is stored, it works once,
/// and only for 15 minutes. Not audited: it changes no business data, and the token must never reach a log.
/// </summary>
public sealed class PortalSignInToken : ITenantOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private PortalSignInToken()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ContactId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public static PortalSignInToken Issue(ClientContact contact, byte[] tokenHash, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OrganizationId = contact.OrganizationId,
        ContactId = contact.Id,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + Lifetime,
    };

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;
}
