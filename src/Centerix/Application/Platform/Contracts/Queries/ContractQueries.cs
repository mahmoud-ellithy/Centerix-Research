namespace Centerix.Application.Platform.Contracts.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;

using Mapster;

using MediatR;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Query to get a single Contract by its ID.
/// </summary>
public record GetContractByIdQuery(Guid ContractId) : IRequest<Result<ContractDetailDto>>;

public class GetContractByIdHandler(IAppDbContext dbContext)
    : IRequestHandler<GetContractByIdQuery, Result<ContractDetailDto>>
{
    public async Task<Result<ContractDetailDto>> Handle(
        GetContractByIdQuery request,
        CancellationToken cancellationToken)
    {
        var contract = await dbContext.Contracts
            .Include(c => c.PricingTiers)
            .Include(c => c.Benefits)
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
        {
            return ContractErrors.ContractNotFound(request.ContractId);
        }

        return contract.ProjectToType<ContractDetailDto>();
    }
}

/// <summary>
/// Query to list all Contracts for the current tenant.
/// </summary>
public record ListContractsQuery : IRequest<Result<IEnumerable<ContractDto>>>;

public class ListContractsHandler(IAppDbContext dbContext)
    : IRequestHandler<ListContractsQuery, Result<IEnumerable<ContractDto>>>
{
    public async Task<Result<IEnumerable<ContractDto>>> Handle(
        ListContractsQuery request,
        CancellationToken cancellationToken)
    {
        var contracts = await dbContext.Contracts
            .OrderByDescending(c => c.CreatedAtUtc)
            .ProjectToType<ContractDto>()
            .ToListAsync(cancellationToken);

        return contracts;
    }
}
