using MediatR;
using Microsoft.Extensions.Logging;

namespace Centerix.Application.Common.Behaviours;

public class UnhandledExceptionBehaviour<TRequest, TResponse>(
    ILogger<TRequest> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (Exception ex)
        {
            var requestName = typeof(TRequest).Name;

            // REV-001: do not log the request payload — it may contain secrets
            // (passwords, invitation tokens, etc.). Log only the request type name.
            logger.LogError(ex, "Unhandled Exception for Request {RequestName}", requestName);

            throw;
        }
    }
}