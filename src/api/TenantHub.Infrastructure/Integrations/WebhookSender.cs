using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Integrations;

namespace TenantHub.Infrastructure.Integrations;

public sealed class WebhookOptions
{
    public const string Section = "Webhooks";

    /// <summary>
    /// Development and the E2E journey only (SPEC-V2 5.8, D052): one local receiver, such as
    /// <c>http://localhost:5099/</c>, that endpoints may name and the guard lets through. Ignored everywhere else.
    /// </summary>
    public Uri? AllowedReceiver { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Resolves a host name; a test replaces it to make a name point anywhere.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Which addresses a webhook may reach (SPEC-V2 5.8, criterion 18): public unicast only. Loopback, private, shared,
/// link-local (the cloud metadata address among them), unique-local, multicast, documentation, benchmarking and reserved
/// ranges are refused, in IPv4 and IPv6, and an IPv4 address inside IPv6 (mapped, compatible, 6to4, Teredo, NAT64) is
/// judged as the IPv4 address it carries.
/// </summary>
public static class PublicAddresses
{
    private static readonly (IPAddress Network, int Bits)[] BlockedV4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8), // "this network"
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("100.64.0.0"), 10), // carrier-grade NAT
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("169.254.0.0"), 16), // link-local, 169.254.169.254 among them
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.0.0.0"), 24), // IETF protocol assignments
        (IPAddress.Parse("192.0.2.0"), 24), // documentation
        (IPAddress.Parse("192.88.99.0"), 24), // 6to4 relay anycast
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("198.18.0.0"), 15), // benchmarking
        (IPAddress.Parse("198.51.100.0"), 24), // documentation
        (IPAddress.Parse("203.0.113.0"), 24), // documentation
        (IPAddress.Parse("224.0.0.0"), 4), // multicast
        (IPAddress.Parse("240.0.0.0"), 4), // reserved, broadcast
    ];

    private static readonly (IPAddress Network, int Bits)[] BlockedV6 =
    [
        (IPAddress.Parse("::"), 128), // unspecified
        (IPAddress.Parse("::1"), 128), // loopback
        (IPAddress.Parse("64:ff9b:1::"), 48), // local-use NAT64
        (IPAddress.Parse("100::"), 64), // discard-only
        (IPAddress.Parse("2001::"), 23), // IETF protocol assignments (Teredo's 2001::/32 is judged by its IPv4 below)
        (IPAddress.Parse("2001:db8::"), 32), // documentation
        (IPAddress.Parse("fc00::"), 7), // unique-local
        (IPAddress.Parse("fe80::"), 10), // link-local
        (IPAddress.Parse("fec0::"), 10), // site-local (deprecated)
        (IPAddress.Parse("ff00::"), 8), // multicast
    ];

    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && EmbeddedV4(address) is { } inner)
        {
            return IsPublic(inner);
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !BlockedV4.Any(r => InRange(address, r.Network, r.Bits)),
            AddressFamily.InterNetworkV6 => (address.GetAddressBytes()[0] & 0xE0) == 0x20 // global unicast is 2000::/3
                && !BlockedV6.Any(r => InRange(address, r.Network, r.Bits)),
            _ => false,
        };
    }

    /// <summary>The IPv4 address an IPv6 address carries, for the forms that carry one.</summary>
    private static IPAddress? EmbeddedV4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        // IPv4-compatible (::a.b.c.d, deprecated) and NAT64 (64:ff9b::a.b.c.d): the last four bytes.
        if ((b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && !(b[12] == 0 && b[13] == 0 && b[14] == 0))
            || InRange(address, IPAddress.Parse("64:ff9b::"), 96))
        {
            return new IPAddress(b.AsSpan(12, 4));
        }

        // 6to4 (2002:a.b.c.d::/48): bytes 2 to 5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return new IPAddress(b.AsSpan(2, 4));
        }

        // Teredo (2001:0::/32): the client's address, inverted, in the last four bytes.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)
        {
            return new IPAddress([(byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15]]);
        }

        return null;
    }

    private static bool InRange(IPAddress address, IPAddress network, int bits)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        var whole = bits / 8;
        if (!a.AsSpan(0, whole).SequenceEqual(n.AsSpan(0, whole)))
        {
            return false;
        }

        var rest = bits % 8;
        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (a[whole] & mask) == (n[whole] & mask);
    }
}

