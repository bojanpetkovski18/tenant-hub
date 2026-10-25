using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace TenantHub.Api.Common;

/// <summary>
/// Configuration section <c>ForwardedHeaders</c> (M11, for the host chosen in M12). Behind a TLS-terminating proxy the
/// app must see the client's address (rate limits partition by it) and the original scheme (secure cookies, HSTS).
/// Off by default: trusting <c>X-Forwarded-*</c> from anyone would let a client choose its own address.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string Section = "ForwardedHeaders";

    public bool Enabled { get; init; }

    /// <summary>
    /// Proxy networks to trust, in CIDR notation (loopback is always trusted). <c>0.0.0.0/0</c> and <c>::/0</c> trust
    /// any sender: only for a host whose proxy is the app's only way in.
    /// </summary>
    public IReadOnlyList<string> KnownNetworks { get; init; } = [];

    /// <summary>Individual proxy addresses to trust.</summary>
    public IReadOnlyList<string> KnownProxies { get; init; } = [];
}

public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddAppForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(ForwardedHeadersSettings.Section).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        services.AddSingleton(settings);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // One proxy in front of the app: the nearest X-Forwarded-For entry is the client.
            options.ForwardLimit = 1;
            foreach (var network in settings.KnownNetworks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }

            foreach (var proxy in settings.KnownProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }
        });
        return services;
    }

    /// <summary>First in the pipeline, so everything after it sees the client's address and scheme.</summary>
    public static WebApplication UseAppForwardedHeaders(this WebApplication app)
    {
        if (app.Services.GetRequiredService<ForwardedHeadersSettings>().Enabled)
        {
            app.UseForwardedHeaders();
        }

        return app;
    }
}
