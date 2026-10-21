using System.Net;
using System.Net.Http.Json;
using TenantHub.Api.Features.Auth;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Account;

public sealed class AccountTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Display_name_can_be_changed_and_is_validated()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        await browser.SignUpAsync(BrowserClient.NewEmail());

        var response = await browser.PatchJsonAsync("/api/me", new { displayName = "  Olivia B.  " });
        var invalid = await browser.PatchJsonAsync("/api/me", new { displayName = "   " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(BrowserClient.Json, Ct);
        Assert.Equal("Olivia B.", session!.User!.DisplayName);
        await ProblemAssert.HasFieldErrorsAsync(invalid, "displayName");
    }

    [Fact]
    public async Task Changing_the_password_ends_other_sessions_but_keeps_this_one()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var laptop = new BrowserClient(factory);
        using var phone = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await laptop.SignUpAsync(email)).EnsureSuccessStatusCode();
        (await phone.SignInAsync(email)).EnsureSuccessStatusCode();
        Assert.NotNull((await phone.SessionAsync()).User);

        var change = await laptop.PostJsonAsync("/api/me/password", new
        {
            currentPassword = BrowserClient.Password,
            newPassword = "Battery-Staple-42",
        });

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.NotNull((await laptop.SessionAsync()).User);
        Assert.Null((await phone.SessionAsync()).User);
        Assert.Equal(HttpStatusCode.OK, (await phone.SignInAsync(email, "Battery-Staple-42")).StatusCode);
    }

    [Fact]
    public async Task Wrong_current_password_and_weak_new_password_are_field_errors()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        await browser.SignUpAsync(BrowserClient.NewEmail());

        var wrongCurrent = await browser.PostJsonAsync("/api/me/password", new { currentPassword = "Nope-Nope-99", newPassword = "Battery-Staple-42" });
        var weakNew = await browser.PostJsonAsync("/api/me/password", new { currentPassword = BrowserClient.Password, newPassword = "short" });

        await ProblemAssert.HasFieldErrorsAsync(wrongCurrent, "currentPassword");
        await ProblemAssert.HasFieldErrorsAsync(weakNew, "newPassword");
    }
}
