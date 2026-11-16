namespace TenantHub.Application.Tenancy;

/// <summary>
/// What this scope shows the database's row-level security policies beyond its tenant (D038). The policies let a
/// request read rows of the organization it entered (<see cref="TenantContext"/>), and two narrow things more:
/// <list type="bullet">
///   <item>the memberships of the user it declared with <see cref="ReadAsUser"/>, in every organization (the
///   session, tenant resolution and the five-owned limit read them before any tenant is entered);</item>
///   <item>the one invitation whose token it presents with <see cref="PresentInvitationToken"/> (the invitee is not
///   a member yet).</item>
/// </list>
/// These are the database side of the allow-listed <c>AcrossTenants()</c> files, and only those files call them
/// (<c>TenantBypassAllowListTests</c>). Writes are never widened: they always need the entered organization.
/// </summary>
public sealed class RowSecurity
{
    public Guid? UserId { get; private set; }

    public byte[]? InvitationTokenHash { get; private set; }

    /// <summary>This scope reads the memberships of <paramref name="userId"/>, the caller or the account it just
    /// signed in. One user per scope, like the tenant.</summary>
    public void ReadAsUser(Guid userId)
    {
        if (UserId is { } current && current != userId)
        {
            throw new InvalidOperationException("A scope reads the memberships of one user; it cannot switch.");
        }

        UserId = userId;
    }

    /// <summary>The scope holds an invitation link; the hash of its token makes that one invitation readable.</summary>
    public void PresentInvitationToken(byte[] tokenHash) => InvitationTokenHash = tokenHash;
}
