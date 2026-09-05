namespace Centerix.Application.Platform.Contracts.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using MediatR;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Query to get a Contract by ID with its pricing tiers and benefits.
/// </summary>
public record GetContractByIdQuery(Guid Id) : IRequest<Result<ContractDetailDto>>;

/// <summary>
/// Query to list all Contracts for the current tenant.
/// </summary>
public record ListContractsQuery() : IRequest<Result<List<ContractDto>>>;

/// <summary>
/// Handler for GetContractByIdQuery.
/// </summary>
public class GetContractByIdHandler : IRequestHandler<GetContractByIdQuery, Result<ContractDetailDto>>
{
    private readonly IAppDbContext _dbContext;

    public GetContractByIdHandler(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<ContractDetailDto>> Handle(GetContractByIdQuery request, CancellationToken cancellationToken)
    {
        var contract = await _dbContext.Contracts
            .Include(c => c.PricingTiers)
            .Include(c => c.Benefits)
            .FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken);

        if (contract == null)
            return ContractErrors.ContractNotFound(request.Id);

        return new ContractDetailDto
        {
            Id = contract.Id,
            ContractNumber = contract.ContractNumber,
            Status = (byte)contract.Status,
            PlanId = contract.PlanId,
            EffectiveAtUtc = contract.EffectiveAtUtc,
            EndsAtUtc = contract.EndsAtUtc,
            DurationMonths = contract.DurationMonths,
            MonthlyListPrice = contract.MonthlyListPrice,
            CurrencyCode = contract.CurrencyCode,
            ContractedAmount = contract.ContractedAmount,
            DiscountAmount = contract.DiscountAmount,
            PromotionReference = contract.PromotionReference,
            CreatedAtUtc = contract.CreatedAtUtc,
            PricingTiers = contract.PricingTiers.Select(t => new ContractPricingTierDto
            {
                Id = t.Id,
                DurationMonths = t.DurationMonths,
                TierPrice = t.TierPrice,
                CurrencyCode = t.CurrencyCode,
                MonthlyListPrice = t.MonthlyListPrice,
                DisplayOrder = t.DisplayOrder
            }).ToList(),
            Benefits = contract.Benefits.Select(b => new ContractBenefitDto
            {
                Id = b.Id,
                BenefitType = (byte)b.BenefitType,
                Name = b.Name,
                Description = b.Description,
                ContractualValue = b.ContractualValue,
                CurrencyCode = b.CurrencyCode,
                IsGranted = b.IsGranted,
                GrantedAtUtc = b.GrantedAtUtc
            }).ToList()
        };
    }
}

/// <summary>
/// Handler for ListContractsQuery.
/// </summary>
public class ListContractsHandler : IRequestHandler<ListContractsQuery, Result<List<ContractDto>>>
{
    private readonly IAppDbContext _dbContext;

    public ListContractsHandler(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<List<ContractDto>>> Handle(ListContractsQuery request, CancellationToken cancellationToken)
    {
        var contracts = await _dbContext.Contracts
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new ContractDto
            {
                Id = c.Id,
                ContractNumber = c.ContractNumber,
                Status = (byte)c.Status,
                PlanId = c.PlanId,
                EffectiveAtUtc = c.EffectiveAtUtc,
                EndsAtUtc = c.EndsAtUtc,
                DurationMonths = c.DurationMonths,
                MonthlyListPrice = c.MonthlyListPrice,
                CurrencyCode = c.CurrencyCode,
                ContractedAmount = c.ContractedAmount,
                DiscountAmount = c.DiscountAmount,
                PromotionReference = c.PromotionReference,
                CreatedAtUtc = c.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return contracts;
    }
}
