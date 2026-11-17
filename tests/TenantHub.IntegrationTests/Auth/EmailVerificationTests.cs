using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Accounts;
using TenantHub.Infrastructure.Identity;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Auth;

/// <summary>
/// Email verification (US-06, SPEC-V2 4.1): with email on, a new account is asked to confirm its address and cannot
/// invite until it does; with email off nothing is asked. Accounts made from an invitation link start verified.
/// </summary>
public sealed class EmailVerificationTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_account_cannot_invite_until_it_follows_the_emailed_link()
    {
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<IEmailSender>(emails));
        using var owner = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await owner.SignUpAsync(email, displayName: "Hana Okafor")).EnsureSuccessStatusCode();
        var session = (await owner.SessionAsync()).User!;
        var org = session.Memberships.Single().OrganizationId;

        Assert.False(session.EmailVerified);
        await ProblemAssert.HasCodeAsync(await InviteAsync(owner, org), HttpStatusCode.Forbidden, EmailVerification.NotVerifiedCode);

        var message = await EmailLinks.LatestAsync(factory, emails, email, EmailLinks.VerificationSubject);
        Assert.Contains("Hello Hana Okafor,", message.TextBody, StringComparison.Ordinal);
        Assert.Contains($"href=\"https://app.tenanthub.example/verify-email#", message.HtmlBody, StringComparison.Ordinal);
        using var elsewhere = new BrowserClient(factory); // the link works in any browser, signed in or not
        Assert.Equal(HttpStatusCode.NoContent, (await VerifyAsync(elsewhere, EmailLinks.TokenFrom(message, "verify-email"))).StatusCode);

        Assert.True((await owner.SessionAsync()).User!.EmailVerified);
        Assert.Equal(HttpStatusCode.OK, (await InviteAsync(owner, org)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await VerifyAsync(elsewhere, EmailLinks.TokenFrom(message, "verify-email"))).StatusCode); // twice is fine
    }

    [Fact]
    public async Task With_email_off_nothing_is_asked_and_nothing_is_written()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString);
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail())).EnsureSuccessStatusCode();
        var session = (await owner.SessionAsync()).User!;

        Assert.True(session.EmailVerified);
        Assert.Equal(HttpStatusCode.OK, (await InviteAsync(owner, session.Memberships.Single().OrganizationId)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await ResendAsync(owner)).StatusCode);
        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await check.OutboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Send_again_is_rate_limited_and_stops_once_verified()
    {
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(
            await postgres.CreateDatabaseAsync(),
            new Dictionary<string, string?> { ["RateLimiting:AccountEmailPermitLimit"] = "2" },
            s => s.AddSingleton<IEmailSender>(emails));
        using var owner = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await owner.SignUpAsync(email)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Accepted, (await ResendAsync(owner)).StatusCode);
        await factory.DispatchOutboxAsync();
        Assert.Equal(2, emails.Sent.Count(m => m.Subject == EmailLinks.VerificationSubject)); // sign-up's and the resend
        await EmailLinks.VerifyAsync(factory, emails, owner, email);
        Assert.Equal(HttpStatusCode.Accepted, (await ResendAsync(owner)).StatusCode);
        await ProblemAssert.IsProblemAsync(await ResendAsync(owner), HttpStatusCode.TooManyRequests);

        await factory.DispatchOutboxAsync();
        Assert.Equal(2, emails.Sent.Count(m => m.Subject == EmailLinks.VerificationSubject)); // nothing after verifying
        Assert.Equal(HttpStatusCode.Unauthorized, (await new BrowserClient(factory).PostJsonAsync("/api/me/verification-email", new { })).StatusCode);
    }

    [Fact]
    public async Task Altered_and_expired_links_are_refused()
    {
        var emails = new CapturedEmails();
        var clock = new MovingClock(Start);
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<IEmailSender>(emails).AddSingleton<TimeProvider>(clock));
        using var owner = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await owner.SignUpAsync(email)).EnsureSuccessStatusCode();
        var token = EmailLinks.TokenFrom(await EmailLinks.LatestAsync(factory, emails, email, EmailLinks.VerificationSubject), "verify-email");

        await ProblemAssert.HasCodeAsync(await VerifyAsync(owner, token[..^2] + (token[^2] == 'A' ? "BB" : "AA")), HttpStatusCode.BadRequest, EmailVerification.InvalidLinkCode);
        clock.Advance(AccountTokenProvider.VerificationLifespan);
        await ProblemAssert.HasCodeAsync(await VerifyAsync(owner, token), HttpStatusCode.BadRequest, EmailVerification.InvalidLinkCode);
        await ProblemAssert.HasFieldErrorsAsync(await owner.PostJsonAsync("/api/auth/verify-email", new { token = "" }), "token");
        Assert.False((await owner.SessionAsync()).User!.EmailVerified);
    }

    [Fact]
    public async Task An_account_made_from_an_invitation_link_starts_verified()
    {
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<IEmailSender>(emails));
        using var owner = new BrowserClient(factory);
        var ownerEmail = BrowserClient.NewEmail();
        (await owner.SignUpAsync(ownerEmail)).EnsureSuccessStatusCode();
        await EmailLinks.VerifyAsync(factory, emails, owner, ownerEmail);
        var inviteeEmail = BrowserClient.NewEmail();
        (await InviteAsync(owner, (await owner.SessionAsync()).User!.Memberships.Single().OrganizationId, inviteeEmail)).EnsureSuccessStatusCode();
        var token = EmailLinks.TokenFrom(emails.Sent.Single(m => m.To == inviteeEmail), "invite");

        using var invitee = new BrowserClient(factory);
        (await invitee.PostJsonAsync("/api/invitations/sign-up", new { token, displayName = "Ida Novak", password = BrowserClient.Password })).EnsureSuccessStatusCode();

        Assert.True((await invitee.SessionAsync()).User!.EmailVerified);
        await factory.DispatchOutboxAsync();
        Assert.DoesNotContain(emails.Sent, m => m.To == inviteeEmail && m.Subject == EmailLinks.VerificationSubject);
    }

    [Fact]
    public async Task A_failed_sign_up_leaves_no_verification_email()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(connectionString, services: s => s.AddSingleton<IEmailSender>(emails));
        using var browser = new BrowserClient(factory);

        await ProblemAssert.HasFieldErrorsAsync(await browser.SignUpAsync(BrowserClient.NewEmail(), timeZone: "Mars/Olympus_Mons"), "timeZone");

        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await check.OutboxMessages.CountAsync(Ct));
        Assert.Equal(0, await check.Users.CountAsync(Ct));
    }

    private static Task<HttpResponseMessage> InviteAsync(BrowserClient owner, Guid org, string? email = null) =>
        owner.PostJsonAsync($"/api/orgs/{org}/invitations", new { email = email ?? BrowserClient.NewEmail(), role = "member" });

    private static Task<HttpResponseMessage> VerifyAsync(BrowserClient browser, string token) =>
        browser.PostJsonAsync("/api/auth/verify-email", new { token });

    private static Task<HttpResponseMessage> ResendAsync(BrowserClient browser) =>
        browser.PostJsonAsync("/api/me/verification-email", new { });
}
