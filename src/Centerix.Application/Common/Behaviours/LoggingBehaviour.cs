using MediatR;
using Microsoft.Extensions.Logging;

namespace Centerix.Application.Common.Behaviours;

public class LoggingBehaviour<TRequest, TResponse>(
    ILogger<TRequest> logger) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        logger.LogInformation("Handling {RequestName}", requestName);

        var response = await next();

        logger.LogInformation("Handled {RequestName}", requestName);

        return response;
    }
}
