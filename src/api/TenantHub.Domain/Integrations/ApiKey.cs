using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Integrations;

/// <summary>What a key may do (SPEC-V2 2.4): a fixed set, whatever its creator's role.</summary>
public enum ApiKeyAccess
{
    ReadOnly,
    ReadWrite,
}

public static class ApiKeyPermissions
{
    private static readonly IReadOnlySet<string> Read = new HashSet<string>(
        [Permissions.ClientsRead, Permissions.JobsRead, Permissions.AgreementsRead], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ReadWrite = new HashSet<string>(
        [.. Read, Permissions.ClientsWrite, Permissions.JobsCreate, Permissions.JobsWork, Permissions.JobsManage], StringComparer.Ordinal);

    /// <summary>Never administration: members, billing, the organization, the audit log, the portal or integrations.</summary>
    public static IReadOnlySet<string> For(ApiKeyAccess access) => access == ApiKeyAccess.ReadWrite ? ReadWrite : Read;
}

/// <summary>
/// A credential an organization gives another system (US-110, SPEC-V2 5.8). Only the SHA-256 hash of the key is kept,
/// with a short prefix to recognise it by; the key itself is shown once, when it is made. Revocation is immediate; an
/// expired or revoked key authenticates nothing.
/// </summary>
public sealed class ApiKey : AggregateRoot, ITenantOwned
{
    // "API key " and the name fit the audit's 80-character actor name.
    public const int NameMaxLength = 60;

    /// <summary>A key expires at most this long after it is made (SPEC-V2 5.8).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(366);

    /// <summary>Last use is written at most this often, so a busy key does not write on every request.</summary>
    public static readonly TimeSpan UseRecordedEvery = TimeSpan.FromMinutes(1);

    private ApiKey()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = "";

    /// <summary>The first characters of the key, shown in lists so people can tell their keys apart.</summary>
    public string Prefix { get; private set; } = "";

    public byte[] KeyHash { get; private set; } = [];

    public ApiKeyAccess Access { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>How the audit names what the key does (SPEC-V2 2.1).</summary>
    public string ActorName => $"API key {Name}";

    public static ApiKey Issue(
        Guid organizationId,
        string? name,
        ApiKeyAccess? access,
        DateTimeOffset? expiresAt,
        string prefix,
        byte[] keyHash,
        Guid createdByUserId,
        DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        var normalized = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is 0 or > NameMaxLength)
        {
            errors["name"] = [$"Name the key in 1–{NameMaxLength} characters, after the system that uses it."];
        }

        if (access is null)
        {
            errors["access"] = ["Choose read-only or read-write."];
        }

        if (expiresAt is { } expiry && (expiry <= now || expiry > now + MaxLifetime))
        {
            errors["expiresAt"] = ["Choose an expiry in the future and within a year, or none."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        var key = new ApiKey
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Name = normalized,
            Prefix = prefix,
            KeyHash = keyHash,
            Access = access!.Value,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
            ExpiresAt = expiresAt,
        };
        key.Raise(new ApiKeyEvent(
            key,
            "api_key.created",
            $"Created the {(key.Access == ApiKeyAccess.ReadWrite ? "read-write" : "read-only")} API key “{key.Name}” ({prefix}…)",
            [new FieldChange("access", null, key.Access.ToString())]));
        return key;
    }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            throw new DomainException("api-key-revoked", $"The API key “{Name}” is already revoked.");
        }

        RevokedAt = now;
        Raise(new ApiKeyEvent(this, "api_key.revoked", $"Revoked the API key “{Name}” ({Prefix}…)", []));
    }
}

public sealed record ApiKeyEvent(ApiKey Key, string Action, string Summary, IReadOnlyList<FieldChange> Changes) : IAuditableEvent
{
    public Guid OrganizationId => Key.OrganizationId;

    public string EntityType => "api_key";

    public Guid EntityId => Key.Id;
}
