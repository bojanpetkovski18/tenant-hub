using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Accounts;
using TenantHub.Infrastructure.Background;
using TenantHub.Infrastructure.Data;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Background;

/// <summary>
/// SPEC-V2 4.9 and criterion 16 (D039): a message is written with its change or not at all, secrets are encrypted at
/// rest and gone once sent, a failing delivery is retried on schedule and then dead, and two dispatchers on one
/// database deliver every message exactly once.
/// </summary>
public sealed class OutboxTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rolled_back_command_leaves_no_message_and_a_committed_one_leaves_one()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var recorder = new Recorder();
        await using var factory = Factory(connectionString, recorder);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            scope.ServiceProvider.GetRequiredService<IOutbox>().Add(Recorder.Type, new TestPayload("rolled back"));
            await db.SaveChangesAsync(Ct);
            // Something later in the same command fails: dispose without commit (as v1 criterion 8 tests audit entries).
        }

        await AddAsync(factory, Recorder.Type, new TestPayload("committed"));

        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(["committed"], (await check.OutboxMessages.Select(m => m.Payload).ToListAsync(Ct)).Select(p => OutboxEnvelopeRead(p).Text));
        Assert.Equal(1, await factory.DispatchOutboxAsync());
        Assert.Equal(["committed"], recorder.Texts);
    }

    [Fact]
    public async Task A_secret_payload_is_encrypted_at_rest_and_deleted_once_sent()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var recorder = new Recorder();
        await using var factory = Factory(connectionString, recorder);
        await AddAsync(factory, Recorder.SecretType, new TestPayload("the-secret-link"));

        await using var check = PostgresFixture.CreateDbContext(connectionString);
        var row = await check.OutboxMessages.AsNoTracking().SingleAsync(Ct);
        Assert.DoesNotContain("the-secret-link", row.Payload, StringComparison.Ordinal);
        Assert.Null(row.OrganizationId);

        Assert.Equal(1, await factory.DispatchOutboxAsync());
        Assert.Equal(["the-secret-link"], recorder.Texts);
        Assert.Equal(0, await check.OutboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task A_failing_delivery_is_retried_on_schedule_and_then_marked_dead()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new MovingClock(Start);
        var recorder = new Recorder { FailuresLeft = int.MaxValue };
        await using var factory = Factory(connectionString, recorder, clock);
        await AddAsync(factory, Recorder.Type, new TestPayload("never"));
        await using var check = PostgresFixture.CreateDbContext(connectionString);

        for (var attempt = 1; attempt < OutboxDispatcher.MaxAttempts; attempt++)
        {
            Assert.Equal(1, await factory.DispatchOutboxAsync());
            var row = await check.OutboxMessages.AsNoTracking().SingleAsync(Ct);
            var delay = OutboxDispatcher.RetryDelays[attempt - 1];
            Assert.Equal((attempt, "InvalidOperationException", clock.Now + delay, (DateTimeOffset?)null), (row.Attempts, row.LastError, row.AvailableAt, row.DeadAt));

            // Not before its time.
            clock.Advance(delay - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await factory.DispatchOutboxAsync());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, await factory.DispatchOutboxAsync());
        var dead = await check.OutboxMessages.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((OutboxDispatcher.MaxAttempts, clock.Now), (dead.Attempts, dead.DeadAt));
        Assert.Null(dead.ProcessedAt);
        Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains($"{dead.Id}", StringComparison.Ordinal) && e.Message.Contains("is dead", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await factory.DispatchOutboxAsync());
        Assert.Equal(OutboxDispatcher.MaxAttempts, recorder.Calls);

        // Only the class of the error is kept: the exception's message named an address.
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Message.Contains("someone@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delivery_that_fails_once_succeeds_on_the_retry()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new MovingClock(Start);
        var recorder = new Recorder { FailuresLeft = 1 };
        await using var factory = Factory(connectionString, recorder, clock);
        await AddAsync(factory, Recorder.Type, new TestPayload("second time lucky"));

        await factory.DispatchOutboxAsync();
        clock.Advance(OutboxDispatcher.RetryDelays[0]);
        await factory.DispatchOutboxAsync();

        await using var check = PostgresFixture.CreateDbContext(connectionString);
        var row = await check.OutboxMessages.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((2, clock.Now, (string?)null), (row.Attempts, row.ProcessedAt, row.LastError));
        Assert.Equal(["second time lucky"], recorder.Texts);
    }

    [Fact]
    public async Task Unknown_types_and_permanent_failures_are_dead_at_once()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var recorder = new Recorder { Permanent = true, FailuresLeft = 1 };
        await using var factory = Factory(connectionString, recorder);
        await AddAsync(factory, Recorder.Type, new TestPayload("refused"));
        await AddAsync(factory, new OutboxMessageType("test.nobody-handles-this", CarriesSecret: false), new TestPayload("lost"));

        await factory.DispatchOutboxAsync();

        await using var check = PostgresFixture.CreateDbContext(connectionString);
        var rows = await check.OutboxMessages.AsNoTracking().OrderBy(m => m.Type).ToListAsync(Ct);
        Assert.Equal(
            [("test.nobody-handles-this", "unknown-type", 1), ("test.recorded", "test-refused", 1)],
            rows.Select(r => (r.Type, r.LastError!, r.Attempts)));
        Assert.All(rows, r => Assert.NotNull(r.DeadAt));
    }

    [Fact]
    public async Task Two_dispatchers_on_one_database_deliver_every_message_exactly_once()
    {
        // Repeated rounds on fresh data: a race that loses one round in a hundred would still be likely to show.
        const int rounds = 5;
        const int messages = 150;
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        var settings = new Dictionary<string, string?> { ["Outbox:BatchSize"] = "4" };
        await using var first = new ApiFactory(connectionString, settings, s => s.AddSingleton<IEmailSender>(emails));
        await using var second = new ApiFactory(connectionString, settings, s => s.AddSingleton<IEmailSender>(emails));
        var claimedBy = new List<(int First, int Second)>();

        for (var round = 0; round < rounds; round++)
        {
            await using (var scope = first.Services.CreateAsyncScope())
            {
                var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
                for (var i = 0; i < messages; i++)
                {
                    // The real account-email path: encrypted payloads, the SMTP handler, a recording sender.
                    outbox.Add(AccountEmails.Verification, new EmailMessage($"r{round}-m{i}@test.example", "Subject", "Text", "<p>Html</p>"));
                }

                await scope.ServiceProvider.GetRequiredService<IAppDbContext>().SaveChangesAsync(Ct);
            }

            var results = await Task.WhenAll(Task.Run(first.DispatchOutboxAsync, Ct), Task.Run(second.DispatchOutboxAsync, Ct));
            claimedBy.Add((results[0], results[1]));
        }

        var deliveries = emails.Sent.GroupBy(m => m.MessageId).Select(g => g.Count()).ToList();
        Assert.Equal(rounds * messages, emails.Sent.Count);
        Assert.Equal(rounds * messages, deliveries.Count);
        Assert.All(deliveries, count => Assert.Equal(1, count));
        Assert.Equal(rounds * messages, claimedBy.Sum(c => c.First + c.Second));
        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await check.OutboxMessages.CountAsync(Ct)); // secret messages are deleted once sent
        TestContext.Current.TestOutputHelper?.WriteLine($"Claims per round (first, second): {string.Join(", ", claimedBy)}");
    }

    private static TestPayload OutboxEnvelopeRead(string json) =>
        new OutboxEnvelope(Guid.Empty, Recorder.Type, null, json, 1).Read<TestPayload>();

    private static ApiFactory Factory(string connectionString, Recorder recorder, TimeProvider? clock = null) =>
        new(connectionString, services: services =>
        {
            services.AddSingleton(recorder);
            services.AddOutboxHandler<RecordingHandler>(Recorder.Type, Recorder.SecretType);
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        });

    private static async Task AddAsync(ApiFactory factory, OutboxMessageType type, TestPayload payload)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOutbox>().Add(type, payload);
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(Ct);
    }

    public sealed record TestPayload(string Text);

    public sealed class Recorder
    {
        public static readonly OutboxMessageType Type = new("test.recorded", CarriesSecret: false, PerOrganization: false);
        public static readonly OutboxMessageType SecretType = new("test.secret", CarriesSecret: true, PerOrganization: false);

        private readonly ConcurrentQueue<string> _texts = new();
        private int _calls;
        private int _failuresLeft;

        public int FailuresLeft { get => _failuresLeft; set => _failuresLeft = value; }

        public bool Permanent { get; init; }

        public int Calls => _calls;

        public IReadOnlyList<string> Texts => [.. _texts];

        public void Record(OutboxEnvelope message)
        {
            Interlocked.Increment(ref _calls);
            if (Interlocked.Decrement(ref _failuresLeft) >= 0)
            {
                throw Permanent ? new OutboxDeliveryException("test-refused", permanent: true) : new InvalidOperationException("Delivery to someone@example.com failed.");
            }

            _texts.Enqueue(message.Read<TestPayload>().Text);
        }
    }

    private sealed class RecordingHandler(Recorder recorder) : IOutboxHandler
    {
        public Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
        {
            recorder.Record(message);
            return Task.CompletedTask;
        }
    }
}
