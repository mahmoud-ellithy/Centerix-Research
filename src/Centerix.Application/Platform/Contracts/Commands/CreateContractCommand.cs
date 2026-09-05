namespace Centerix.Application.Platform.Contracts.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using MediatR;

/// <summary>
/// Command to create a new Contract with immutable commercial snapshot.
/// </summary>
public record CreateContractCommand(
    Guid ContractId,
    string TenantId,
    string ContractNumber,
    int PlanId,
    DateTime EffectiveAtUtc,
    DateTime EndsAtUtc,
    int DurationMonths,
    decimal MonthlyListPrice,
    string CurrencyCode,
    decimal ContractedAmount,
    decimal DiscountAmount,
    string? PromotionReference,
    List<CreatePricingTierRequest> PricingTiers,
    List<CreateBenefitRequest> Benefits) : IRequest<Result<Guid>>;

/// <summary>
/// Request to create a pricing tier snapshot.
/// </summary>
public record CreatePricingTierRequest(
    Guid Id,
    int DurationMonths,
    decimal TierPrice,
    string CurrencyCode,
    decimal MonthlyListPrice,
    int DisplayOrder);

/// <summary>
/// Request to create a benefit/gift.
/// </summary>
public record CreateBenefitRequest(
    Guid Id,
    ContractBenefitType BenefitType,
    string Name,
    string? Description,
    decimal ContractualValue,
    string CurrencyCode);

/// <summary>
/// Handler for CreateContractCommand. Creates the Contract aggregate
/// and persists the immutable commercial snapshot.
/// </summary>
public class CreateContractHandler : IRequestHandler<CreateContractCommand, Result<Guid>>
{
    private readonly IAppDbContext _dbContext;

    public CreateContractHandler(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<Guid>> Handle(CreateContractCommand request, CancellationToken cancellationToken)
    {
        // Create the Contract aggregate
        var contractResult = Contract.Create(
            request.ContractId,
            request.TenantId,
            request.ContractNumber,
            request.PlanId,
            request.EffectiveAtUtc,
            request.EndsAtUtc,
            request.DurationMonths,
            request.MonthlyListPrice,
            request.CurrencyCode,
            request.ContractedAmount,
            request.DiscountAmount,
            request.PromotionReference);

        if (!contractResult.IsSuccess)
            return contractResult.Errors!;

        var contract = contractResult.Value;

        // Add pricing tier snapshots
        foreach (var tierRequest in request.PricingTiers)
        {
            var tierResult = ContractPricingTier.Create(
                tierRequest.Id,
                contract.Id,
                tierRequest.DurationMonths,
                tierRequest.TierPrice,
                tierRequest.CurrencyCode,
                tierRequest.MonthlyListPrice,
                tierRequest.DisplayOrder);

            if (!tierResult.IsSuccess)
                return tierResult.Errors!;

            contract.AddPricingTier(tierResult.Value);
        }

        // Add benefit snapshots
        foreach (var benefitRequest in request.Benefits)
        {
            var benefitResult = ContractBenefit.Create(
                benefitRequest.Id,
                contract.Id,
                benefitRequest.BenefitType,
                benefitRequest.Name,
                benefitRequest.Description,
                benefitRequest.ContractualValue,
                benefitRequest.CurrencyCode);

            if (!benefitResult.IsSuccess)
                return benefitResult.Errors!;

            var addBenefitResult = contract.AddBenefit(benefitResult.Value);
            if (!addBenefitResult.IsSuccess)
                return addBenefitResult.Errors!;
        }

        _dbContext.Contracts.Add(contract);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return contract.Id;
    }
}
