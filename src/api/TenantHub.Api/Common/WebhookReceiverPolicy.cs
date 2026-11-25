namespace TenantHub.Api.Common;

/// <summary>
/// Where the named local webhook receiver (<c>Webhooks:AllowedReceiver</c>, SPEC-V2 5.8) is honoured: Development, and
/// the <c>E2E</c> environment in which the second journey runs the production image against a test receiver (D052).
/// Every other environment, Production and Staging included, ignores the setting, so the guard refuses every
/// non-public address there. E2E changes nothing else: every other switch checks <c>IsDevelopment()</c>.
/// </summary>
public static class WebhookReceiverPolicy
{
    public const string EndToEndEnvironment = "E2E";

    public static Uri? AllowedReceiver(IHostEnvironment environment, Uri? configured) =>
        environment.IsDevelopment() || environment.IsEnvironment(EndToEndEnvironment) ? configured : null;
}
