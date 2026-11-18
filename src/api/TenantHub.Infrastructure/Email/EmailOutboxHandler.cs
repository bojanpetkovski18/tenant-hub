using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Email;

/// <summary>
/// Sends an <see cref="EmailMessage"/> from the outbox (D039). The message id becomes the email's <c>Message-ID</c>,
/// so if a crash between sending and recording makes the dispatcher send it again, mail systems see the same message.
/// </summary>
internal sealed class EmailOutboxHandler(IEmailSender sender, EmailOptions options) : IOutboxHandler
{
    public async Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken)
    {
        if (!sender.IsEnabled)
        {
            // Email was switched off after the message was written; retrying will not help.
            throw new OutboxDeliveryException("email-disabled", permanent: true);
        }

        var email = message.Read<EmailMessage>() with { MessageId = MessageIdFor(message.Id, options.From) };
        if (!await sender.SendAsync(email, cancellationToken))
        {
            throw new OutboxDeliveryException("email-not-accepted");
        }
    }

    internal static string MessageIdFor(Guid id, string from) =>
        $"<{id:N}@{from[(from.IndexOf('@', StringComparison.Ordinal) + 1)..]}>";
}
