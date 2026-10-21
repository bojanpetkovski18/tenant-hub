namespace TenantHub.Domain.Memberships;

/// <summary>A member's role in one organization (SPEC 2). Permissions per role arrive in M03.</summary>
public enum Role
{
    Owner,
    Admin,
    Member,
    Viewer,
}
