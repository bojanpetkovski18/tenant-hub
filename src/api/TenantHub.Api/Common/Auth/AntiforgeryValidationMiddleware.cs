using Microsoft.AspNetCore.Antiforgery;

namespace TenantHub.Api.Common.Auth;

/// <summary>
/// CSRF protection for the cookie-authenticated JSON API (D009). The built-in antiforgery middleware only
/// validates form posts, so unsafe <c>/api</c> requests are checked here: the <c>X-XSRF-TOKEN</c> header must
/// match the antiforgery cookie. Sign-in and sign-up are included (login CSRF). Endpoints that authenticate
/// differently would opt out with <c>.DisableAntiforgery()</c>; there are none yet.
/// </summary>
public sealed partial class AntiforgeryValidationMiddleware(
    RequestDelegate next,
    IAntiforgery antiforgery,
    IProblemDetailsService problemDetails,
    ILogger<AntiforgeryValidationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresValidation(context))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                LogRejected(logger, context.Request.Method, context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await problemDetails.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status400BadRequest,
                        Title = "Invalid or missing anti-forgery token.",
                        Detail = "Reload the page and try again.",
                    },
                });
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresValidation(HttpContext context)
    {
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
        {
            return false;
        }

        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            return false;
        }

        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>();
        return metadata?.RequiresValidation != false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected {Method} {Path}: invalid or missing anti-forgery token.")]
    private static partial void LogRejected(ILogger logger, string method, string path);
}
