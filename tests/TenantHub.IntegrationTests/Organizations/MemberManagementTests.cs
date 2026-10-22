using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Api.Features.Invitations;
using TenantHub.Api.Features.Organizations;
using TenantHub.Application.Invitations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Organizations;

/// <summary>US-12 through the API: role changes, removal and leaving within the rules of SPEC 5.2.</summary>
public sealed class MemberManagementTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Members(Guid org) => $"/api/orgs/{org}/members";

    private static async Task<Guid> UserIdAsync(ApiTenants tenants, string email)
    {
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct);
    }

    [Fact]
    public async Task Roles_change_within_the_rules()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var ownerId = await UserIdAsync(tenants, tenants.EmailsA[Role.Owner]);
        var adminId = await UserIdAsync(tenants, tenants.EmailsA[Role.Admin]);
        var memberId = await UserIdAsync(tenants, tenants.EmailsA[Role.Member]);
        var viewerId = await UserIdAsync(tenants, tenants.EmailsA[Role.Viewer]);

        // Admins: not to Owner, not an Owner, not themselves.
        await ProblemAssert.HasCodeAsync(await admin.PatchJsonAsync($"{Members(tenants.OrgA)}/{memberId}", new { role = "owner" }), HttpStatusCode.Forbidden, "role-not-grantable");
        await ProblemAssert.HasCodeAsync(await admin.PatchJsonAsync($"{Members(tenants.OrgA)}/{ownerId}", new { role = "viewer" }), HttpStatusCode.Forbidden, "owner-protected");
        await ProblemAssert.HasCodeAsync(await admin.PatchJsonAsync($"{Members(tenants.OrgA)}/{adminId}", new { role = "member" }), HttpStatusCode.Conflict, "cannot-change-own-role");
        var promoted = await admin.PatchJsonAsync($"{Members(tenants.OrgA)}/{viewerId}", new { role = "member" });

        // Owners may make an Owner.
        var crowned = await owner.PatchJsonAsync($"{Members(tenants.OrgA)}/{memberId}", new { role = "owner" });

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (promoted.StatusCode, crowned.StatusCode));
        var listed = await owner.Http.GetFromJsonAsync<List<MemberResponse>>(Members(tenants.OrgA), BrowserClient.Json, Ct);
        Assert.Equal((Role.Member, Role.Owner), (listed!.Single(m => m.UserId == viewerId).Role, listed!.Single(m => m.UserId == memberId).Role));
        await ProblemAssert.HasFieldErrorsAsync(await owner.PatchJsonAsync($"{Members(tenants.OrgA)}/{viewerId}", new { }), "role");
        await ProblemAssert.HasCodeAsync(await owner.PatchJsonAsync($"{Members(tenants.OrgA)}/{Guid.NewGuid()}", new { role = "member" }), HttpStatusCode.NotFound, "member-not-found");

        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var changes = await db.AuditEntries.AcrossTenants()
            .Where(a => a.OrganizationId == tenants.OrgA && a.Action == "member.role_changed")
            .OrderBy(a => a.Id)
            .Select(a => a.Summary)
            .ToListAsync(Ct);
        Assert.Equal(
            [$"Changed {tenants.EmailsA[Role.Viewer].Split('@')[0]}'s role from Viewer to Member", $"Changed {tenants.EmailsA[Role.Member].Split('@')[0]}'s role from Member to Owner"],
            changes);
    }

    [Fact]
    public async Task The_member_list_says_what_the_caller_may_do_to_whom()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var adminId = await UserIdAsync(tenants, tenants.EmailsA[Role.Admin]);
        var ownerId = await UserIdAsync(tenants, tenants.EmailsA[Role.Owner]);
        var viewerId = await UserIdAsync(tenants, tenants.EmailsA[Role.Viewer]);

        var asAdmin = await (await tenants.ClientForAsync(Role.Admin)).Http.GetFromJsonAsync<List<MemberResponse>>(Members(tenants.OrgA), BrowserClient.Json, Ct);
        var asViewer = await (await tenants.ClientForAsync(Role.Viewer)).Http.GetFromJsonAsync<List<MemberResponse>>(Members(tenants.OrgA), BrowserClient.Json, Ct);

        var self = asAdmin!.Single(m => m.UserId == adminId);
        Assert.Equal((true, false, false, true), (self.IsYou, self.CanChangeRole, self.CanRemove, self.CanLeave));
        var anOwner = asAdmin!.Single(m => m.UserId == ownerId);
        Assert.Equal((false, false, false), (anOwner.IsYou, anOwner.CanChangeRole, anOwner.CanRemove));
        var aViewer = asAdmin!.Single(m => m.UserId == viewerId);
        Assert.Equal((true, true), (aViewer.CanChangeRole, aViewer.CanRemove));
        Assert.All(asViewer!.Where(m => !m.IsYou), m => Assert.False(m.CanChangeRole || m.CanRemove));
    }

    [Fact]
    public async Task The_session_says_which_roles_each_membership_may_grant_and_invite()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);

        var owner = (await (await tenants.ClientForAsync(Role.Owner)).SessionAsync()).User!.Memberships.Single(m => m.OrganizationId == tenants.OrgA);
        var admin = (await (await tenants.ClientForAsync(Role.Admin)).SessionAsync()).User!.Memberships.Single(m => m.OrganizationId == tenants.OrgA);
        var member = (await (await tenants.ClientForAsync(Role.Member)).SessionAsync()).User!.Memberships.Single(m => m.OrganizationId == tenants.OrgA);

        Assert.Equal([Role.Owner, Role.Admin, Role.Member, Role.Viewer], owner.GrantableRoles);
        Assert.Equal([Role.Admin, Role.Member, Role.Viewer], owner.InvitableRoles);
        Assert.Equal([Role.Admin, Role.Member, Role.Viewer], admin.GrantableRoles);
        Assert.Equal([Role.Admin, Role.Member, Role.Viewer], admin.InvitableRoles);
        Assert.Empty(member.GrantableRoles);
        Assert.Empty(member.InvitableRoles);
    }

    [Fact]
    public async Task A_removed_member_loses_access_on_the_next_request()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var member = await tenants.ClientForAsync(Role.Member);
        var memberId = await UserIdAsync(tenants, tenants.EmailsA[Role.Member]);
        Assert.Equal(HttpStatusCode.OK, (await member.Http.GetAsync($"/api/orgs/{tenants.OrgA}/clients", Ct)).StatusCode);

        var removed = await admin.SendJsonAsync(HttpMethod.Delete, $"{Members(tenants.OrgA)}/{memberId}", null);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await ProblemAssert.HasCodeAsync(await member.Http.GetAsync($"/api/orgs/{tenants.OrgA}/clients", Ct), HttpStatusCode.NotFound, TenantResolutionFilter.OrganizationNotFoundCode);
        Assert.DoesNotContain((await member.SessionAsync()).User!.Memberships, m => m.OrganizationId == tenants.OrgA);
        var listed = await admin.Http.GetFromJsonAsync<List<MemberResponse>>(Members(tenants.OrgA), BrowserClient.Json, Ct);
        Assert.DoesNotContain(listed!, m => m.UserId == memberId);

        // The row stays (soft removal, SPEC 5.2), so history can still name the person.
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var row = await db.Memberships.AcrossTenants().SingleAsync(m => m.OrganizationId == tenants.OrgA && m.UserId == memberId, Ct);
        Assert.NotNull(row.RemovedAt);
        Assert.True(await db.AuditEntries.AcrossTenants().AnyAsync(a => a.EntityId == memberId && a.Action == "member.removed", Ct));
    }

    [Fact]
    public async Task Removal_is_refused_for_yourself_and_for_owners_by_admins()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var adminId = await UserIdAsync(tenants, tenants.EmailsA[Role.Admin]);
        var ownerId = await UserIdAsync(tenants, tenants.EmailsA[Role.Owner]);

        await ProblemAssert.HasCodeAsync(await admin.SendJsonAsync(HttpMethod.Delete, $"{Members(tenants.OrgA)}/{adminId}", null), HttpStatusCode.Conflict, "use-leave");
        await ProblemAssert.HasCodeAsync(await admin.SendJsonAsync(HttpMethod.Delete, $"{Members(tenants.OrgA)}/{ownerId}", null), HttpStatusCode.Forbidden, "owner-protected");
    }

    [Fact]
    public async Task The_last_owner_cannot_leave_or_be_removed_but_others_can_leave()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var viewer = await tenants.ClientForAsync(Role.Viewer);
        var coOwnerId = await UserIdAsync(tenants, tenants.CoOwnerEmail);

        // Two Owners: one may remove the other; then the remaining Owner is the last one.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendJsonAsync(HttpMethod.Delete, $"{Members(tenants.OrgA)}/{coOwnerId}", null)).StatusCode);
        await ProblemAssert.HasCodeAsync(await owner.SendJsonAsync(HttpMethod.Delete, $"{Members(tenants.OrgA)}/me", null), HttpStatusCode.Conflict, "last-owner");

        var left = await viewer.SendJsonAsync(HttpMethod.Delete, $"{Members(tenants.OrgA)}/me", null);

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        await ProblemAssert.HasCodeAsync(await viewer.Http.GetAsync($"/api/orgs/{tenants.OrgA}", Ct), HttpStatusCode.NotFound, TenantResolutionFilter.OrganizationNotFoundCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Http.GetAsync($"/api/orgs/{tenants.OrgA}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_at_once_leave_one_owner()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        using var coOwner = new BrowserClient(tenants.Factory);
        (await coOwner.SignInAsync(tenants.CoOwnerEmail)).EnsureSuccessStatusCode();
        var ownerId = await UserIdAsync(tenants, tenants.EmailsA[Role.Owner]);
        var coOwnerId = await UserIdAsync(tenants, tenants.CoOwnerEmail);
        await owner.SessionAsync();
        await coOwner.SessionAsync();

        var responses = await Task.WhenAll(
            owner.PatchJsonAsync($"{Members(tenants.OrgA)}/{coOwnerId}", new { role = "admin" }),
            coOwner.PatchJsonAsync($"{Members(tenants.OrgA)}/{ownerId}", new { role = "admin" }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        Assert.Equal(1, await db.Memberships.AcrossTenants().CountAsync(m => m.OrganizationId == tenants.OrgA && m.Role == Role.Owner && m.RemovedAt == null, Ct));
    }

    [Fact]
    public async Task Inviting_a_removed_member_again_gives_them_their_membership_back_with_the_new_role()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var removedId = await UserIdAsync(tenants, tenants.RemovedMemberEmail);
        using var removed = new BrowserClient(tenants.Factory);
        (await removed.SignInAsync(tenants.RemovedMemberEmail)).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await removed.Http.GetAsync($"/api/orgs/{tenants.OrgA}", Ct), HttpStatusCode.NotFound, TenantResolutionFilter.OrganizationNotFoundCode);

        var invite = await owner.PostJsonAsync($"/api/orgs/{tenants.OrgA}/invitations", new { email = tenants.RemovedMemberEmail, role = "viewer" });
        var link = (await invite.Content.ReadFromJsonAsync<InvitationSent>(BrowserClient.Json, Ct))!.Link!;
        var accepted = await removed.PostJsonAsync("/api/invitations/accept", new { token = link[(link.IndexOf('#', StringComparison.Ordinal) + 1)..] });

        var joined = (await accepted.Content.ReadFromJsonAsync<AcceptInvitationResponse>(BrowserClient.Json, Ct))!;
        Assert.Equal(Role.Viewer, joined.Session.User!.Memberships.Single(m => m.OrganizationId == tenants.OrgA).Role);
        Assert.Equal(HttpStatusCode.OK, (await removed.Http.GetAsync($"/api/orgs/{tenants.OrgA}", Ct)).StatusCode);

        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var rows = await db.Memberships.AcrossTenants().Where(m => m.OrganizationId == tenants.OrgA && m.UserId == removedId).ToListAsync(Ct);
        Assert.Null(Assert.Single(rows).RemovedAt);
    }
}
