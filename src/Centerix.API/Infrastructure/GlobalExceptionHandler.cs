using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Centerix.Application.Common.Interfaces;

namespace Centerix.API.Infrastructure;

public class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILocalizer localizer,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception occurred during request processing.");

        if (exception is ValidationException validationException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

            var errors = validationException.Errors
                .Select(e => new
                {
                    propertyName = e.PropertyName,
                    errorMessage = e.ErrorMessage
                })
                .ToList();

            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.4.2",
                    Title = localizer.Translate("Error:Validation"),
                    Detail = "One or more validation errors occurred.",
                    Extensions = { ["errors"] = errors }
                }
            });
        }

        // Optimistic concurrency conflict (Teacher RowVersion, SalaryPayment RowVersion):
        // the row was modified by another request between the read and the save. This is a
        // client-retryable conflict, mapped to 409 consistent with ErrorKind.Conflict.
        if (exception is DbUpdateConcurrencyException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
                    Title = localizer.Translate("Error:Concurrency"),
                    Detail = "The record was modified by another request. Reload the record and try again.",
                }
            });
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                Title = localizer.Translate("Error:Application"),
                Detail = "An unexpected error occurred. Please try again later.",
            }
        });
    }
}
