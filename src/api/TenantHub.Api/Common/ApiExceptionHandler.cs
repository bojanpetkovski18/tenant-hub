using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Api.Common;

/// <summary>
/// Turns the application's expected failures into problem details: <see cref="RequestValidationException"/>
/// becomes a 400 validation problem with field errors, <see cref="DomainException"/> a 400, 403, 404 or 409 with
/// its stable <c>code</c> (SPEC 7; 403 for a rule about who may do what to whom). A
/// <see cref="TenantIsolationException"/> is logged as a security event and answered with a bare 500. Anything else
/// falls through to the generic 500 without exception details.
/// </summary>
public sealed partial class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is TenantIsolationException isolation)
        {
            // Always a bug, possibly an attack: loud in the logs, silent to the caller.
            LogIsolationViolation(logger, isolation.Message, httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = { Status = StatusCodes.Status500InternalServerError },
            });
        }

        ProblemDetails? problem = exception switch
        {
            DomainValidationException fields => new ValidationProblemDetails(fields.Errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = fields.Message,
                Extensions = { ["code"] = fields.Code },
            },
            RequestValidationException validation => new ValidationProblemDetails(
                validation.Errors.Errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Some fields need attention.",
            },
            DomainException domain => new ProblemDetails
            {
                Status = domain.Kind switch
                {
                    DomainErrorKind.Validation => StatusCodes.Status400BadRequest,
                    DomainErrorKind.NotFound => StatusCodes.Status404NotFound,
                    DomainErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status409Conflict,
                },
                Title = domain.Message,
                Extensions = { ["code"] = domain.Code },
            }.WithDetails(domain),
            _ => null,
        };

        if (problem is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Critical, Message = "SECURITY: tenant isolation violation blocked ({Detail}) on {Method} {Path}.")]
    private static partial void LogIsolationViolation(ILogger logger, string detail, string method, PathString path);
}

internal static class ProblemExtensions
{
    /// <summary>
    /// Business-rule details a client acts on, as camelCase values: an invalid job transition lists the moves the
    /// caller may choose (SPEC 5.4); a plan limit names the limit, its value and the usage, and a blocked downgrade
    /// lists every limit in the way (SPEC 5.5).
    /// </summary>
    public static ProblemDetails WithDetails(this ProblemDetails problem, DomainException exception)
    {
        switch (exception)
        {
            case InvalidJobTransitionException transition:
                problem.Extensions["allowed"] = transition.Allowed.Select(s => Camel(s.ToString())).ToList();
                break;
            case PlanLimitReachedException reached:
                problem.Extensions["plan"] = Camel(reached.Plan.ToString());
                problem.Extensions["limit"] = Camel(reached.Breach.Limit.ToString());
                problem.Extensions["allowed"] = reached.Breach.Allowed;
                problem.Extensions["usage"] = reached.Breach.Usage;
                break;
            case PlanDowngradeBlockedException blocked:
                problem.Extensions["plan"] = Camel(blocked.Target.ToString());
                problem.Extensions["blocking"] = blocked.Blocking
                    .Select(b => new Dictionary<string, object> { ["limit"] = Camel(b.Limit.ToString()), ["allowed"] = b.Allowed, ["usage"] = b.Usage })
                    .ToList();
                break;
        }

        return problem;
    }

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
}
