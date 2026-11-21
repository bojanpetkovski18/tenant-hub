using System.Text.Json;

namespace TenantHub.Application.Abstractions;

/// <summary>
/// A kind of outbox message (D039): its stable name, stored in every row, and whether its payload carries a secret
/// (a link that signs someone in or resets a password). Secret payloads are encrypted at rest under a purpose of
/// their own and deleted once delivered. <c>PerOrganization</c> types take the current tenant's organization and go
/// with it; the others (account emails) belong to a person and have none. <c>RetryDelays</c> replaces the dispatcher's
/// schedule for a type whose receiver may be away longer (webhooks: about a day).
/// </summary>
public sealed record OutboxMessageType(string Name, bool CarriesSecret, bool PerOrganization = true, IReadOnlyList<TimeSpan>? RetryDelays = null);

/// <summary>
/// Side effects that must leave the request safely (SPEC-V2 4.9). A message added here is written by the next
/// <c>SaveChanges</c> of the request's unit of work, with the change that caused it and its audit entries: a
/// rolled-back command sends nothing, and a committed one cannot lose its message. The organization comes from the
/// tenant context, never from the caller.
/// </summary>
public interface IOutbox
{
    void Add<TPayload>(OutboxMessageType type, TPayload payload)
        where TPayload : notnull;
}

/// <summary>One message as its handler sees it: the payload already decrypted.</summary>
public sealed record OutboxEnvelope(Guid Id, OutboxMessageType Type, Guid? OrganizationId, string Payload, int Attempt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize<TPayload>(TPayload payload) => JsonSerializer.Serialize(payload, Json);

    public TPayload Read<TPayload>() =>
        JsonSerializer.Deserialize<TPayload>(Payload, Json) ?? throw new OutboxDeliveryException("payload-empty", permanent: true);
}

/// <summary>
/// Delivers one type of message. The dispatcher may hand the same message over again after a crash between delivery
/// and marking it done, so a handler is idempotent by <see cref="OutboxEnvelope.Id"/>.
/// </summary>
public interface IOutboxHandler
{
    Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken);
}

/// <summary>
/// A delivery that failed for a reason worth recording. <see cref="Code"/> is what the row keeps: a class of error,
/// never an address or a secret. A permanent failure is not retried.
/// </summary>
public sealed class OutboxDeliveryException(string code, bool permanent = false)
    : Exception($"Delivery failed: {code}.")
{
    public string Code { get; } = code;

    public bool Permanent { get; } = permanent;
}
