namespace Centerix.Application.Platform.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform;
using Centerix.Domain.Common.Results;
using MediatR;

/// <summary>PLATFORM: lists every subscription across tenants (Subscriptions.Read).</summary>
public record GetSubscriptionsQuery() : IRequest<Result<IEnumerable<TenantPlanDto>>>;

public class GetSubscriptionsHandler(IPlatformService platformService)
    : IRequestHandler<GetSubscriptionsQuery, Result<IEnumerable<TenantPlanDto>>>
{
    public Task<Result<IEnumerable<TenantPlanDto>>> Handle(
        GetSubscriptionsQuery request,
        CancellationToken cancellationToken)
        => platformService.GetSubscriptionsAsync(cancellationToken);
}
