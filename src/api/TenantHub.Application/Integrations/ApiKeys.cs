using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Organizations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Integrations;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Integrations;

/// <summary>
/// The key itself: <c>thk_</c> and 32 random bytes (base64url), 47 characters. Only its SHA-256 hash is stored; the
/// first 12 characters are the visible prefix.
/// </summary>
public static class ApiKeySecrets
{
    public const string Scheme = "thk_";
    private const int PrefixLength = 12;

    private static readonly SearchValues<char> Base64UrlAlphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    public static (string Key, string Prefix, byte[] Hash) New()
    {
        var key = Scheme + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (key, key[..PrefixLength], Hash(key));
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Shaped like one of our keys (cheap to check before any lookup).</summary>
    public static bool LooksLikeKey(string? value) =>
        value is { Length: 47 } && value.StartsWith(Scheme, StringComparison.Ordinal)
        && value.AsSpan(Scheme.Length).IndexOfAnyExcept(Base64UrlAlphabet) < 0;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record ApiKeyView(
    Guid Id,
    string Name,
    string Prefix,
    ApiKeyAccess Access,
    DateTimeOffset CreatedAt,
    string CreatedByName,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool Active);

/// <summary>A new key with the key itself, which is shown this once and never again.</summary>
public sealed record CreatedApiKey(ApiKeyView ApiKey, string Key);

/// <summary>API keys (US-110): a Business feature for those with <c>integrations.manage</c>; every change audited.</summary>
public sealed class ApiKeyCommands(IAppDbContext db, ITenantContext tenant, PlanLimitsCheck limits, IIdentityService identity, TimeProvider time)
{
    public async Task<IReadOnlyList<ApiKeyView>> ListAsync(CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var keys = await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(cancellationToken);
        var names = await identity.FindProfilesAsync([.. keys.Select(k => k.CreatedByUserId).Distinct()], cancellationToken);
        var now = time.GetUtcNow();
        return [.. keys.Select(k => View(k, names.TryGetValue(k.CreatedByUserId, out var p) ? p.DisplayName : "A former member", now))];
    }

    public async Task<CreatedApiKey> CreateAsync(string? name, ApiKeyAccess? access, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var (secret, prefix, hash) = ApiKeySecrets.New();
        var now = time.GetUtcNow();
        var key = ApiKey.Issue(tenant.OrganizationId!.Value, name, access, expiresAt, prefix, hash, tenant.UserId!.Value, now);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        var creator = (await identity.FindProfilesAsync([key.CreatedByUserId], cancellationToken)).GetValueOrDefault(key.CreatedByUserId)?.DisplayName ?? "";
        return new CreatedApiKey(View(key, creator, now), secret);
    }

    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, cancellationToken)
            ?? throw new DomainException("api-key-not-found", "API key not found.", DomainErrorKind.NotFound);
        key.Revoke(time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ApiKeyView View(ApiKey k, string createdBy, DateTimeOffset now) =>
        new(k.Id, k.Name, k.Prefix, k.Access, k.CreatedAt, createdBy, k.ExpiresAt, k.LastUsedAt, k.RevokedAt, k.IsUsable(now));
}

/// <summary>Who a key is, once found: enough to enter the tenant as it.</summary>
public sealed record ApiKeyIdentity(Guid OrganizationId, Guid KeyId, string Name, ApiKeyAccess Access, Guid CreatorUserId);

public enum ApiKeyLookupResult
{
    Found,

    /// <summary>No such key in this organization: an unknown key and another organization's key answer the same.</summary>
    NotFound,

    /// <summary>Revoked or expired.</summary>
    Unusable,

    /// <summary>The organization's plan has no API access (SPEC-V2 4.8).</summary>
    PlanWithout,
}

/// <summary>
/// Finds a presented key (SPEC-V2 3.3) inside the organization the route names: the lookup runs as the system in that
/// organization, under the tenant filter and row-level security, so no other organization's keys are ever read and
/// no bypass is needed. Records the use at most once a minute. Run it in a scope of its own: it enters the tenant.
/// </summary>
public sealed class ApiKeyLookup(IAppDbContext db, TenantContext tenant, TimeProvider time)
{
    public async Task<(ApiKeyLookupResult Result, ApiKeyIdentity? Identity)> FindAsync(Guid organizationId, string presented, CancellationToken cancellationToken)
    {
        if (!ApiKeySecrets.LooksLikeKey(presented))
        {
            return (ApiKeyLookupResult.NotFound, null);
        }

        tenant.EnterAsSystem(organizationId);
        var hash = ApiKeySecrets.Hash(presented);
        var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.KeyHash == hash, cancellationToken);
        if (key is null)
        {
            return (ApiKeyLookupResult.NotFound, null);
        }

        var now = time.GetUtcNow();
        if (!key.IsUsable(now))
        {
            return (ApiKeyLookupResult.Unusable, null);
        }

        var plan = await db.Organizations.Where(o => o.Id == organizationId).Select(o => o.Plan).SingleAsync(cancellationToken);
        if (!PlanFeatures.Includes(plan, PlanFeature.Integrations))
        {
            return (ApiKeyLookupResult.PlanWithout, null);
        }

        var stale = now - ApiKey.UseRecordedEvery;
        await db.ApiKeys
            .Where(k => k.Id == key.Id && (k.LastUsedAt == null || k.LastUsedAt < stale))
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        return (ApiKeyLookupResult.Found, new ApiKeyIdentity(organizationId, key.Id, key.Name, key.Access, key.CreatedByUserId));
    }
}
