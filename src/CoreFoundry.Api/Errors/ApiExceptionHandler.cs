using CoreFoundry.Application.Common;
using CoreFoundry.Application.SchemaEngine;
using CoreFoundry.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CoreFoundry.Api.Errors;

/// <summary>Turns known application exceptions into ProblemDetails responses. Anything else stays a 500.</summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    /// <summary>Problem types for schema-apply outcomes, so clients can tell the 409s apart.</summary>
    public const string ProblemTypeBase = "https://corefoundry.dev/problems/";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            ValidationFailedException validation => new ValidationProblemDetails(
                validation.Errors.ToDictionary(pair => pair.Key, pair => pair.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more fields are invalid.",
            },
            DomainException domain => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The request breaks a business rule.",
                Detail = domain.Message,
            },
            AuthenticationFailedException auth => new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Authentication failed.",
                Detail = auth.Message,
            },
            ConflictException conflict => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict.",
                Detail = conflict.Message,
            },
            PlanStaleException stale => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Type = ProblemTypeBase + "plan-stale",
                Title = "The plan is out of date.",
                Detail = stale.Message,
            },
            ApplyInProgressException busy => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Type = ProblemTypeBase + "apply-in-progress",
                Title = "Another apply is running.",
                Detail = busy.Message,
            },
            DestructiveNotAcknowledgedException destructive => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Type = ProblemTypeBase + "destructive-not-acknowledged",
                Title = "Destructive changes need confirmation.",
                Detail = destructive.Message,
            },
            ApplyFailedException failed => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Type = ProblemTypeBase + "apply-failed",
                Title = "The apply stopped at a failing statement.",
                Detail = failed.Message,
                Extensions =
                {
                    ["migrationId"] = failed.MigrationId,
                    ["failedStatement"] = failed.FailedStatement,
                    ["statement"] = failed.Statement,
                    ["error"] = failed.Error,
                },
            },
            ConcurrencyConflictException concurrency => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict.",
                Detail = concurrency.Message,
            },
            ForbiddenException forbidden => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden.",
                Detail = forbidden.Message,
            },
            NotFoundException notFound => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not found.",
                Detail = notFound.Message,
            },
            DatabaseProvisioningException provisioning => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The project database is temporarily unavailable.",
                Detail = provisioning.Message,
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
