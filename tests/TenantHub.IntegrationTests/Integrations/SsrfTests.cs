using System.Net;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Integrations;
using TenantHub.Infrastructure.Integrations;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Integrations;

/// <summary>
/// SPEC-V2 5.8 and criterion 18: nothing a customer types makes the server call its own network. The real sender, with
/// its connection-time check, against loopback, private, link-local (the cloud metadata address), unique-local and
/// IPv4-in-IPv6 addresses, a name that resolves to one, a name with one private answer among public ones, and a
/// redirect towards one: all refused, each refusal logged as a security event. No test here reaches the network.
/// </summary>
public sealed class SsrfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Refused() =>
    [
        "https://localhost/hooks",
        "https://127.0.0.1/hooks",
        "https://127.8.9.10/hooks",
        "https://[::1]/hooks",
        "https://0.0.0.0/hooks",
        "https://10.0.0.5/hooks",
        "https://172.16.4.2/hooks",
        "https://172.31.255.254/hooks",
        "https://192.168.1.10/hooks",
        "https://169.254.169.254/latest/meta-data/",
        "https://100.64.0.1/hooks",
        "https://[fc00::1]/hooks",
        "https://[fd12:3456:789a::1]/hooks",
        "https://[fe80::1]/hooks",
        "https://[::ffff:127.0.0.1]/hooks",
        "https://[::ffff:169.254.169.254]/hooks",
        "https://[::ffff:10.1.2.3]/hooks",
        "https://[64:ff9b::a00:1]/hooks", // NAT64 of 10.0.0.1
        "https://[2002:c0a8:101::1]/hooks", // 6to4 of 192.168.1.1
        "https://224.0.0.1/hooks",
        "https://internal.example/hooks", // a name that resolves to a private address
        "https://metadata.example/hooks", // a name that resolves to the metadata address
        "https://mixed.example/hooks", // one public answer, one private: refused whole
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_non_public_address_is_refused_and_logged_as_a_security_event(string url)
    {
        var logs = new CapturedLogs();
        using var sender = Sender(logs);

        var result = await sender.SendAsync(new Uri(url), "{}", "t=1,v1=00", Guid.NewGuid(), Ct);

        Assert.Equal((null, GuardedWebhookSender.RefusedAddress), (result.StatusCode, result.Error));
        var entry = Assert.Single(logs.Entries, e => e.Message.StartsWith("Security: refused a webhook connection", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(new Uri(url).Host.Trim('[', ']'), entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/hooks", entry.Message, StringComparison.Ordinal); // the path may carry a token
    }

    [Fact]
    public async Task The_machine_s_own_resolver_is_refused_localhost_too()
    {
        var logs = new CapturedLogs();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        using var sender = new GuardedWebhookSender(new DnsHostResolver(), new WebhookAddresses(null), new WebhookOptions(), factory.CreateLogger<GuardedWebhookSender>());

        var result = await sender.SendAsync(new Uri("https://localhost:8443/"), "{}", "t=1,v1=00", Guid.NewGuid(), Ct);

        Assert.Equal(GuardedWebhookSender.RefusedAddress, result.Error);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_even_from_an_allowed_receiver()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.RedirectTo = "http://169.254.169.254/latest/meta-data/";
        var logs = new CapturedLogs();
        using var sender = Sender(logs, allowed: receiver.Url);

        var result = await sender.SendAsync(new Uri(receiver.Url, "hooks"), "{}", "t=1,v1=00", Guid.NewGuid(), Ct);

        Assert.Equal((302, "redirect-not-followed"), (result.StatusCode!.Value, result.Error!));
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task The_development_receiver_is_allowed_on_its_own_host_and_port_only()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        var logs = new CapturedLogs();
        using var sender = Sender(logs, allowed: receiver.Url);

        var allowed = await sender.SendAsync(new Uri(receiver.Url, "hooks"), "{}", "t=1,v1=00", Guid.NewGuid(), Ct);
        var otherPort = await sender.SendAsync(new Uri($"http://127.0.0.1:{receiver.Url.Port + 1}/hooks"), "{}", "t=1,v1=00", Guid.NewGuid(), Ct);
        var otherHost = await sender.SendAsync(new Uri($"http://10.0.0.1:{receiver.Url.Port}/hooks"), "{}", "t=1,v1=00", Guid.NewGuid(), Ct);

        Assert.Equal((200, (string?)null), (allowed.StatusCode!.Value, allowed.Error));
        Assert.Equal(GuardedWebhookSender.RefusedAddress, otherPort.Error);
        Assert.Equal(GuardedWebhookSender.RefusedAddress, otherHost.Error);
    }

    [Fact]
    public async Task An_unknown_name_fails_without_reaching_anything()
    {
        using var sender = Sender(new CapturedLogs());

        var result = await sender.SendAsync(new Uri("https://nowhere.example/"), "{}", "t=1,v1=00", Guid.NewGuid(), Ct);

        Assert.Equal((null, "dns-failed"), (result.StatusCode, result.Error));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("172.32.0.1", true)] // just outside 172.16/12
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("2a00:1450:4001::1", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.255.255.255", false)]
    [InlineData("172.16.0.0", false)]
    [InlineData("192.168.255.255", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.0.2.1", false)] // documentation
    [InlineData("198.18.0.1", false)] // benchmarking
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("::127.0.0.1", false)] // IPv4-compatible
    [InlineData("fc00::1", false)]
    [InlineData("fdff:ffff::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)] // documentation
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)] // Teredo carrying 192.0.2.45
    [InlineData("::ffff:192.168.0.1", false)]
    public void Only_public_unicast_addresses_are_public(string address, bool expected) =>
        Assert.Equal(expected, PublicAddresses.IsPublic(IPAddress.Parse(address)));

    private static GuardedWebhookSender Sender(CapturedLogs logs, Uri? allowed = null)
    {
        var resolver = new FakeResolver(new Dictionary<string, IPAddress[]>
        {
            ["localhost"] = [IPAddress.Loopback, IPAddress.IPv6Loopback],
            ["internal.example"] = [IPAddress.Parse("10.20.30.40")],
            ["metadata.example"] = [IPAddress.Parse("169.254.169.254")],
            ["mixed.example"] = [IPAddress.Parse("8.8.4.4"), IPAddress.Parse("192.168.0.1")],
        });
#pragma warning disable CA2000 // the logger factory lives as long as the test; its provider is the captured log
        var factory = LoggerFactory.Create(b => b.AddProvider(logs));
#pragma warning restore CA2000
        return new GuardedWebhookSender(resolver, new WebhookAddresses(allowed), new WebhookOptions(), factory.CreateLogger<GuardedWebhookSender>());
    }
}
