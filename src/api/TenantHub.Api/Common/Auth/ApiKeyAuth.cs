using System.Security.Claims;
using Microsoft.Net.Http.Headers;
using TenantHub.Application.Integrations;
using TenantHub.Domain.Integrations;

namespace TenantHub.Api.Common.Auth;

/// <summary>An endpoint that accepts API keys (SPEC-V2 3.3); every other endpoint refuses them.</summary>
public sealed record ApiKeyAllowedMetadata;

/// <summary>
/// Machine access (SPEC-V2 3.3, D044): <c>Authorization: Bearer &lt;key&gt;</c> on the same tenant routes as staff. Keys
/// are found inside the organization the route names, so a key of another organization is not found at all (404, as
/// for a non-member), and a key acts with a fixed set of permissions (<see cref="ApiKeyPermissions"/>).
/// </summary>
public static class ApiKeyAuth
{
    public const string Scheme = "ApiKey";
    public const string NotAllowedCode = "api-key-not-allowed";
    public const string InvalidCode = "api-key-invalid";
    public const string ConflictCode = "credentials-conflict";

    private const string KeyIdClaim = "th:api-key";

    /// <summary>Opts an endpoint in to API keys, and marks it so in the OpenAPI document.</summary>
    public static TBuilder AllowApiKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ApiKeyAllowedMetadata());

    /// <summary>The key a request authenticated with; null for sessions.</summary>
    public static ApiKeyIdentity? GetApiKey(this HttpContext context) =>
        context.Items.TryGetValue(typeof(ApiKeyIdentity), out var value) ? value as ApiKeyIdentity : null;

    public static bool IsApiKey(this ClaimsPrincipal principal) => principal.Identity?.AuthenticationType == Scheme;

    /// <summary>The partition of the per-key rate limit; null for sessions.</summary>
    public static string? KeyId(this ClaimsPrincipal principal) => principal.IsApiKey() ? principal.FindFirstValue(KeyIdClaim) : null;

    internal static ClaimsPrincipal PrincipalFor(ApiKeyIdentity key) => new(new ClaimsIdentity(
        [
            // The creator as the name identifier: created-by columns name a person, the audit names the key (SPEC-V2 2.1).
            new Claim(ClaimTypes.NameIdentifier, key.CreatorUserId.ToString()),
            new Claim(KeyIdClaim, key.KeyId.ToString()),
        ],
        Scheme));

    /// <summary>The bearer token of the request, if it has one.</summary>
    internal static string? Bearer(HttpRequest request) =>
        request.Headers.Authorization.ToString() is { Length: > 7 } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;
}

/// <summary>
/// Authenticates bearer requests to <c>/api</c>, after the cookie schemes and before authorization, antiforgery and the
/// rate limiter:
/// <list type="number">
/// <item>a cookie session and a key together → 400 <c>credentials-conflict</c> (a browser never sends a key);</item>
/// <item>an endpoint that does not accept keys → 403 <c>api-key-not-allowed</c>, before any lookup;</item>
/// <item>the key is looked up in a scope of its own, as the system inside the route's organization: not there → 404
/// <c>organization-not-found</c>; revoked or expired → 401 <c>api-key-invalid</c>; a plan without integrations → 403
/// <c>plan-feature-unavailable</c>;</item>
/// <item>otherwise the request runs as the key: <see cref="Tenancy.TenantResolutionFilter"/> enters the tenant with its
/// permissions, antiforgery is skipped (no cookie, so nothing to forge), and the rate limiter counts per key.</item>
/// </list>
/// The key itself is never logged.
/// </summary>
public sealed partial class ApiKeyMiddleware(RequestDelegate next, IServiceScopeFactory scopes, IProblemDetailsService problems, ILogger<ApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var presented = context.Request.Path.StartsWithSegments("/api") ? ApiKeyAuth.Bearer(context.Request) : null;
        if (presented is null)
        {
            await next(context);
            return;
        }

        if (context.Request.Cookies.ContainsKey(AuthSetup.SessionCookieName) || context.Request.Cookies.ContainsKey(PortalAuth.CookieName)
            || context.User.Identity?.IsAuthenticated == true)
        {
            await RefuseAsync(context, StatusCodes.Status400BadRequest, ApiKeyAuth.ConflictCode, "Send either a session cookie or an API key, not both.");
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<ApiKeyAllowedMetadata>() is null)
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, ApiKeyAuth.NotAllowedCode, "This endpoint does not accept API keys.");
            return;
        }

        if (!Guid.TryParse(context.Request.RouteValues["orgId"] as string, out var organizationId))
        {
            await RefuseAsync(context, StatusCodes.Status404NotFound, Tenancy.TenantResolutionFilter.OrganizationNotFoundCode, "Organization not found.");
            return;
        }

        (ApiKeyLookupResult Result, ApiKeyIdentity? Identity) found;
        await using (var scope = scopes.CreateAsyncScope())
        {
            found = await scope.ServiceProvider.GetRequiredService<ApiKeyLookup>().FindAsync(organizationId, presented, context.RequestAborted);
        }

        switch (found.Result)
        {
            case ApiKeyLookupResult.Found:
                context.Items[typeof(ApiKeyIdentity)] = found.Identity;
                context.User = ApiKeyAuth.PrincipalFor(found.Identity!);
                using (logger.BeginScope(new Dictionary<string, object> { ["ApiKeyId"] = found.Identity!.KeyId }))
                {
                    await next(context);
                }

                return;
            case ApiKeyLookupResult.Unusable:
                LogUnusable(logger, organizationId);
                await RefuseAsync(context, StatusCodes.Status401Unauthorized, ApiKeyAuth.InvalidCode, "This API key is revoked or expired.");
                return;
            case ApiKeyLookupResult.PlanWithout:
                await RefuseAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "plan-feature-unavailable",
                    "API keys and webhooks are part of the Business plan. An Owner can change the plan in Settings.");
                return;
            default:
                await RefuseAsync(context, StatusCodes.Status404NotFound, Tenancy.TenantResolutionFilter.OrganizationNotFoundCode, "Organization not found.");
                return;
        }
    }

    private async Task RefuseAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
        {
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer error=\"invalid_token\"";
        }

        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Extensions = { ["code"] = code } },
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused a revoked or expired API key in organization {OrganizationId}.")]
    private static partial void LogUnusable(ILogger logger, Guid organizationId);
}
