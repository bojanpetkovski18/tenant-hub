using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;

namespace TenantHub.Api.Common;

/// <summary>
/// Turns the application's expected failures into problem details: <see cref="RequestValidationException"/>
/// becomes a 400 validation problem with field errors, <see cref="DomainException"/> a 400 or 409 with its stable
/// <c>code</c> (SPEC 7). Anything else falls through to the generic 500 without exception details.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            RequestValidationException validation => new ValidationProblemDetails(
                validation.Errors.Errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Some fields need attention.",
            },
            DomainException domain => new ProblemDetails
            {
                Status = domain.Kind == DomainErrorKind.Validation ? StatusCodes.Status400BadRequest : StatusCodes.Status409Conflict,
                Title = domain.Message,
                Extensions = { ["code"] = domain.Code },
            },
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
}
