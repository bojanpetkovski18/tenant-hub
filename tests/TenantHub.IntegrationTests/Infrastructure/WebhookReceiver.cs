using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Infrastructure.Integrations;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// A real HTTP server on a loopback port, standing in for a customer's webhook endpoint: it records what arrives and
/// answers with whatever the test sets. Tests allow exactly its address (the development-only setting, D044).
/// </summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;

    private WebhookReceiver(WebApplication app, Uri url) => (_app, Url) = (app, url);

    public Uri Url { get; }

    public ConcurrentQueue<Received> Requests { get; } = new();

    /// <summary>The status to answer with; 200 unless a test changes it.</summary>
    public int Status { get; set; } = StatusCodes.Status200OK;

    /// <summary>When set, answers 302 to this location instead.</summary>
    public string? RedirectTo { get; set; }

    /// <summary>How long to wait before answering.</summary>
    public TimeSpan Delay { get; set; }

    public static async Task<WebhookReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        WebhookReceiver? receiver = null;
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            receiver!.Requests.Enqueue(new Received(
                context.Request.Path,
                context.Request.Headers["TenantHub-Signature"].ToString(),
                context.Request.Headers["TenantHub-Event-Id"].ToString(),
                context.Request.ContentType,
                body));
            if (receiver.Delay > TimeSpan.Zero)
            {
                await Task.Delay(receiver.Delay, context.RequestAborted);
            }

            if (receiver.RedirectTo is { } location)
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = location;
                return;
            }

            context.Response.StatusCode = receiver.Status;
            // A body the delivery log must never keep.
            await context.Response.WriteAsync("receiver-private-response-body", context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        receiver = new WebhookReceiver(app, new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal) + "/"));
        return receiver;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public sealed record Received(string Path, string Signature, string EventId, string? ContentType, string Body);
}

/// <summary>
/// The resolver tests use: names map to the addresses a test gives, anything else does not exist. No test ever asks the
/// machine's DNS, so a webhook test cannot depend on the network or reach it.
/// </summary>
public sealed class FakeResolver(IReadOnlyDictionary<string, IPAddress[]>? names = null) : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        names?.TryGetValue(host, out var addresses) == true
            ? Task.FromResult(addresses)
            : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
}
