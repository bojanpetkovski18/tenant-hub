using System.Diagnostics;

namespace TenantHub.Api.Common;

public static class ProblemDetailsSetup
{
    /// <summary>
    /// Every error response is RFC 7807 problem details carrying a <c>traceId</c>,
    /// so a user-reported error can be matched to a log entry.
    /// </summary>
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions["traceId"] =
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
}
