namespace Centerix.Application.Platform.Contracts.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Subscriptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to create a new Contract with immutable commercial snapshot.
/// Tenant is NOT accepted from the client; it is resolved from the authenticated tenant context.
/// </summary>
/// <remarks>
/// NON-PRODUCTION PATH: no API controller, background job, or production workflow sends this
/// command. Production Contract creation flows exclusively through
/// CreateContractFromOfferCommand, RenewSubscriptionOfferCommand, and
/// ChangeSubscriptionPlanCommand, which derive commercial terms from authoritative
/// Offer/Plan sources. This handler exists for testing/manual entry only and loads
/// entitlement limits and features from the Plan catalog (never from the client).
/// The client-supplied <c>EndsAtUtc</c> is ignored; the period end is always derived
/// authoritatively from effective date + duration + Plan bonus months.
/// </remarks>
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
        // A missing Plan must fail creation — never fall back to zeroed entitlements.
        var plan = await _dbContext.Plans
            .Include(p => p.PlanFeatures)
            .FirstOrDefaultAsync(p => p.Id == request.PlanId, cancellationToken);

        if (plan is null)
            return ContractErrors.PlanNotFound(request.PlanId);

        // The client MUST NOT control the commercial period end: derive it authoritatively
        // from the effective date, duration, and the Plan's bonus months using the same
        // calendar-month helper as every production path, so this command can never
        // produce a Contract whose EndsAtUtc disagrees with its Subscription's
        // EffectiveEndsAtUtc. The request.EndsAtUtc value is intentionally ignored.
        var endsAtUtc = TenantPlan.ComputeEffectiveEndsAtUtc(
            request.EffectiveAtUtc, request.DurationMonths, plan.BonusMonths);

        // Create the Contract aggregate — client-supplied commercial terms only (price, duration, etc.)
        // Compute GrossAmount from ContractedAmount + DiscountAmount (invariant: ContractedAmount = GrossAmount - DiscountAmount)
        var grossAmount = request.ContractedAmount + request.DiscountAmount;
        var contractResult = Contract.Create(
            request.ContractId,
            tenantId,
            request.ContractNumber,
            request.PlanId,
            request.EffectiveAtUtc,
            endsAtUtc,
            request.DurationMonths,
            request.MonthlyListPrice,
            request.ContractualMonthlyValue,
            request.CurrencyCode,
            grossAmount,
            request.ContractedAmount,
            Contract.CompleteEntitlementSnapshotVersion,
            request.DiscountAmount,
            request.PromotionReference,
            request.PromotionId,
            request.PromotionType,
            request.ChargedMonths,
            bonusMonths: plan.BonusMonths,
            maxStudents: plan.MaxStudents,
            maxUsers: plan.MaxUsers,
            maxBranches: plan.MaxBranches,
            maxTeachers: plan.MaxTeachers,
            storageGb: plan.StorageGB,
            smsQuota: plan.SMSQuota);

        if (!contractResult.IsSuccess)
            return contractResult.Errors!;

        var contract = contractResult.Value;

        // Never stamp a complete snapshot version on incomplete data: even this
        // non-production path must satisfy the production snapshot invariant.
        var snapshotValidation = contract.ValidateSnapshotCompleteness();
        if (!snapshotValidation.IsSuccess)
            return snapshotValidation.Errors!;

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
                    var featureResult = ContractFeature.Create(contract.Id, feature);
                    if (!featureResult.IsSuccess)
                        return featureResult.Errors!;

                    var addFeatureResult = contract.AddContractFeature(featureResult.Value);
                    if (!addFeatureResult.IsSuccess)
                        return addFeatureResult.Errors!;
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
