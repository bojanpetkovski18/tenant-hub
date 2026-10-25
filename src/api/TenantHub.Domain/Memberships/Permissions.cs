namespace TenantHub.Domain.Memberships;

/// <summary>
/// What a member may do in their organization (SPEC 2). Checks always name a permission, never a role, so custom
/// roles (V2) would change data, not code (D008). The strings are also what the API sends to the SPA.
/// </summary>
public static class Permissions
{
    public const string OrgRead = "org.read";
    public const string OrgManage = "org.manage";
    public const string OrgDelete = "org.delete";
    public const string BillingManage = "billing.manage";
    public const string MembersManage = "members.manage";
    public const string ClientsRead = "clients.read";
    public const string ClientsWrite = "clients.write";
    public const string ClientsDelete = "clients.delete";
    public const string JobsRead = "jobs.read";
    public const string JobsCreate = "jobs.create";
    public const string JobsManage = "jobs.manage";
    public const string JobsWork = "jobs.work";
    public const string AuditRead = "audit.read";

    public static IReadOnlyList<string> All { get; } =
    [
        OrgRead, OrgManage, OrgDelete, BillingManage, MembersManage,
        ClientsRead, ClientsWrite, ClientsDelete,
        JobsRead, JobsCreate, JobsManage, JobsWork,
        AuditRead,
    ];

    private static readonly IReadOnlySet<string> OwnerPermissions = new HashSet<string>(All, StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> AdminPermissions = new HashSet<string>(
        [
            OrgRead, OrgManage, MembersManage,
            ClientsRead, ClientsWrite, ClientsDelete,
            JobsRead, JobsCreate, JobsManage, JobsWork,
            AuditRead,
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> MemberPermissions = new HashSet<string>(
        [OrgRead, ClientsRead, ClientsWrite, JobsRead, JobsCreate, JobsWork],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ViewerPermissions = new HashSet<string>(
        [OrgRead, ClientsRead, JobsRead],
        StringComparer.Ordinal);

    public static IReadOnlySet<string> For(Role role) => role switch
    {
        Role.Owner => OwnerPermissions,
        Role.Admin => AdminPermissions,
        Role.Member => MemberPermissions,
        Role.Viewer => ViewerPermissions,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };
}
