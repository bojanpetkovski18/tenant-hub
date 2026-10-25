namespace TenantHub.Api.Common.Tenancy;

/// <summary>Who may call an endpoint (SPEC 7). Every endpoint declares exactly one; the isolation tests enforce it.</summary>
public enum EndpointAccess
{
    /// <summary>Anyone, signed in or not.</summary>
    Public,

    /// <summary>A signed-in user, outside any organization.</summary>
    User,

    /// <summary>A signed-in user with an active membership in the organization in the route, and a permission.</summary>
    Tenant,
}

public sealed record EndpointAccessMetadata(EndpointAccess Access);

public sealed record RequiredPermissionMetadata(string Permission);

public static class EndpointAccessExtensions
{
    public const string TenantRoutePrefix = "/api/orgs/{orgId:guid}";

    public static TBuilder Public<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AllowAnonymous().WithMetadata(new EndpointAccessMetadata(EndpointAccess.Public));

    public static TBuilder UserScoped<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new EndpointAccessMetadata(EndpointAccess.User));

    /// <summary>
    /// Routes under <c>/api/orgs/{orgId}</c>: the membership check and the permission check run before every
    /// handler in the group (SPEC 3.2). The organization comes from the route only.
    /// </summary>
    public static RouteGroupBuilder MapTenantGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup(TenantRoutePrefix)
            .WithMetadata(new EndpointAccessMetadata(EndpointAccess.Tenant))
            .AddEndpointFilter<TenantResolutionFilter>();

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredPermissionMetadata(permission));
}
