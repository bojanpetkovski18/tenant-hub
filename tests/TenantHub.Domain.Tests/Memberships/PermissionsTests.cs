using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Tests.Memberships;

/// <summary>
/// The SPEC section 2 table, row by row, written out again on purpose: a change to the map in code or to the
/// specification must be a deliberate edit of both.
/// </summary>
public sealed class PermissionsTests
{
    public static TheoryData<string, bool, bool, bool, bool> Table => new()
    {
        // permission, Owner, Admin, Member, Viewer
        { "org.read", true, true, true, true },
        { "org.manage", true, true, false, false },
        { "org.delete", true, false, false, false },
        { "billing.manage", true, false, false, false },
        { "members.manage", true, true, false, false },
        { "clients.read", true, true, true, true },
        { "clients.write", true, true, true, false },
        { "clients.delete", true, true, false, false },
        { "jobs.read", true, true, true, true },
        { "jobs.create", true, true, true, false },
        { "jobs.manage", true, true, false, false },
        { "jobs.work", true, true, true, false },
        { "audit.read", true, true, false, false },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Each_role_has_exactly_the_permissions_in_the_spec(string permission, bool owner, bool admin, bool member, bool viewer)
    {
        Assert.Equal(owner, Permissions.For(Role.Owner).Contains(permission));
        Assert.Equal(admin, Permissions.For(Role.Admin).Contains(permission));
        Assert.Equal(member, Permissions.For(Role.Member).Contains(permission));
        Assert.Equal(viewer, Permissions.For(Role.Viewer).Contains(permission));
    }

    [Fact]
    public void The_table_covers_every_permission_and_no_role_has_an_unknown_one()
    {
        var tabled = Table.Select(row => (string)row.Data.Item1).ToHashSet();

        Assert.Equal(tabled.Order(), Permissions.All.Order());
        Assert.All(Enum.GetValues<Role>(), role => Assert.Subset(tabled, Permissions.For(role).ToHashSet()));
    }

    [Fact]
    public void Permission_names_are_stable_lowercase_dotted_strings() =>
        Assert.All(Permissions.All, p => Assert.Matches("^[a-z]+\\.[a-z]+$", p));
}
