using System.Collections.Concurrent;
using TenantHub.Application.Abstractions;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>An enabled email sender that keeps the messages instead of sending them; can be told to fail.</summary>
public sealed class CapturedEmails : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public bool IsEnabled => true;

    public bool Fail { get; set; }

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            return Task.FromResult(false);
        }

        _sent.Enqueue(message);
        return Task.FromResult(true);
    }
}
