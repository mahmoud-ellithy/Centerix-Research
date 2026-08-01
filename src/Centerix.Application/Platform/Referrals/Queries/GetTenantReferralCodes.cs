namespace Centerix.Application.Platform.Referrals.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using Mapster;

using MediatR;

public record GetTenantReferralCodesQuery : IRequest<Result<IEnumerable<TenantReferralCodeDto>>>;

public class GetTenantReferralCodesHandler(IAppDbContext dbContext) : IRequestHandler<GetTenantReferralCodesQuery, Result<IEnumerable<TenantReferralCodeDto>>>
{
    public async Task<Result<IEnumerable<TenantReferralCodeDto>>> Handle(
        GetTenantReferralCodesQuery request,
        CancellationToken cancellationToken)
    {
        var referralCodes = await dbContext.TenantReferralCodes
            .ProjectToType<TenantReferralCodeDto>()
            .ToListAsync(cancellationToken);

        return referralCodes;
    }
}
