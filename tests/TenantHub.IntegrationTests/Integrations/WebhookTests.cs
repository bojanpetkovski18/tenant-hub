using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Integrations;
using TenantHub.Application.Notifications;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Integrations;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Notifications;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Background;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Integrations;

/// <summary>
/// US-111 and SPEC-V2 5.8, end to end through a real HTTP receiver: events arrive signed as documented (checked here by
/// an implementation of the documented algorithm, not the product's signer), with an id that never changes; failures
/// retry on the webhook schedule; three days of failures disable the endpoint and tell its Owners; one message is
/// delivered by one dispatcher at a time; the log keeps no response body.
/// </summary>
public sealed class WebhookTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_event_arrives_signed_as_documented_with_the_documented_payload()
    {
        await using var setup = await SetupAsync();
        var (endpointId, secret) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);

        var jobId = await CreateJobAsync(setup, "Window cleaning, third floor");
        await setup.Tenants.Factory.DispatchOutboxAsync();

        var received = Assert.Single(setup.Receiver.Requests);
        Assert.Equal("/hooks/tenant-hub", received.Path);
        Assert.StartsWith("application/json", received.ContentType, StringComparison.Ordinal);
        var t = VerifiedTimestamp(received.Signature, received.Body, [secret]);
        Assert.Equal(setup.Clock.GetUtcNow().ToUnixTimeSeconds(), t);

        using var payload = JsonDocument.Parse(received.Body);
        var root = payload.RootElement;
        Assert.Equal(received.EventId, root.GetProperty("id").GetString());
        Assert.Equal("job.created", root.GetProperty("type").GetString());
        Assert.Equal(setup.Tenants.OrgA, root.GetProperty("organizationId").GetGuid());
        Assert.True(root.TryGetProperty("occurredAt", out _));
        var data = root.GetProperty("data");
        Assert.Equal(jobId, data.GetProperty("id").GetGuid());
        Assert.Equal("Window cleaning, third floor", data.GetProperty("title").GetString());
        Assert.Equal("new", data.GetProperty("status").GetString());
        Assert.Equal(setup.Tenants.ClientA, data.GetProperty("clientId").GetGuid());

        var log = await DeliveriesAsync(setup, endpointId);
        var attempt = Assert.Single(log);
        Assert.Equal((1, 200, true, (string?)null), (attempt.Attempt, attempt.StatusCode, attempt.Succeeded, attempt.Error));
    }

    [Fact]
    public async Task Only_subscribed_events_are_sent_and_a_status_change_is_its_own_event()
    {
        await using var setup = await SetupAsync();
        await AddEndpointAsync(setup, [WebhookEvents.JobStatusChanged]);

        var jobId = await CreateJobAsync(setup, "Gutter check");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        Assert.Empty(setup.Receiver.Requests);

        (await setup.Owner.PostJsonAsync($"/api/orgs/{setup.Tenants.OrgA}/jobs/{jobId}/transitions", new { to = "cancelled", reason = "The client moved away" })).EnsureSuccessStatusCode();
        await setup.Tenants.Factory.DispatchOutboxAsync();

        var received = Assert.Single(setup.Receiver.Requests);
        using var payload = JsonDocument.Parse(received.Body);
        Assert.Equal("job.status_changed", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("cancelled", payload.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task After_a_rotation_both_secrets_sign_for_a_day_then_only_the_new_one()
    {
        await using var setup = await SetupAsync();
        var (endpointId, oldSecret) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);

        var rotated = await setup.Owner.PostJsonAsync($"/api/orgs/{setup.Tenants.OrgA}/webhooks/{endpointId}/rotate-secret", new { });
        var newSecret = (await rotated.Content.ReadFromJsonAsync<WithSecret>(BrowserClient.Json, Ct))!.Secret;
        Assert.NotEqual(oldSecret, newSecret);

        await CreateJobAsync(setup, "During the overlap");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        var during = Assert.Single(setup.Receiver.Requests);
        Assert.Equal(2, during.Signature.Split(',').Count(p => p.StartsWith("v1=", StringComparison.Ordinal)));
        VerifiedTimestamp(during.Signature, during.Body, [oldSecret]);
        VerifiedTimestamp(during.Signature, during.Body, [newSecret]);

        setup.Clock.Advance(WebhookEndpoint.RotationOverlap + TimeSpan.FromMinutes(1));
        setup.Receiver.Requests.Clear();
        await CreateJobAsync(setup, "After the overlap");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        var after = Assert.Single(setup.Receiver.Requests);
        Assert.Single(after.Signature.Split(','), p => p.StartsWith("v1=", StringComparison.Ordinal));
        VerifiedTimestamp(after.Signature, after.Body, [newSecret]);
        Assert.Throws<SignatureMismatch>(() => VerifiedTimestamp(after.Signature, after.Body, [oldSecret]));
    }

    [Fact]
    public async Task Failures_retry_eight_times_over_about_a_day_with_the_same_event_id_and_body()
    {
        await using var setup = await SetupAsync();
        var (endpointId, _) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        setup.Receiver.Status = 503;

        await CreateJobAsync(setup, "Retried until it gives up");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        TimeSpan[] expected = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8), TimeSpan.FromHours(10)];
        Assert.Equal(expected, WebhookDeliveryMessage.Deliver.RetryDelays);
        foreach (var delay in expected)
        {
            var message = await DeliveryMessageAsync(setup);
            Assert.Equal(setup.Clock.GetUtcNow() + delay, message.AvailableAt);

            // Not a moment early: nothing is due a second before.
            setup.Clock.Advance(delay - TimeSpan.FromSeconds(1));
            await setup.Tenants.Factory.DispatchOutboxAsync();
            setup.Clock.Advance(TimeSpan.FromSeconds(1));
            await setup.Tenants.Factory.DispatchOutboxAsync();
        }

        var requests = setup.Receiver.Requests.ToList();
        Assert.Equal(8, requests.Count);
        Assert.Single(requests.Select(r => r.EventId).Distinct());
        Assert.Single(requests.Select(r => r.Body).Distinct());
        var dead = await DeliveryMessageAsync(setup);
        Assert.NotNull(dead.DeadAt);
        Assert.Equal("http-503", dead.LastError);

        var log = await DeliveriesAsync(setup, endpointId);
        Assert.Equal(Enumerable.Range(1, 8).Reverse(), log.Select(d => d.Attempt));
        Assert.All(log.Skip(1), d => Assert.NotNull(d.NextAttemptAt));
        Assert.Null(log[0].NextAttemptAt);
        Assert.All(log, d => Assert.Equal((503, "http-503", false), (d.StatusCode!.Value, d.Error!, d.Succeeded)));

        // About a day, and the endpoint is still enabled: the window is three days.
        Assert.InRange(expected.Aggregate(TimeSpan.Zero, (a, b) => a + b), TimeSpan.FromHours(20), TimeSpan.FromHours(28));
        Assert.True((await EndpointsAsync(setup)).Single(e => e.Id == endpointId).Enabled);
    }

    [Fact]
    public async Task Three_days_of_failures_disable_the_endpoint_and_tell_its_Owners()
    {
        await using var setup = await SetupAsync();
        var (endpointId, _) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        setup.Receiver.Status = 500;
        await TenantHub.IntegrationTests.Notifications.NotificationTests.ClearNotificationsAsync(setup.Tenants.ConnectionString);

        await CreateJobAsync(setup, "First failure");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        setup.Clock.Advance(TimeSpan.FromDays(2));
        await CreateJobAsync(setup, "Still failing");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        Assert.True((await EndpointsAsync(setup)).Single(e => e.Id == endpointId).Enabled);

        setup.Clock.Advance(TimeSpan.FromDays(1));
        await CreateJobAsync(setup, "Three days on");
        await setup.Tenants.Factory.DispatchOutboxAsync(); // delivers, fails, disables
        await setup.Tenants.Factory.DispatchOutboxAsync(); // the notification's fan-out

        var endpoint = (await EndpointsAsync(setup)).Single(e => e.Id == endpointId);
        Assert.False(endpoint.Enabled);
        await using var db = PostgresFixture.CreateDbContext(setup.Tenants.ConnectionString);
        var disabled = await db.AuditEntries.AcrossTenants().SingleAsync(a => a.EntityId == endpointId && a.Action == "webhook.disabled", Ct);
        Assert.Equal(ActorKind.System, disabled.ActorKind);
        var notified = await db.Notifications.AcrossTenants()
            .Where(n => n.OrganizationId == setup.Tenants.OrgA && n.Type == NotificationType.WebhookDisabled)
            .Select(n => n.UserId)
            .ToListAsync(Ct);
        var owners = await db.Memberships.AcrossTenants()
            .Where(m => m.OrganizationId == setup.Tenants.OrgA && m.Role == Role.Owner && m.RemovedAt == null)
            .Select(m => m.UserId)
            .ToListAsync(Ct);
        Assert.Equal(owners.Order(), notified.Order());
        Assert.Equal(2, owners.Count); // the fixture has two Owners; the Admin is not told

        // Nothing more is sent; enabling it again starts afresh.
        var sent = setup.Receiver.Requests.Count;
        await CreateJobAsync(setup, "While disabled");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        Assert.Equal(sent, setup.Receiver.Requests.Count);
        setup.Receiver.Status = 200;
        (await setup.Owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{setup.Tenants.OrgA}/webhooks/{endpointId}", new { url = new Uri(setup.Receiver.Url, "hooks/tenant-hub").ToString(), events = new[] { "job.created" }, enabled = true })).EnsureSuccessStatusCode();
        await CreateJobAsync(setup, "Enabled again");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        Assert.Equal(sent + 1, setup.Receiver.Requests.Count);
    }

    [Fact]
    public async Task Two_dispatchers_never_deliver_one_message_twice()
    {
        await using var setup = await SetupAsync();
        await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        await CreateJobAsync(setup, "One at a time");
        var dispatcher = setup.Tenants.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(Ct); // the fan-out queues the delivery
        setup.Receiver.Delay = TimeSpan.FromSeconds(1);

        await Task.WhenAll(dispatcher.DispatchBatchAsync(Ct), dispatcher.DispatchBatchAsync(Ct), dispatcher.DispatchBatchAsync(Ct));

        Assert.Single(setup.Receiver.Requests);
    }

    [Fact]
    public async Task Deliver_again_sends_the_same_event_and_the_test_event_is_a_ping()
    {
        await using var setup = await SetupAsync();
        var (endpointId, secret) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        await CreateJobAsync(setup, "Sent twice on purpose");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        var first = Assert.Single(setup.Receiver.Requests);
        var delivery = Assert.Single(await DeliveriesAsync(setup, endpointId));

        var again = await setup.Owner.PostJsonAsync($"/api/orgs/{setup.Tenants.OrgA}/webhooks/{endpointId}/deliveries/{delivery.Id}/redeliver", new { });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        await setup.Tenants.Factory.DispatchOutboxAsync();
        var second = setup.Receiver.Requests.Last();
        Assert.Equal((first.EventId, first.Body), (second.EventId, second.Body));

        var test = await setup.Owner.PostJsonAsync($"/api/orgs/{setup.Tenants.OrgA}/webhooks/{endpointId}/test", new { });
        Assert.Equal(HttpStatusCode.Accepted, test.StatusCode);
        await setup.Tenants.Factory.DispatchOutboxAsync();
        var ping = setup.Receiver.Requests.Last();
        using var payload = JsonDocument.Parse(ping.Body);
        Assert.Equal("webhook.ping", payload.RootElement.GetProperty("type").GetString());
        VerifiedTimestamp(ping.Signature, ping.Body, [secret]);

        await using var db = PostgresFixture.CreateDbContext(setup.Tenants.ConnectionString);
        var actions = await db.AuditEntries.AcrossTenants().Where(a => a.EntityId == endpointId).Select(a => a.Action).ToListAsync(Ct);
        Assert.Contains("webhook.redelivered", actions);
        Assert.Contains("webhook.test_sent", actions);
    }

    [Fact]
    public async Task The_log_keeps_no_response_body_and_secrets_are_stored_encrypted()
    {
        await using var setup = await SetupAsync();
        var (endpointId, secret) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        setup.Receiver.Status = 500;
        await CreateJobAsync(setup, "Answered with a body");
        await setup.Tenants.Factory.DispatchOutboxAsync();

        var list = await setup.Owner.Http.GetStringAsync($"/api/orgs/{setup.Tenants.OrgA}/webhooks/{endpointId}/deliveries", Ct);
        var endpoints = await setup.Owner.Http.GetStringAsync($"/api/orgs/{setup.Tenants.OrgA}/webhooks", Ct);
        await using var connection = new Npgsql.NpgsqlConnection(PostgresFixture.AdminConnectionStringFor(setup.Tenants.ConnectionString));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("SELECT coalesce((SELECT string_agg(d::text, '|') FROM webhook_deliveries d), '') || coalesce((SELECT string_agg(e::text, '|') FROM webhook_endpoints e), '') || coalesce((SELECT string_agg(o::text, '|') FROM outbox_messages o), '')", connection);
        var stored = (string)(await command.ExecuteScalarAsync(Ct))!;

        Assert.DoesNotContain("receiver-private-response-body", list, StringComparison.Ordinal);
        Assert.DoesNotContain("receiver-private-response-body", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, string.Join('\n', setup.Tenants.Factory.Logs.Entries.Select(e => e.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoints_are_https_only_at_most_five_and_from_the_catalog()
    {
        await using var setup = await SetupAsync();
        var path = $"/api/orgs/{setup.Tenants.OrgA}/webhooks";

        await ProblemAssert.HasFieldErrorsAsync(await setup.Owner.PostJsonAsync(path, new { url = "http://hooks.example/plain", events = new[] { "job.created" } }), "url");
        await ProblemAssert.HasFieldErrorsAsync(await setup.Owner.PostJsonAsync(path, new { url = "https://user:pass@hooks.example/", events = new[] { "job.created" } }), "url");
        await ProblemAssert.HasFieldErrorsAsync(await setup.Owner.PostJsonAsync(path, new { url = "https://hooks.example/", events = new[] { "job.deleted" } }), "events");
        await ProblemAssert.HasFieldErrorsAsync(await setup.Owner.PostJsonAsync(path, new { url = "https://hooks.example/", events = Array.Empty<string>() }), "events");
        for (var i = 0; i < WebhookEndpoint.MaxPerOrganization; i++)
        {
            (await setup.Owner.PostJsonAsync(path, new { url = $"https://hooks.example/{i}", events = new[] { "job.created" } })).EnsureSuccessStatusCode();
        }

        await ProblemAssert.HasCodeAsync(
            await setup.Owner.PostJsonAsync(path, new { url = "https://hooks.example/six", events = new[] { "job.created" } }),
            HttpStatusCode.Conflict,
            "webhook-limit-reached");
    }

    [Fact]
    public async Task Below_the_Business_plan_every_key_and_webhook_endpoint_is_refused()
    {
        await using var setup = await SetupAsync();
        var org = setup.Tenants.OrgA;
        var (endpointId, _) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"/api/orgs/{org}/api-keys", null),
            (HttpMethod.Post, $"/api/orgs/{org}/api-keys", new { name = "Too early", access = "readOnly" }),
            (HttpMethod.Delete, $"/api/orgs/{org}/api-keys/{setup.Tenants.ApiKeyA}", null),
            (HttpMethod.Get, $"/api/orgs/{org}/webhooks", null),
            (HttpMethod.Post, $"/api/orgs/{org}/webhooks", new { url = "https://hooks.example/", events = new[] { "job.created" } }),
            (HttpMethod.Put, $"/api/orgs/{org}/webhooks/{endpointId}", new { url = "https://hooks.example/", events = new[] { "job.created" } }),
            (HttpMethod.Delete, $"/api/orgs/{org}/webhooks/{endpointId}", null),
            (HttpMethod.Post, $"/api/orgs/{org}/webhooks/{endpointId}/rotate-secret", new { }),
            (HttpMethod.Get, $"/api/orgs/{org}/webhooks/{endpointId}/deliveries", null),
            (HttpMethod.Post, $"/api/orgs/{org}/webhooks/{endpointId}/test", new { }),
        };

        foreach (var plan in new[] { Plan.Free, Plan.Team })
        {
            await setup.Tenants.SetPlanAsync(org, plan);
            foreach (var (method, path, body) in calls)
            {
                var response = method == HttpMethod.Get
                    ? await setup.Owner.Http.GetAsync(path, Ct)
                    : await setup.Owner.SendJsonAsync(method, path, body);
                await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Forbidden, "plan-feature-unavailable");
            }
        }

        // Kept endpoints stay quiet below Business.
        await CreateJobAsync(setup, "Not sent on Team");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        Assert.Empty(setup.Receiver.Requests);
    }

    [Fact]
    public async Task An_endpoint_can_be_edited_after_deliveries_have_recorded_their_outcome()
    {
        // Found in the browser pass: every attempt updates the endpoint's failure window (and its row version), so an
        // edit carrying the version the page loaded conflicted with the endpoint's own deliveries.
        await using var setup = await SetupAsync();
        var (endpointId, _) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        var shown = (await EndpointsAsync(setup)).Single(e => e.Id == endpointId);
        setup.Receiver.Status = 500;
        await CreateJobAsync(setup, "Fails, and the endpoint records it");
        await setup.Tenants.Factory.DispatchOutboxAsync();

        var edited = await setup.Owner.SendJsonAsync(
            HttpMethod.Put,
            $"/api/orgs/{setup.Tenants.OrgA}/webhooks/{endpointId}",
            new { url = new Uri(setup.Receiver.Url, "hooks/tenant-hub").ToString(), events = new[] { "job.created", "job.assigned" }, enabled = false, version = shown.Version });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.False((await EndpointsAsync(setup)).Single(e => e.Id == endpointId).Enabled);
    }

    [Fact]
    public async Task A_downgrade_counts_usable_keys_and_enabled_endpoints_as_integrations_in_use()
    {
        await using var setup = await SetupAsync(); // no endpoints; the fixture's usable key remains
        var plan = $"/api/orgs/{setup.Tenants.OrgA}/plan";

        var withKey = await setup.Owner.SendJsonAsync(HttpMethod.Put, plan, new { plan = "team" });
        await ProblemAssert.HasCodeAsync(withKey, HttpStatusCode.Conflict, "plan-feature-loss");
        var loss = await withKey.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["integrations"], loss.GetProperty("features").EnumerateArray().Select(f => f.GetString()));

        (await setup.Owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{setup.Tenants.OrgA}/api-keys/{setup.Tenants.ApiKeyA}", null)).EnsureSuccessStatusCode();
        var (endpointId, _) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        await ProblemAssert.HasCodeAsync(await setup.Owner.SendJsonAsync(HttpMethod.Put, plan, new { plan = "team" }), HttpStatusCode.Conflict, "plan-feature-loss");

        (await setup.Owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{setup.Tenants.OrgA}/webhooks/{endpointId}", new { url = new Uri(setup.Receiver.Url, "hooks/tenant-hub").ToString(), events = new[] { "job.created" }, enabled = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await setup.Owner.SendJsonAsync(HttpMethod.Put, plan, new { plan = "team" })).StatusCode);
    }

    [Fact]
    public async Task The_delivery_log_is_kept_thirty_days()
    {
        await using var setup = await SetupAsync();
        var (endpointId, _) = await AddEndpointAsync(setup, [WebhookEvents.JobCreated]);
        await CreateJobAsync(setup, "Logged");
        await setup.Tenants.Factory.DispatchOutboxAsync();
        Assert.Single(await DeliveriesAsync(setup, endpointId));

        // A month on, the session has expired: count the rows directly.
        setup.Clock.Advance(WebhookDelivery.Retention - TimeSpan.FromHours(1));
        await RunDailyAsync(setup);
        Assert.Equal(1, await DeliveryCountAsync(setup, endpointId));
        setup.Clock.Advance(TimeSpan.FromHours(2));
        await RunDailyAsync(setup);
        Assert.Equal(0, await DeliveryCountAsync(setup, endpointId));
    }

    private static async Task<int> DeliveryCountAsync(Setup setup, Guid endpointId)
    {
        await using var db = PostgresFixture.CreateDbContext(setup.Tenants.ConnectionString);
        return await db.WebhookDeliveries.AcrossTenants().CountAsync(d => d.EndpointId == endpointId, Ct);
    }

    /// <summary>The documented check (Integrations page): any v1 equal to hex HMAC-SHA256(secret, "t.body").</summary>
    internal static long VerifiedTimestamp(string header, string body, IEnumerable<string> secrets)
    {
        var parts = header.Split(',');
        var t = parts.Single(p => p.StartsWith("t=", StringComparison.Ordinal))[2..];
        var given = parts.Where(p => p.StartsWith("v1=", StringComparison.Ordinal)).Select(p => p[3..]).ToList();
        foreach (var secret in secrets)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var expected = string.Concat(hmac.ComputeHash(Encoding.UTF8.GetBytes(t + "." + body)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            if (given.Any(g => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(g), Encoding.ASCII.GetBytes(expected))))
            {
                return long.Parse(t, CultureInfo.InvariantCulture);
            }
        }

        throw new SignatureMismatch();
    }

    private async Task<Setup> SetupAsync()
    {
        var receiver = await WebhookReceiver.StartAsync();
        // Whole seconds: PostgreSQL keeps microseconds, and the tests compare times exactly.
        var clock = new MovingClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var tenants = await ApiTenants.CreateAsync(postgres, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(new WebhookAddresses(receiver.Url));
        });

        // The fixture's standing endpoints point at a name that does not exist: remove them and whatever they queued.
        await using (var connection = new Npgsql.NpgsqlConnection(PostgresFixture.AdminConnectionStringFor(tenants.ConnectionString)))
        {
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand("DELETE FROM webhook_endpoints; DELETE FROM outbox_messages;", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        return new Setup(tenants, receiver, clock, await tenants.ClientForAsync(Role.Owner));
    }

    private static async Task<(Guid Id, string Secret)> AddEndpointAsync(Setup setup, string[] events)
    {
        var response = await setup.Owner.PostJsonAsync(
            $"/api/orgs/{setup.Tenants.OrgA}/webhooks",
            new { url = new Uri(setup.Receiver.Url, "hooks/tenant-hub").ToString(), events });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<WithSecret>(BrowserClient.Json, Ct))!;
        Assert.StartsWith("whsec_", created.Secret, StringComparison.Ordinal);
        return (created.Endpoint.Id, created.Secret);
    }

    private static async Task<Guid> CreateJobAsync(Setup setup, string title)
    {
        var response = await setup.Owner.PostJsonAsync($"/api/orgs/{setup.Tenants.OrgA}/jobs", new { title, clientId = setup.Tenants.ClientA, priority = "normal" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IdOnly>(BrowserClient.Json, Ct))!.Id;
    }

    private static async Task<List<DeliveryView>> DeliveriesAsync(Setup setup, Guid endpointId)
    {
        var page = await setup.Owner.Http.GetFromJsonAsync<DeliveryPage>($"/api/orgs/{setup.Tenants.OrgA}/webhooks/{endpointId}/deliveries", BrowserClient.Json, Ct);
        return [.. page!.Items];
    }

    private static async Task<List<EndpointView>> EndpointsAsync(Setup setup) =>
        (await setup.Owner.Http.GetFromJsonAsync<List<EndpointView>>($"/api/orgs/{setup.Tenants.OrgA}/webhooks", BrowserClient.Json, Ct))!;

    private static async Task<OutboxMessage> DeliveryMessageAsync(Setup setup)
    {
        await using var db = PostgresFixture.CreateDbContext(setup.Tenants.ConnectionString);
        return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == WebhookDeliveryMessage.Deliver.Name, Ct);
    }

    private static async Task RunDailyAsync(Setup setup)
    {
        await using var scope = setup.Tenants.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OverdueDigest>().RunAsync(setup.Tenants.OrgA, Ct);
    }

    private sealed record Setup(ApiTenants Tenants, WebhookReceiver Receiver, MovingClock Clock, BrowserClient Owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Tenants.DisposeAsync();
            await Receiver.DisposeAsync();
        }
    }

    internal sealed class SignatureMismatch : Exception;

    private sealed record IdOnly(Guid Id);

    private sealed record EndpointView(Guid Id, bool Enabled, uint Version = 0);

    private sealed record WithSecret(EndpointView Endpoint, string Secret);

    private sealed record DeliveryView(Guid Id, int Attempt, int? StatusCode, string? Error, DateTimeOffset? NextAttemptAt, bool Succeeded);

    private sealed record DeliveryPage(List<DeliveryView> Items);
}
