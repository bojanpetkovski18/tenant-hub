using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Accounts;
using TenantHub.Infrastructure.Identity;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Auth;

/// <summary>Password reset by email (US-05, SPEC-V2 4.1): same answer for every address, single use, 30 minutes, every session ends.</summary>
public sealed class PasswordResetTests(PostgresFixture postgres)
{
    private const string NewPassword = "Fresh-Start-42";
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Known_and_unknown_emails_get_the_same_answer_in_the_same_time_class()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(connectionString, services: s => s.AddSingleton<IEmailSender>(emails));
        using var browser = new BrowserClient(factory);
        var email = await SignUpAsync(browser);
        var known = new List<double>();
        var unknown = new List<double>();

        // Alternating, after a warm-up; the cooldown means only the first known request writes a message, so the
        // known path is measured both with and without the write.
        await RequestAsync(browser, BrowserClient.NewEmail());
        for (var i = 0; i < 15; i++)
        {
            known.Add(await TimeAsync(() => RequestAsync(browser, email.ToUpperInvariant())));
            unknown.Add(await TimeAsync(() => RequestAsync(browser, BrowserClient.NewEmail())));
        }

        var answers = new[] { await RequestAsync(browser, email), await RequestAsync(browser, "nobody@test.example"), await RequestAsync(browser, "not an email") };
        foreach (var answer in answers)
        {
            Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode);
            Assert.Equal("", await answer.Content.ReadAsStringAsync(Ct));
        }

        // Neither path hashes a password or talks to a mail server (each takes tens of milliseconds); the difference
        // that is left is one small insert.
        var (knownMedian, unknownMedian) = (Median(known), Median(unknown));
        TestContext.Current.TestOutputHelper?.WriteLine($"Median ms: known {knownMedian:F1}, unknown {unknownMedian:F1}");
        Assert.True(Math.Abs(knownMedian - unknownMedian) < 25, $"known {knownMedian:F1} ms, unknown {unknownMedian:F1} ms");
        Assert.Empty(emails.Sent); // nothing is sent during the request
        await factory.DispatchOutboxAsync();
        Assert.Single(emails.Sent, m => m.To == email && m.Subject == EmailLinks.ResetSubject);
    }

    [Fact]
    public async Task The_link_sets_a_new_password_once_and_ends_every_session()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(connectionString, services: s => s.AddSingleton<IEmailSender>(emails));
        using var signedIn = new BrowserClient(factory);
        var email = await SignUpAsync(signedIn);
        using var otherDevice = new BrowserClient(factory);
        (await otherDevice.SignInAsync(email)).EnsureSuccessStatusCode();
        using var resetter = new BrowserClient(factory);

        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(resetter, email)).StatusCode);
        var token = EmailLinks.TokenFrom(await LatestAsync(factory, emails, email), "reset-password");
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(resetter, token, NewPassword)).StatusCode);

        Assert.Null((await signedIn.SessionAsync()).User);
        Assert.Null((await otherDevice.SessionAsync()).User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await resetter.SignInAsync(email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await resetter.SignInAsync(email, NewPassword)).StatusCode);
        await ProblemAssert.HasCodeAsync(await ConfirmAsync(resetter, token, "Another-Start-43"), HttpStatusCode.BadRequest, ResetPasswordHandler.InvalidLinkCode);
    }

    [Fact]
    public async Task The_link_expires_after_30_minutes()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        var clock = new MovingClock(Start);
        await using var factory = new ApiFactory(connectionString, services: s => s.AddSingleton<IEmailSender>(emails).AddSingleton<TimeProvider>(clock));
        using var browser = new BrowserClient(factory);
        var email = await SignUpAsync(browser);
        await RequestAsync(browser, email);
        var token = EmailLinks.TokenFrom(await LatestAsync(factory, emails, email), "reset-password");

        clock.Advance(AccountTokenProvider.ResetLifespan);

        await ProblemAssert.HasCodeAsync(await ConfirmAsync(browser, token, NewPassword), HttpStatusCode.BadRequest, ResetPasswordHandler.InvalidLinkCode);
        clock.Advance(-TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(browser, token, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task A_weak_password_is_refused_and_the_link_still_works()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(connectionString, services: s => s.AddSingleton<IEmailSender>(emails));
        using var browser = new BrowserClient(factory);
        var email = await SignUpAsync(browser);
        await RequestAsync(browser, email);
        var token = EmailLinks.TokenFrom(await LatestAsync(factory, emails, email), "reset-password");

        await ProblemAssert.HasFieldErrorsAsync(await ConfirmAsync(browser, token, "short"), "password");
        await ProblemAssert.HasCodeAsync(await ConfirmAsync(browser, "not-a-token", NewPassword), HttpStatusCode.BadRequest, ResetPasswordHandler.InvalidLinkCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmAsync(browser, token, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task A_reset_ends_a_lockout()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(connectionString, services: s => s.AddSingleton<IEmailSender>(emails));
        using var browser = new BrowserClient(factory);
        var email = await SignUpAsync(browser);
        await browser.SignOutAsync();
        for (var i = 0; i < 5; i++)
        {
            await browser.SignInAsync(email, "Wrong-Horse-9");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SignInAsync(email)).StatusCode); // locked out
        await RequestAsync(browser, email);
        await ConfirmAsync(browser, EmailLinks.TokenFrom(await LatestAsync(factory, emails, email), "reset-password"), NewPassword);

        Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync(email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task One_email_per_account_per_cooldown_however_often_it_is_asked_for()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        var clock = new MovingClock(Start);
        await using var factory = new ApiFactory(connectionString, services: s => s.AddSingleton<IEmailSender>(emails).AddSingleton<TimeProvider>(clock));
        using var browser = new BrowserClient(factory);
        var email = await SignUpAsync(browser);

        await RequestAsync(browser, email);
        await RequestAsync(browser, email);
        clock.Advance(RequestPasswordResetHandler.Cooldown);
        await RequestAsync(browser, email);
        await factory.DispatchOutboxAsync();

        Assert.Equal(2, emails.Sent.Count(m => m.Subject == EmailLinks.ResetSubject));
    }

    [Fact]
    public async Task Requests_are_rate_limited_per_address()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string?> { ["RateLimiting:AccountEmailPermitLimit"] = "2" });
        using var browser = new BrowserClient(factory);

        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(browser, BrowserClient.NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(browser, BrowserClient.NewEmail())).StatusCode);
        await ProblemAssert.IsProblemAsync(await RequestAsync(browser, BrowserClient.NewEmail()), HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task With_email_off_nothing_is_written()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString);
        using var browser = new BrowserClient(factory);
        var email = await SignUpAsync(browser);

        Assert.Equal(HttpStatusCode.Accepted, (await RequestAsync(browser, email)).StatusCode);

        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await check.OutboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task The_token_never_reaches_the_logs_or_the_outbox_row_in_clear_text()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        await using var factory = new ApiFactory(
            connectionString,
            new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Trace", ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace" },
            s => s.AddSingleton<IEmailSender>(emails));
        using var browser = new BrowserClient(factory);
        var email = await SignUpAsync(browser);
        await RequestAsync(browser, email);

        string payload;
        await using (var check = PostgresFixture.CreateDbContext(connectionString))
        {
            payload = (await check.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == AccountEmails.PasswordReset.Name, Ct)).Payload;
        }

        var message = await LatestAsync(factory, emails, email);
        var token = EmailLinks.TokenFrom(message, "reset-password");
        await ConfirmAsync(browser, token, NewPassword);

        Assert.DoesNotContain(token, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("reset-password", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(email, payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(factory.Logs.Entries, e => e.Message.Contains("/api/auth/password-reset/confirm", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains(Uri.EscapeDataString(token), StringComparison.Ordinal));
    }

    private static async Task<string> SignUpAsync(BrowserClient browser)
    {
        var email = BrowserClient.NewEmail();
        (await browser.SignUpAsync(email)).EnsureSuccessStatusCode();
        return email;
    }

    private static Task<HttpResponseMessage> RequestAsync(BrowserClient browser, string email) =>
        browser.PostJsonAsync("/api/auth/password-reset/request", new { email });

    private static Task<HttpResponseMessage> ConfirmAsync(BrowserClient browser, string token, string password) =>
        browser.PostJsonAsync("/api/auth/password-reset/confirm", new { token, password });

    private static Task<EmailMessage> LatestAsync(ApiFactory factory, CapturedEmails emails, string email) =>
        EmailLinks.LatestAsync(factory, emails, email, EmailLinks.ResetSubject);

    private static async Task<double> TimeAsync(Func<Task<HttpResponseMessage>> request)
    {
        var watch = Stopwatch.StartNew();
        (await request()).Dispose();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
}
