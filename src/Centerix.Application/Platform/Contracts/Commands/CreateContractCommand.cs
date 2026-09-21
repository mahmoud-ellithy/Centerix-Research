namespace Centerix.Application.Platform.Contracts.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to create a new Contract with immutable commercial snapshot.
/// Tenant is NOT accepted from the client; it is resolved from the authenticated tenant context.
/// </summary>
public record CreateContractCommand(
    Guid ContractId,
    string ContractNumber,
    int PlanId,
    DateTime EffectiveAtUtc,
    DateTime EndsAtUtc,
    int DurationMonths,
    decimal MonthlyListPrice,
    decimal ContractualMonthlyValue,
    string CurrencyCode,
    decimal ContractedAmount,
    decimal DiscountAmount,
    string? PromotionReference,
    List<CreatePricingTierRequest> PricingTiers,
    List<CreateBenefitRequest> Benefits,
    int? PromotionId = null,
    string? PromotionType = null,
    int? ChargedMonths = null) : IRequest<Result<Guid>>;

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
/// Tenant is resolved from ICurrentTenant — never from client input.
/// </summary>
public class CreateContractHandler : IRequestHandler<CreateContractCommand, Result<Guid>>
{
    private readonly IAppDbContext _dbContext;
    private readonly ICurrentTenant _currentTenant;

    public CreateContractHandler(IAppDbContext dbContext, ICurrentTenant currentTenant)
    {
        _dbContext = dbContext;
        _currentTenant = currentTenant;
    }

    public async Task<Result<Guid>> Handle(CreateContractCommand request, CancellationToken cancellationToken)
    {
        // Resolve tenant from authenticated context — NEVER trust client-supplied TenantId
        var tenantId = _currentTenant.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return ContractErrors.TenantNotResolved;

        // NOTE: This command is NOT used by any production Contract creation workflow.
        // Production paths: CreateContractFromOfferCommand, RenewSubscriptionOfferCommand,
        // ChangeSubscriptionPlanCommand — all of which populate the full snapshot from
        // authoritative Plan/Offer sources. This handler exists for testing/manual entry only.
        // Limits and features are NOT accepted from the client; they are loaded from the Plan.
        var plan = await _dbContext.Plans
            .Include(p => p.PlanFeatures)
            .FirstOrDefaultAsync(p => p.Id == request.PlanId, cancellationToken);

        // Create the Contract aggregate — client-supplied commercial terms only (price, duration, etc.)
        var contractResult = Contract.Create(
            request.ContractId,
            tenantId,
            request.ContractNumber,
            request.PlanId,
            request.EffectiveAtUtc,
            request.EndsAtUtc,
            request.DurationMonths,
            request.MonthlyListPrice,
            request.ContractualMonthlyValue,
            request.CurrencyCode,
            request.ContractedAmount,
            request.DiscountAmount,
            request.PromotionReference,
            request.PromotionId,
            request.PromotionType,
            request.ChargedMonths,
            bonusMonths: plan?.BonusMonths ?? 0,
            maxStudents: plan?.MaxStudents ?? 0,
            maxUsers: plan?.MaxUsers ?? 0,
            maxBranches: plan?.MaxBranches ?? 0,
            maxTeachers: plan?.MaxTeachers ?? 0,
            storageGb: plan?.StorageGB ?? 0,
            smsQuota: plan?.SMSQuota ?? 0);

        if (!contractResult.IsSuccess)
            return contractResult.Errors!;

        var contract = contractResult.Value;

        // Validate and add pricing tier snapshots
        var seenDurations = new HashSet<int>();
        foreach (var tierRequest in request.PricingTiers)
        {
            // Domain-level duplicate duration validation
            if (!seenDurations.Add(tierRequest.DurationMonths))
                return ContractErrors.PricingTier.DuplicateDuration(tierRequest.DurationMonths);

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

            // Validate tier currency matches contract currency
            if (!string.Equals(tierResult.Value.CurrencyCode, contract.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                return ContractErrors.PricingTier.CurrencyMismatch(contract.CurrencyCode);

            contract.AddPricingTier(tierResult.Value);
        }

        // Snapshot feature entitlements from the Plan catalog into the Contract
        if (plan is not null)
        {
            foreach (var pf in plan.PlanFeatures.Where(f => f.IsEnabled))
            {
                var feature = await _dbContext.Features
                    .AsNoTracking()
                    .Where(f => f.Id == pf.FeatureId)
                    .Select(f => f.Code)
                    .FirstOrDefaultAsync(cancellationToken);

                if (feature is not null)
                {
                    contract.AddContractFeature(
                        ContractFeature.Create(contract.Id, feature));
                }
            }
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

            // Validate benefit currency matches contract currency
            if (!string.Equals(benefitResult.Value.CurrencyCode, contract.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                return ContractErrors.Benefit.CurrencyMismatch(contract.CurrencyCode);

            var addBenefitResult = contract.AddBenefit(benefitResult.Value);
            if (!addBenefitResult.IsSuccess)
                return addBenefitResult.Errors!;
        }

        _dbContext.Contracts.Add(contract);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return contract.Id;
    }
}
