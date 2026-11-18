using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Abstractions;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Background;

public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>Messages claimed at once. Small batches spread work across instances; the claim is one statement.</summary>
    public int BatchSize { get; init; } = 20;
}

/// <summary>Which handler delivers which message type.</summary>
public sealed record OutboxRoute(OutboxMessageType Type, Type HandlerType);

public static class OutboxRegistration
{
    public static IServiceCollection AddOutboxHandler<THandler>(this IServiceCollection services, params OutboxMessageType[] types)
        where THandler : class, IOutboxHandler
    {
        services.AddScoped<THandler>();
        foreach (var type in types)
        {
            services.AddSingleton(new OutboxRoute(type, typeof(THandler)));
        }

        return services;
    }
}

/// <summary>
/// Delivers outbox messages (SPEC-V2 4.9, D039). Several instances may run it against one database:
/// <list type="number">
/// <item>A short transaction claims a batch with <c>FOR UPDATE SKIP LOCKED</c>, oldest due first, and pushes each
/// message's <c>available_at</c> one lease ahead while counting the attempt. Another dispatcher skips the locked
/// rows and, after the commit, finds them not yet due.</item>
/// <item>Each message is delivered outside any transaction, by its handler, in a scope of its own.</item>
/// <item>The outcome is written only if the attempt count is still the one this claim set (a fencing token): a
/// dispatcher that outlived its lease cannot overwrite the claim of the one that took the message over.</item>
/// </list>
/// A dispatcher that dies mid-batch leaves its messages to come back when the lease ends, which is why handlers are
/// idempotent by message id.
/// </summary>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopes,
    IEnumerable<OutboxRoute> routes,
    IDataProtectionProvider protection,
    OutboxOptions options,
    TimeProvider time,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>How long a claim holds a message; longer than <see cref="HandlerTimeout"/>, so a live claim never expires.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>The wait before attempt n + 1 after attempt n failed. One failure more than this list makes a message dead.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4)];

    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, OutboxRoute> _routes = routes.ToDictionary(r => r.Type.Name, StringComparer.Ordinal);

    public static int MaxAttempts => RetryDelays.Count + 1;

    /// <summary>Claims one batch and delivers it. Returns how many messages it claimed (0: nothing was due).</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var batch = await ClaimAsync(db, cancellationToken);
        foreach (var message in batch)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break; // the rest come back when their lease ends
            }

            await DeliverAsync(db, message, cancellationToken);
        }

        return batch.Count;
    }

    private async Task<List<OutboxMessage>> ClaimAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL AND dead_at IS NULL AND available_at <= {now}
                ORDER BY available_at
                LIMIT {options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            message.AvailableAt = now + Lease;
            message.Attempts++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return batch;
    }

    private async Task DeliverAsync(AppDbContext db, OutboxMessage message, CancellationToken cancellationToken)
    {
        string? error = null;
        var permanent = false;
        var carriesSecret = false;
        try
        {
            if (!_routes.TryGetValue(message.Type, out var route))
            {
                throw new OutboxDeliveryException("unknown-type", permanent: true);
            }

            carriesSecret = route.Type.CarriesSecret;
            var payload = carriesSecret
                ? Outbox.Protector(protection, route.Type).Unprotect(JsonSerializer.Deserialize<string>(message.Payload)!)
                : message.Payload;

            await using var scope = scopes.CreateAsyncScope();
            var handler = (IOutboxHandler)scope.ServiceProvider.GetRequiredService(route.HandlerType);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandlerTimeout);
            await handler.HandleAsync(new OutboxEnvelope(message.Id, route.Type, message.OrganizationId, payload, message.Attempts), timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // shutting down: the lease brings the message back
        }
        catch (OutboxDeliveryException exception)
        {
            (error, permanent) = (exception.Code, exception.Permanent);
        }
        catch (CryptographicException)
        {
            // The key ring lost the key, or the row was altered: no later attempt can read it either.
            (error, permanent) = ("payload-unreadable", true);
        }
        catch (Exception exception)
        {
            // The class only: exception messages can carry addresses (an SMTP reply quotes the recipient).
            error = exception.GetType().Name;
            LogHandlerFailed(logger, message.Id, message.Type, exception.GetType().Name);
        }

        await CompleteAsync(db, message, carriesSecret, error, permanent);
    }

    // Written even when shutdown has begun (CancellationToken.None): a message that was delivered must not be sent again.
    private async Task CompleteAsync(AppDbContext db, OutboxMessage message, bool carriesSecret, string? error, bool permanent)
    {
        var now = time.GetUtcNow();
        var claim = db.OutboxMessages.Where(m =>
            m.Id == message.Id && m.Attempts == message.Attempts && m.ProcessedAt == null && m.DeadAt == null);

        int written;
        if (error is null)
        {
            // A secret leaves the database as soon as it has been delivered.
            written = carriesSecret
                ? await claim.ExecuteDeleteAsync(CancellationToken.None)
                : await claim.ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, now).SetProperty(m => m.LastError, (string?)null), CancellationToken.None);
        }
        else if (permanent || message.Attempts >= MaxAttempts)
        {
            // A dead secret is no use to anyone (reset links expire in 30 minutes): only the record of the failure stays.
            written = carriesSecret
                ? await claim.ExecuteUpdateAsync(
                    s => s.SetProperty(m => m.DeadAt, now).SetProperty(m => m.LastError, error).SetProperty(m => m.Payload, "null"),
                    CancellationToken.None)
                : await claim.ExecuteUpdateAsync(
                    s => s.SetProperty(m => m.DeadAt, now).SetProperty(m => m.LastError, error),
                    CancellationToken.None);
            if (written > 0)
            {
                LogDead(logger, message.Id, message.Type, message.Attempts, error);
            }
        }
        else
        {
            var retryAt = now + RetryDelays[message.Attempts - 1];
            written = await claim.ExecuteUpdateAsync(
                s => s.SetProperty(m => m.AvailableAt, retryAt).SetProperty(m => m.LastError, error),
                CancellationToken.None);
            LogRetry(logger, message.Id, message.Type, message.Attempts, error, retryAt);
        }

        if (written == 0)
        {
            LogClaimLost(logger, message.Id, message.Type);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) threw {ErrorClass}.")]
    private static partial void LogHandlerFailed(ILogger logger, Guid messageId, string type, string errorClass);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {MessageId} ({Type}) failed attempt {Attempt} ({Error}); retrying at {RetryAt:o}.")]
    private static partial void LogRetry(ILogger logger, Guid messageId, string type, int attempt, string error, DateTimeOffset retryAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) is dead after {Attempts} attempts ({Error}).")]
    private static partial void LogDead(ILogger logger, Guid messageId, string type, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) was claimed again before this attempt finished; its outcome is not recorded.")]
    private static partial void LogClaimLost(ILogger logger, Guid messageId, string type);
}