/// <summary>
/// The only way the server calls a customer's URL (SPEC-V2 5.8). The handler's <c>ConnectCallback</c> resolves the host
/// itself, refuses the whole host if any of its addresses is not public, and connects to an address it checked: no
/// second lookup between check and connect, so a name that changes its answer (DNS rebinding) cannot slip through.
/// Redirects are not followed (a 3xx is a failure), the proxy settings of the machine are ignored, and a request takes
/// at most 10 seconds. A refusal is logged as a security event with the host and port, never the path (it may carry a token).
/// </summary>
public sealed partial class GuardedWebhookSender : IWebhookSender, IDisposable
{
    public const string RefusedAddress = "address-not-allowed";

    private readonly HttpClient _client;
    private readonly IHostResolver _resolver;
    private readonly WebhookAddresses _addresses;
    private readonly ILogger<GuardedWebhookSender> _logger;

    public GuardedWebhookSender(IHostResolver resolver, WebhookAddresses addresses, WebhookOptions options, ILogger<GuardedWebhookSender> logger)
    {
        _resolver = resolver;
        _addresses = addresses;
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = options.Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler) { Timeout = options.Timeout };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TenantHub-Webhooks", "1.0"));
    }

    public async Task<WebhookResult> SendAsync(Uri url, string body, string signature, Guid eventId, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        int Elapsed() => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(WebhookSignature.Header, signature);
        request.Headers.TryAddWithoutValidation("TenantHub-Event-Id", eventId.ToString());

        try
        {
            // Headers only: the response body is never read, kept or logged.
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            return status is >= 200 and < 300
                ? new WebhookResult(status, Elapsed(), null)
                : new WebhookResult(status, Elapsed(), status is >= 300 and < 400 ? "redirect-not-followed" : $"http-{status}");
        }
        catch (HttpRequestException exception) when (exception.InnerException is AddressRefusedException)
        {
            return new WebhookResult(null, Elapsed(), RefusedAddress);
        }
        catch (HttpRequestException exception) when (exception.InnerException is NameNotFoundException)
        {
            return new WebhookResult(null, Elapsed(), "dns-failed");
        }
        catch (HttpRequestException exception) when (exception.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return new WebhookResult(null, Elapsed(), "tls-failed");
        }
        catch (HttpRequestException)
        {
            return new WebhookResult(null, Elapsed(), "connection-failed");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebhookResult(null, Elapsed(), "timeout");
        }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolver.ResolveAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                // The handler wraps whatever this callback throws; a type of our own survives the wrapping.
                throw new NameNotFoundException();
            }
        }

        // Every address must be public: a host with one private answer is refused whole, whichever answer comes first.
        if (addresses.Length == 0 || (!_addresses.IsAllowedReceiver(host, port) && addresses.Any(a => !PublicAddresses.IsPublic(a))))
        {
            LogRefused(_logger, host, port);
            throw new AddressRefusedException();
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose() => _client.Dispose();

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Security: refused a webhook connection to {Host}:{Port}, which resolves to a non-public address.")]
    private static partial void LogRefused(ILogger logger, string host, int port);

    private sealed class AddressRefusedException() : Exception("The address is not allowed for webhooks.");

    private sealed class NameNotFoundException() : Exception("The host could not be resolved.");
}

/// <summary>Webhook secrets under a Data Protection purpose of their own (SPEC-V2 5.8); the key ring is the database's.</summary>
internal sealed class WebhookSecretProtector(IDataProtectionProvider protection) : IWebhookSecretProtector
{
    private readonly IDataProtector _protector = protection.CreateProtector("TenantHub.Webhooks.Secret");

    public string Protect(string secret) => _protector.Protect(secret);

    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
}
