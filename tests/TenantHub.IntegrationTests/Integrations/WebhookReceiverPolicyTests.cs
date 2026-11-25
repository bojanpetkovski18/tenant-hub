using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TenantHub.Api.Common;

namespace TenantHub.IntegrationTests.Integrations;

/// <summary>
/// D052: the named local receiver (<c>Webhooks:AllowedReceiver</c>) is honoured in Development and in the E2E
/// environment the second journey runs the production image in, and nowhere else, so a deployed image refuses every
/// non-public address whatever its configuration says.
/// </summary>
public sealed class WebhookReceiverPolicyTests
{
    private static readonly Uri Receiver = new("http://receiver:5099/");

    [Theory]
    [InlineData("Development")]
    [InlineData("E2E")]
    [InlineData("e2e")]
    public void The_receiver_is_honoured_in_development_and_the_journey(string environment) =>
        Assert.Equal(Receiver, WebhookReceiverPolicy.AllowedReceiver(new Environment(environment), Receiver));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    [InlineData("E2E-Production")]
    [InlineData("")]
    public void Every_other_environment_ignores_it(string environment) =>
        Assert.Null(WebhookReceiverPolicy.AllowedReceiver(new Environment(environment), Receiver));

    [Fact]
    public void Without_a_configured_receiver_there_is_none() =>
        Assert.Null(WebhookReceiverPolicy.AllowedReceiver(new Environment("E2E"), null));

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "TenantHub.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
