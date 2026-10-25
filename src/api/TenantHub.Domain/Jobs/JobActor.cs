using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Jobs;

/// <summary>
/// Who is acting, as the job rules see them (SPEC 2, 5.4): <c>jobs.manage</c> acts on any job, <c>jobs.work</c>
/// only on jobs assigned to the actor. The role map stays in <see cref="Permissions"/>; this carries its result.
/// </summary>
public sealed record JobActor(Guid UserId, bool CanManage, bool CanWork)
{
    public static JobActor From(Guid userId, IReadOnlySet<string> permissions) =>
        new(userId, permissions.Contains(Permissions.JobsManage), permissions.Contains(Permissions.JobsWork));

    public static JobActor From(Guid userId, Role role) => From(userId, Permissions.For(role));
}
