using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Centerix.Application.Common.Behaviours;

public class PerformanceBehaviour<TRequest, TResponse>(
    ILogger<TRequest> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();

        var response = await next();

        timer.Stop();

        var elapsedMilliseconds = timer.ElapsedMilliseconds;

        if (elapsedMilliseconds > 500)
        {
            var requestName = typeof(TRequest).Name;

            logger.LogWarning(
                "Long Running Request: {RequestName} ({ElapsedMilliseconds} milliseconds) {Request}",
                requestName, elapsedMilliseconds, request);
        }

        return response;
    }
}