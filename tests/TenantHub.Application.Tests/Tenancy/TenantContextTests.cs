using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Tests.Tenancy;

public sealed class TenantContextTests
{
    private static readonly Guid OrgA = Guid.Parse("0199f0a0-0000-7000-8000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("0199f0a0-0000-7000-8000-0000000000b1");
    private static readonly Guid User = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");

    [Fact]
    public void A_new_context_has_no_organization_and_no_permissions()
    {
        var tenant = new TenantContext();

        Assert.Null(tenant.OrganizationId);
        Assert.Null(tenant.Role);
        Assert.False(tenant.HasPermission(Permissions.OrgRead));
    }

    [Fact]
    public void Entering_sets_the_organization_and_the_role_s_permissions()
    {
        var tenant = new TenantContext();

        tenant.Enter(OrgA, User, Role.Viewer);

        Assert.Equal((OrgA, User, Role.Viewer), (tenant.OrganizationId, tenant.UserId, tenant.Role));
        Assert.True(tenant.HasPermission(Permissions.JobsRead));
        Assert.False(tenant.HasPermission(Permissions.ClientsWrite));
    }

    [Fact]
    public void A_scope_cannot_switch_to_another_organization_or_user()
    {
        var tenant = new TenantContext();
        tenant.Enter(OrgA, User, Role.Owner);

        Assert.Throws<InvalidOperationException>(() => tenant.Enter(OrgB, User, Role.Owner));
        Assert.Throws<InvalidOperationException>(() => tenant.Enter(OrgA, Guid.NewGuid(), Role.Owner));
        Assert.Equal(OrgA, tenant.OrganizationId);
    }
}
