using TenantHub.Domain.Integrations;
using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Tests.Integrations;

/// <summary>SPEC-V2 2.4 and 5.8: what a key may do and when it works; what an endpoint accepts and when it gives up.</summary>
public sealed class IntegrationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.NewGuid();

    [Fact]
    public void A_key_has_a_fixed_set_of_permissions_and_never_administration()
    {
        Assert.Equal(
            ["agreements.read", "clients.read", "jobs.read"],
            ApiKeyPermissions.For(ApiKeyAccess.ReadOnly).Order());
        Assert.Equal(
            ["agreements.read", "clients.read", "clients.write", "jobs.create", "jobs.manage", "jobs.read", "jobs.work"],
            ApiKeyPermissions.For(ApiKeyAccess.ReadWrite).Order());
        string[] never = [Permissions.MembersManage, Permissions.BillingManage, Permissions.OrgManage, Permissions.OrgDelete, Permissions.AuditRead, Permissions.PortalManage, Permissions.IntegrationsManage];
        Assert.All(never, p => Assert.DoesNotContain(p, ApiKeyPermissions.For(ApiKeyAccess.ReadWrite)));
    }

    [Fact]
    public void A_key_needs_a_name_an_access_level_and_an_expiry_within_a_year()
    {
        var invalid = Assert.Throws<DomainValidationException>(() => Issue(name: "  ", access: null, expiresAt: Now.AddDays(367)));
        Assert.Equal(["access", "expiresAt", "name"], invalid.Errors.Keys.Order());
        Assert.Throws<DomainValidationException>(() => Issue(expiresAt: Now));
        Assert.Throws<DomainValidationException>(() => Issue(name: new string('k', ApiKey.NameMaxLength + 1)));

        var key = Issue(name: "  Accounting   sync ", expiresAt: Now.AddDays(366));
        Assert.Equal("Accounting sync", key.Name);
        Assert.Equal("API key Accounting sync", key.ActorName);
        var created = Assert.IsType<ApiKeyEvent>(Assert.Single(key.DomainEvents));
        Assert.Equal("api_key.created", created.Action);
        Assert.DoesNotContain("thk_secret", created.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_works_until_it_expires_or_is_revoked_and_is_revoked_once()
    {
        var expiring = Issue(expiresAt: Now.AddDays(1));
        Assert.True(expiring.IsUsable(Now.AddHours(23)));
        Assert.False(expiring.IsUsable(Now.AddDays(1)));

        var key = Issue();
        Assert.True(key.IsUsable(Now.AddYears(5)));
        key.Revoke(Now);
        Assert.False(key.IsUsable(Now));
        Assert.Equal("api-key-revoked", Assert.Throws<DomainException>(() => key.Revoke(Now)).Code);
    }

    [Theory]
    [InlineData("http://hooks.example/")]
    [InlineData("ftp://hooks.example/")]
    [InlineData("https://user:secret@hooks.example/")]
    [InlineData("hooks.example/path")]
    [InlineData("")]
    public void An_endpoint_url_is_https_and_carries_no_credentials(string url) =>
        Assert.Equal(["url"], Assert.Throws<DomainValidationException>(() => WebhookEndpoint.ValidateUrl(url)).Errors.Keys);

    [Fact]
    public void The_development_receiver_may_be_plain_http()
    {
        Assert.Equal("http", WebhookEndpoint.ValidateUrl("http://127.0.0.1:5099/hooks", allowHttpFor: true).Scheme);
        Assert.Equal("https", WebhookEndpoint.ValidateUrl(" https://hooks.example/a ").Scheme);
    }

    [Fact]
    public void Events_come_from_the_catalog_in_catalog_order()
    {
        Assert.Equal(["job.created", "quote.approved"], WebhookEndpoint.ValidateEvents(["quote.approved", "job.created", "job.created"]));
        Assert.Throws<DomainValidationException>(() => WebhookEndpoint.ValidateEvents([]));
        Assert.Throws<DomainValidationException>(() => WebhookEndpoint.ValidateEvents(["job.deleted"]));
        Assert.Throws<DomainValidationException>(() => WebhookEndpoint.ValidateEvents([WebhookEvents.Ping]));
    }

    [Fact]
    public void An_organization_has_at_most_five_endpoints()
    {
        Add(existing: WebhookEndpoint.MaxPerOrganization - 1);
        Assert.Equal("webhook-limit-reached", Assert.Throws<DomainException>(() => Add(existing: WebhookEndpoint.MaxPerOrganization)).Code);
    }

    [Fact]
    public void A_rotated_secret_keeps_signing_for_a_day()
    {
        var endpoint = Add();
        endpoint.RotateSecret("new-encrypted", Now);

        Assert.Equal("new-encrypted", endpoint.SecretEncrypted);
        Assert.Equal("old-encrypted", endpoint.PreviousSecretAt(Now.AddHours(23)));
        Assert.Null(endpoint.PreviousSecretAt(Now.AddHours(24)));
    }

    [Fact]
    public void Three_days_of_failures_disable_an_endpoint_once_and_a_success_starts_the_count_afresh()
    {
        var endpoint = Add();
        Assert.False(endpoint.RecordFailure(Now));
        Assert.False(endpoint.RecordFailure(Now.AddDays(2)));
        endpoint.RecordSuccess();
        Assert.Null(endpoint.FailingSince);

        Assert.False(endpoint.RecordFailure(Now.AddDays(2)));
        Assert.False(endpoint.RecordFailure(Now.AddDays(5) - TimeSpan.FromSeconds(1)));
        Assert.True(endpoint.RecordFailure(Now.AddDays(5)));
        Assert.False(endpoint.IsEnabled);
        Assert.False(endpoint.RecordFailure(Now.AddDays(6)));
        var disabled = Assert.Single(endpoint.DomainEvents.OfType<WebhookEndpointEvent>(), e => e.Action == "webhook.disabled");
        Assert.Equal(ActorKind.System, disabled.Actor?.Kind);

        endpoint.Update(new Uri(endpoint.Url), endpoint.Events, enabled: true, Now.AddDays(7));
        Assert.True(endpoint.IsEnabled);
        Assert.Null(endpoint.FailingSince);
    }

    [Fact]
    public void The_audit_names_the_host_only_never_the_path()
    {
        var endpoint = WebhookEndpoint.Add(Org, new Uri("https://hooks.example/t/secret-token"), ["job.created"], "x", 0, Now);

        var created = Assert.IsType<WebhookEndpointEvent>(Assert.Single(endpoint.DomainEvents));
        Assert.Contains("hooks.example", created.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", created.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delivery_keeps_at_most_a_short_error_class()
    {
        var endpoint = Add();
        var delivery = WebhookDelivery.Record(endpoint, Guid.NewGuid(), "job.created", "{}", 1, null, 10, new string('e', 300), null, Now);

        Assert.Equal(WebhookDelivery.ErrorMaxLength, delivery.Error!.Length);
        Assert.Null(delivery.SucceededAt);
        Assert.Equal(Now, WebhookDelivery.Record(endpoint, Guid.NewGuid(), "job.created", "{}", 1, 204, 10, null, null, Now).SucceededAt);
    }

    private static ApiKey Issue(string? name = "Booking site", ApiKeyAccess? access = ApiKeyAccess.ReadWrite, DateTimeOffset? expiresAt = null) =>
        ApiKey.Issue(Org, name, access, expiresAt, "thk_abcdefgh", new byte[32], Guid.NewGuid(), Now);

    private static WebhookEndpoint Add(int existing = 0) =>
        WebhookEndpoint.Add(Org, new Uri("https://hooks.example/a"), ["job.created"], "old-encrypted", existing, Now);
}
