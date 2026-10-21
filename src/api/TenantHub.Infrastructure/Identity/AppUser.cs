using Microsoft.AspNetCore.Identity;

namespace TenantHub.Infrastructure.Identity;

/// <summary>
/// An account. Users are not tenant-owned: one user can belong to several organizations through memberships
/// (D010), and roles live on the membership, not on the user.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public const int DisplayNameMaxLength = 80;

    public string DisplayName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
