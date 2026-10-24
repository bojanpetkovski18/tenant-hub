using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using TenantHub.Api.Common.Auth;

namespace TenantHub.Api.Common;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";

    /// <summary>Sign-up and sign-in attempts per client IP per window (D022).</summary>
    public int AuthPermitLimit { get; init; } = 10;

    public int AuthWindowSeconds { get; init; } = 60;

    /// <summary>Invitation sends (per user) and invitation look-ups and acceptances (per client IP) per window.</summary>
    public int InvitationPermitLimit { get; init; } = 20;

    public int InvitationWindowSeconds { get; init; } = 60;

    /// <summary>The public demo's lower limits (SPEC 4.1), used instead of the two above when <c>DEMO_MODE</c> is on.</summary>
    public int DemoAuthPermitLimit { get; init; } = 5;

    public int DemoInvitationPermitLimit { get; init; } = 5;
}

public static class RateLimiting
{
    public const string AuthPolicy = "auth";
    public const string InvitationPolicy = "invitations";

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        if (configuration.GetValue("DEMO_MODE", false))
        {
            options = new RateLimitOptions
            {
                AuthPermitLimit = options.DemoAuthPermitLimit,
                AuthWindowSeconds = options.AuthWindowSeconds,
                InvitationPermitLimit = options.DemoInvitationPermitLimit,
                InvitationWindowSeconds = options.InvitationWindowSeconds,
            };
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problemDetails.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many attempts.",
                        Detail = "Please wait a minute and try again.",
                    },
                });
            };

            // Per client IP. Per-account brute force is covered by Identity's lockout (5 failures, 15 minutes).
            // Behind a reverse proxy this needs forwarded headers (configured with the deployment, M11).
            limiter.AddPolicy(AuthPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.AuthPermitLimit,
                    Window = TimeSpan.FromSeconds(options.AuthWindowSeconds),
                    QueueLimit = 0,
                }));

            // Signed in: per user, so one account cannot flood inboxes from many addresses. Otherwise per client IP.
            limiter.AddPolicy(InvitationPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.IsAuthenticated == true
                    ? "user:" + context.User.GetUserId()
                    : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.InvitationPermitLimit,
                    Window = TimeSpan.FromSeconds(options.InvitationWindowSeconds),
                    QueueLimit = 0,
                }));
        });

        return services;
    }
}
