using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Api.Features.Invitations;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Invitations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Invitations;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Invitations;

/// <summary>US-10 and US-11 end to end: invite, look at the link, accept with or without an account, and every refusal.</summary>
public sealed class InvitationFlowTests(PostgresFixture postgres)
{
    private const string PublicUrl = "https://app.tenanthub.example/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Invitations(Guid org) => $"/api/orgs/{org}/invitations";

    private static string TokenOf(string link)
    {
        Assert.StartsWith($"{PublicUrl}invite#", link, StringComparison.Ordinal);
        return link[(link.IndexOf('#', StringComparison.Ordinal) + 1)..];
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, BrowserClient.Json)!;
    }

    private static async Task<InvitationSent> InviteAsync(BrowserClient inviter, Guid org, string email, string role = "member") =>
        await ReadAsync<InvitationSent>(await inviter.PostJsonAsync(Invitations(org), new { email, role }));

    private static Task<HttpResponseMessage> PreviewAsync(BrowserClient browser, string token) =>
        browser.PostJsonAsync("/api/invitations/preview", new { token });

    private static Task<HttpResponseMessage> SignUpAsync(BrowserClient browser, string token, string displayName = "Nina Park") =>
        browser.PostJsonAsync("/api/invitations/sign-up", new { token, displayName, password = BrowserClient.Password });

    private static Task<HttpResponseMessage> AcceptAsync(BrowserClient browser, string token) =>
        browser.PostJsonAsync("/api/invitations/accept", new { token });

    [Fact]
    public async Task A_new_person_signs_up_through_the_link_and_lands_in_the_organization()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var email = $"Nina.Park-{Guid.NewGuid():N}@Test.example";

        var sent = await InviteAsync(admin, tenants.OrgA, email);
        using var invitee = new BrowserClient(tenants.Factory);
        var token = TokenOf(sent.Link!);
        var preview = await ReadAsync<InvitationPreview>(await PreviewAsync(invitee, token));
        var joined = await ReadAsync<AcceptInvitationResponse>(await SignUpAsync(invitee, token));
        var listed = await admin.Http.GetFromJsonAsync<List<InvitationView>>(Invitations(tenants.OrgA), BrowserClient.Json, Ct);

        // Email is disabled in tests, so the inviter gets the link (D011).
        Assert.Equal((InvitationDelivery.Link, InvitationState.Pending, Role.Member), (sent.Delivery, sent.Invitation.State, sent.Invitation.Role));
        Assert.Equal(("Alpha Cleaning", "admin-", Role.Member, InvitationState.Pending, false, null), (
            preview.OrganizationName, preview.InvitedBy[..6], preview.Role, preview.State, preview.AccountExists, preview.SignedInEmailMatches));
        Assert.Equal($"N***@{email.Split('@')[1]}", preview.MaskedEmail);
        Assert.Equal((tenants.OrgA, false), (joined.OrganizationId, joined.AlreadyMember));
        var membership = joined.Session.User!.Memberships.Single(m => m.OrganizationId == tenants.OrgA);
        Assert.Equal(Role.Member, membership.Role);
        Assert.Equal(email, joined.Session.User.Email);
        Assert.DoesNotContain(listed!, i => i.Id == sent.Invitation.Id);

        // Signed in by the sign-up: the organization opens right away.
        Assert.Equal(HttpStatusCode.OK, (await invitee.Http.GetAsync($"/api/orgs/{tenants.OrgA}/clients", Ct)).StatusCode);

        // The link is single use.
        using var again = new BrowserClient(tenants.Factory);
        await ProblemAssert.HasCodeAsync(await SignUpAsync(again, token, "Someone Else"), HttpStatusCode.Conflict, "invitation-used");
        var used = await ReadAsync<InvitationPreview>(await PreviewAsync(again, token));
        Assert.Equal(InvitationState.Accepted, used.State);

        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var history = await db.AuditEntries.AcrossTenants().Where(a => a.EntityId == sent.Invitation.Id).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct);
        Assert.Equal(["invitation.created", "invitation.accepted"], history);
    }

    [Fact]
    public async Task Someone_with_an_account_joins_with_one_click_and_a_second_click_just_opens_the_organization()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        using var existing = new BrowserClient(tenants.Factory);
        var email = BrowserClient.NewEmail();
        (await existing.SignUpAsync(email, "Elsewhere Ltd")).EnsureSuccessStatusCode();

        var token = TokenOf((await InviteAsync(owner, tenants.OrgA, email.ToUpperInvariant(), "viewer")).Link!);
        var preview = await ReadAsync<InvitationPreview>(await PreviewAsync(existing, token));
        var first = await ReadAsync<AcceptInvitationResponse>(await AcceptAsync(existing, token));
        var second = await ReadAsync<AcceptInvitationResponse>(await AcceptAsync(existing, token));

        Assert.Equal((true, true, (Guid?)null), (preview.AccountExists, preview.SignedInEmailMatches, preview.MemberOfOrganizationId));
        Assert.False(first.AlreadyMember);
        Assert.Equal(Role.Viewer, first.Session.User!.Memberships.Single(m => m.OrganizationId == tenants.OrgA).Role);
        Assert.Equal((tenants.OrgA, true), (second.OrganizationId, second.AlreadyMember));
        var afterwards = await ReadAsync<InvitationPreview>(await PreviewAsync(existing, token));
        Assert.Equal(tenants.OrgA, afterwards.MemberOfOrganizationId);
    }

    [Fact]
    public async Task Signed_in_with_another_email_is_explained_and_refused()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var token = TokenOf((await InviteAsync(owner, tenants.OrgA, BrowserClient.NewEmail())).Link!);
        using var stranger = new BrowserClient(tenants.Factory);
        (await stranger.SignUpAsync(BrowserClient.NewEmail(), "Stranger Co")).EnsureSuccessStatusCode();

        var preview = await ReadAsync<InvitationPreview>(await PreviewAsync(stranger, token));

        Assert.False(preview.SignedInEmailMatches);
        await ProblemAssert.HasCodeAsync(await AcceptAsync(stranger, token), HttpStatusCode.Conflict, "invitation-email-mismatch");
        Assert.DoesNotContain((await stranger.SessionAsync()).User!.Memberships, m => m.OrganizationId == tenants.OrgA);
    }

    [Fact]
    public async Task Resending_makes_the_old_link_fail_and_inviting_again_replaces_the_invitation()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var email = BrowserClient.NewEmail();
        var first = await InviteAsync(owner, tenants.OrgA, email);

        var resent = await ReadAsync<InvitationSent>(await owner.PostJsonAsync($"{Invitations(tenants.OrgA)}/{first.Invitation.Id}/resend", new { }));
        var again = await InviteAsync(owner, tenants.OrgA, email.ToUpperInvariant(), "admin");
        var listed = await owner.Http.GetFromJsonAsync<List<InvitationView>>(Invitations(tenants.OrgA), BrowserClient.Json, Ct);

        using var invitee = new BrowserClient(tenants.Factory);
        await ProblemAssert.HasCodeAsync(await PreviewAsync(invitee, TokenOf(first.Link!)), HttpStatusCode.NotFound, "invitation-not-found");
        await ProblemAssert.HasCodeAsync(await PreviewAsync(invitee, TokenOf(resent.Link!)), HttpStatusCode.NotFound, "invitation-not-found");
        Assert.Equal(Role.Admin, (await ReadAsync<InvitationPreview>(await PreviewAsync(invitee, TokenOf(again.Link!)))).Role);
        Assert.Equal(first.Invitation.Id, again.Invitation.Id);
        Assert.Single(listed!, i => string.Equals(i.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_revoked_link_says_so_and_cannot_be_used()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var sent = await InviteAsync(owner, tenants.OrgA, BrowserClient.NewEmail());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendJsonAsync(HttpMethod.Delete, $"{Invitations(tenants.OrgA)}/{sent.Invitation.Id}", null)).StatusCode);

        using var invitee = new BrowserClient(tenants.Factory);
        var token = TokenOf(sent.Link!);
        Assert.Equal(InvitationState.Revoked, (await ReadAsync<InvitationPreview>(await PreviewAsync(invitee, token))).State);
        await ProblemAssert.HasCodeAsync(await SignUpAsync(invitee, token), HttpStatusCode.Conflict, "invitation-revoked");
        await ProblemAssert.HasCodeAsync(
            await owner.PostJsonAsync($"{Invitations(tenants.OrgA)}/{sent.Invitation.Id}/resend", new { }), HttpStatusCode.Conflict, "invitation-closed");
    }

    [Fact]
    public async Task An_expired_link_says_so_and_creates_no_account()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var email = BrowserClient.NewEmail();
        var sent = await InviteAsync(owner, tenants.OrgA, email);
        await ExpireAsync(tenants, sent.Invitation.Id);

        using var invitee = new BrowserClient(tenants.Factory);
        var token = TokenOf(sent.Link!);
        Assert.Equal(InvitationState.Expired, (await ReadAsync<InvitationPreview>(await PreviewAsync(invitee, token))).State);
        await ProblemAssert.HasCodeAsync(await SignUpAsync(invitee, token), HttpStatusCode.Conflict, "invitation-expired");

        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        Assert.False(await db.Users.AnyAsync(u => u.Email == email, Ct));
    }

    [Fact]
    public async Task Inviting_a_current_member_is_refused_and_admins_cannot_invite_owners()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var admin = await tenants.ClientForAsync(Role.Admin);

        await ProblemAssert.HasCodeAsync(
            await admin.PostJsonAsync(Invitations(tenants.OrgA), new { email = tenants.EmailsA[Role.Viewer].ToUpperInvariant(), role = "member" }),
            HttpStatusCode.Conflict,
            "already-member");
        await ProblemAssert.HasCodeAsync(
            await admin.PostJsonAsync(Invitations(tenants.OrgA), new { email = BrowserClient.NewEmail(), role = "owner" }),
            HttpStatusCode.BadRequest,
            "owner-not-invitable");
        await ProblemAssert.HasFieldErrorsAsync(await admin.PostJsonAsync(Invitations(tenants.OrgA), new { email = "not-an-email", role = "member" }), "email");
        await ProblemAssert.HasFieldErrorsAsync(await admin.PostJsonAsync(Invitations(tenants.OrgA), new { email = BrowserClient.NewEmail() }), "role");
    }

    [Fact]
    public async Task Malformed_and_unknown_tokens_are_simply_not_found()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        using var visitor = new BrowserClient(tenants.Factory);

        foreach (var token in new[] { "", "not a token", InvitationTokens.Create().Token, new string('A', 43) })
        {
            var response = await PreviewAsync(visitor, token);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{token}: {response.StatusCode}");
        }

        await ProblemAssert.HasCodeAsync(await PreviewAsync(visitor, InvitationTokens.Create().Token), HttpStatusCode.NotFound, "invitation-not-found");
        Assert.Equal(HttpStatusCode.Unauthorized, (await AcceptAsync(visitor, InvitationTokens.Create().Token)).StatusCode);
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored_anywhere()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var sent = await InviteAsync(owner, tenants.OrgA, BrowserClient.NewEmail());
        var token = TokenOf(sent.Link!);
        using var invitee = new BrowserClient(tenants.Factory);
        await ReadAsync<AcceptInvitationResponse>(await SignUpAsync(invitee, token));

        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var stored = await db.Invitations.AcrossTenants().AsNoTracking().SingleAsync(i => i.Id == sent.Invitation.Id, Ct);
        Assert.Equal(SHA256.HashData(Base64Url.DecodeFromChars(token)), stored.TokenHash);

        // Every row of every table that could hold it, as text: the token itself appears nowhere.
        var pattern = $"%{token}%";
        FormattableString[] tables =
        [
            $"SELECT count(*)::int AS \"Value\" FROM invitations t WHERE t::text LIKE {pattern}",
            $"SELECT count(*)::int AS \"Value\" FROM audit_entries t WHERE t::text LIKE {pattern}",
            $"SELECT count(*)::int AS \"Value\" FROM memberships t WHERE t::text LIKE {pattern}",
            $"SELECT count(*)::int AS \"Value\" FROM users t WHERE t::text LIKE {pattern}",
            $"SELECT count(*)::int AS \"Value\" FROM organizations t WHERE t::text LIKE {pattern}",
        ];
        foreach (var query in tables)
        {
            Assert.Equal(0, await db.Database.SqlQuery<int>(query).SingleAsync(Ct));
        }
    }

    [Fact]
    public async Task With_email_on_the_invitee_gets_an_email_and_the_token_never_reaches_the_logs()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(
            connectionString,
            new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Trace", ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace" },
            services => services.AddSingleton<IEmailSender>(emails));
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), "Harbor & Pine Landscaping", displayName: "Hana Okafor")).EnsureSuccessStatusCode();
        var org = (await owner.SessionAsync()).User!.Memberships.Single().OrganizationId;
        var email = BrowserClient.NewEmail();

        var sent = await InviteAsync(owner, org, email, "admin");
        var message = Assert.Single(emails.Sent);
        var token = TokenOf(message.TextBody.Split(' ', '\n', '\r').Single(w => w.StartsWith(PublicUrl, StringComparison.Ordinal)));
        using var invitee = new BrowserClient(factory);
        await ReadAsync<InvitationPreview>(await PreviewAsync(invitee, token));
        await ReadAsync<AcceptInvitationResponse>(await SignUpAsync(invitee, token, "Ida Novak"));
        await ProblemAssert.HasCodeAsync(await SignUpAsync(invitee, token), HttpStatusCode.Conflict, "invitation-used");
        await ReadAsync<AcceptInvitationResponse>(await AcceptAsync(invitee, token));

        Assert.Equal((InvitationDelivery.Sent, (string?)null), (sent.Delivery, sent.Link));
        Assert.Equal((email, "Hana Okafor invited you to Harbor & Pine Landscaping on TenantHub"), (message.To, message.Subject));
        Assert.Contains("Harbor &amp; Pine Landscaping", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"href=\"{PublicUrl}invite#{token}\"", message.HtmlBody, StringComparison.Ordinal);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.Contains(factory.Logs.Entries, e => e.Category == "scope" && e.Message.Contains("/api/invitations/sign-up", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_sending_fails_the_inviter_gets_the_link()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails { Fail = true };
        await using var factory = new ApiFactory(connectionString, services: services => services.AddSingleton<IEmailSender>(emails));
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), "Failing Mail Co")).EnsureSuccessStatusCode();
        var org = (await owner.SessionAsync()).User!.Memberships.Single().OrganizationId;

        var sent = await InviteAsync(owner, org, BrowserClient.NewEmail());

        Assert.Equal(InvitationDelivery.Failed, sent.Delivery);
        using var invitee = new BrowserClient(factory);
        Assert.Equal(InvitationState.Pending, (await ReadAsync<InvitationPreview>(await PreviewAsync(invitee, TokenOf(sent.Link!)))).State);
    }

    [Fact]
    public async Task Two_simultaneous_sign_ups_with_one_link_create_one_account()
    {
        var tenants = await SharedApiTenants.GetAsync<InvitationFlowTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var email = BrowserClient.NewEmail();
        var token = TokenOf((await InviteAsync(owner, tenants.OrgA, email)).Link!);
        using var first = new BrowserClient(tenants.Factory);
        using var second = new BrowserClient(tenants.Factory);
        await first.SessionAsync();
        await second.SessionAsync();

        var responses = await Task.WhenAll(SignUpAsync(first, token, "First"), SignUpAsync(second, token, "Second"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email, Ct));
    }

    [Fact]
    public async Task Sending_invitations_and_using_links_are_rate_limited()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["RateLimiting:InvitationPermitLimit"] = "2",
        });
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), "Busy Inviters Co")).EnsureSuccessStatusCode();
        var org = (await owner.SessionAsync()).User!.Memberships.Single().OrganizationId;
        using var visitor = new BrowserClient(factory);
        var token = InvitationTokens.Create().Token;

        // Per user for the inviter, per client for the link: one does not use up the other.
        var sends = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
        {
            sends.Add(await owner.PostJsonAsync(Invitations(org), new { email = BrowserClient.NewEmail(), role = "member" }));
        }

        var looks = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
        {
            looks.Add(await PreviewAsync(visitor, token));
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], sends.Select(r => r.StatusCode));
        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests], looks.Select(r => r.StatusCode));
        Assert.True(sends[2].Headers.RetryAfter is not null, "Retry-After header missing.");
    }

    private static async Task ExpireAsync(ApiTenants tenants, Guid invitationId)
    {
        await using var lookup = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var ownerId = await lookup.Users.Where(u => u.Email == tenants.EmailsA[Role.Owner]).Select(u => u.Id).SingleAsync(Ct);
        var tenant = new TenantContext();
        tenant.Enter(tenants.OrgA, ownerId, Role.Owner);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString, tenant);
        var invitation = await db.Invitations.SingleAsync(i => i.Id == invitationId, Ct);
        db.Entry(invitation).Property(i => i.ExpiresAt).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync(Ct);
    }
}
