using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TenantHub.Api.Common;

public static class ProblemDetailsSetup
{
    /// <summary>
    /// Every error response is RFC 7807 problem details carrying a <c>traceId</c>, so a user-reported error can be
    /// matched to a log entry. Validation errors are keyed by the JSON property name (<c>displayName</c>), whether
    /// they come from request validation (which uses C# names) or from a handler, so clients see one spelling.
    /// </summary>
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

                if (context.ProblemDetails is HttpValidationProblemDetails validation)
                {
                    var camelCased = validation.Errors.ToDictionary(
                        e => JsonNamingPolicy.CamelCase.ConvertName(e.Key),
                        e => e.Value,
                        StringComparer.Ordinal);
                    validation.Errors.Clear();
                    foreach (var (key, messages) in camelCased)
                    {
                        validation.Errors[key] = messages;
                    }
                }
            });
}
