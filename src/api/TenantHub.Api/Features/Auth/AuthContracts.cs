using System.ComponentModel.DataAnnotations;
using TenantHub.Application.Accounts;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Auth;

// Request attributes check shape only (present, bounded length). The content rules (name length after trimming,
// email format, time zone, password strength) belong to the handlers and Identity, which report every field at
// once with the same property names; a stricter attribute would stop the request before them and hide the rest.
public sealed record SignUpRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(200)] string DisplayName,
    [property: Required(AllowEmptyStrings = true), MaxLength(254)] string Email,
    [property: Required(AllowEmptyStrings = true), MaxLength(128)] string Password,
    [property: Required(AllowEmptyStrings = true), MaxLength(200)] string OrganizationName,
    [property: Required(AllowEmptyStrings = true), MaxLength(64)] string TimeZone);

public sealed record SignInRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(254)] string Email,
    [property: Required(AllowEmptyStrings = true), MaxLength(128)] string Password);

public sealed record MembershipResponse(
    Guid OrganizationId,
    string Slug,
    string Name,
    Role Role,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Role> GrantableRoles,
    IReadOnlyList<Role> InvitableRoles)
{
    public static MembershipResponse From(MembershipSummary m) =>
        new(m.OrganizationId, m.Slug, m.Name, m.Role, m.Permissions, m.GrantableRoles, m.InvitableRoles);
}

public sealed record UserResponse(Guid Id, string Email, string DisplayName, IReadOnlyList<MembershipResponse> Memberships)
{
    public static UserResponse From(SessionUser user) =>
        new(user.Id, user.Email, user.DisplayName, [.. user.Memberships.Select(MembershipResponse.From)]);
}

/// <summary><c>User</c> is null when nobody is signed in; the endpoint answers 200 either way, so guests see no errors.</summary>
public sealed record SessionResponse(UserResponse? User);
