using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

using Centerix.Application.Common.Interfaces;

namespace Centerix.API.Infrastructure;

public class GlobalExceptionHandler(IProblemDetailsService problemDetailsService, ILocalizer localizer) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Type = exception.GetType().Name,
                Title = localizer.Translate("Error:Application"),
                Detail = exception.Message,
            }
        });
    }
}
