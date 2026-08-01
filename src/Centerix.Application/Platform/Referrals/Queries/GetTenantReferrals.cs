namespace Centerix.Application.Platform.Referrals.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetTenantReferralsQuery : IRequest<Result<IEnumerable<TenantReferralDto>>>;

public class GetTenantReferralsHandler(IAppDbContext dbContext) : IRequestHandler<GetTenantReferralsQuery, Result<IEnumerable<TenantReferralDto>>>
{
    public async Task<Result<IEnumerable<TenantReferralDto>>> Handle(
        GetTenantReferralsQuery request,
        CancellationToken cancellationToken)
    {
        var referrals = await dbContext.TenantReferrals
            .ProjectToType<TenantReferralDto>()
            .ToListAsync(cancellationToken);

        return referrals;
    }
}
