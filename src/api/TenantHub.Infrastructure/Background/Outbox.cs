using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenantHub.Application.Abstractions;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Background;

/// <summary>
/// Adds messages to the request's <see cref="AppDbContext"/>, so the save that writes the change and its audit
/// entries writes the message too, in the same transaction (D039).
/// </summary>
internal sealed class Outbox(AppDbContext db, IDataProtectionProvider protection, ITenantContext tenant, TimeProvider time) : IOutbox
{
    public void Add<TPayload>(OutboxMessageType type, TPayload payload)
        where TPayload : notnull
    {
        var json = OutboxEnvelope.Serialize(payload);
        var now = time.GetUtcNow();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = type.PerOrganization ? tenant.OrganizationId : null,
            Type = type.Name,
            Payload = type.CarriesSecret ? JsonSerializer.Serialize(Protector(protection, type).Protect(json)) : json,
            CreatedAt = now,
            AvailableAt = now,
        });
    }

    /// <summary>One purpose per message type: a payload of one type cannot be decrypted as another.</summary>
    internal static IDataProtector Protector(IDataProtectionProvider protection, OutboxMessageType type) =>
        protection.CreateProtector("TenantHub.Outbox", type.Name);
}

/// <summary>
/// Wakes this instance's dispatcher when a message has been committed, so an email leaves within moments instead
/// of at the next poll. Other instances find the message at their next poll; nothing depends on the signal.
/// </summary>
public sealed class OutboxSignal : IDisposable
{
    private readonly SemaphoreSlim _pending = new(0, 1);

    public void Notify()
    {
        // At most one wake-up is pending; a second notification before the dispatcher runs adds nothing.
        try
        {
            _pending.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>True when notified, false when <paramref name="timeout"/> passed first.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _pending.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _pending.Dispose();
}

/// <summary>Tells the context when its transaction commits: only then can another connection see the messages.</summary>
internal sealed class OutboxCommitInterceptor : DbTransactionInterceptor
{
    public static readonly OutboxCommitInterceptor Instance = new();

    private OutboxCommitInterceptor()
    {
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        (eventData.Context as AppDbContext)?.OnTransactionCommitted();

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as AppDbContext)?.OnTransactionCommitted();
        return Task.CompletedTask;
    }
}
