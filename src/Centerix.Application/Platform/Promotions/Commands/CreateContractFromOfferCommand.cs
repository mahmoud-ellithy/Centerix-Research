namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to create a Contract from an accepted Offer.
/// ALL commercial values are derived from the accepted Offer snapshot.
/// The client CANNOT override any authoritative commercial values.
///
/// Allowed client inputs:
/// - OfferId (required)
/// - ContractNumber (required, but must be unique within tenant)
/// - EffectiveAtUtc (optional, defaults to now)
///
/// Benefits are read exclusively from the persisted Offer snapshot.
/// </summary>
public record CreateContractFromOfferCommand(
    Guid OfferId,
    string ContractNumber,
    DateTime? EffectiveAtUtc = null) : IRequest<Result<Guid>>;

/// <summary>
/// Handler for CreateContractFromOfferCommand.
/// Creates the Contract aggregate from the accepted Offer snapshot.
/// Tenant is resolved from ICurrentTenant — never from client input.
/// Commercial values are NEVER accepted from the HTTP request.
/// Benefits are read exclusively from the Offer snapshot.
/// </summary>
public class CreateContractFromOfferHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant) : IRequestHandler<CreateContractFromOfferCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateContractFromOfferCommand request, CancellationToken cancellationToken)
    {
        var tenantId = currentTenant.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return ContractErrors.TenantNotResolved;

        // Load the offer with its benefits snapshot
        var offer = await dbContext.Offers
            .Include(o => o.Benefits)
            .FirstOrDefaultAsync(o => o.Id == request.OfferId, cancellationToken);

        if (offer is null)
            return OfferErrors.NotFound(request.OfferId);

        // Tenant isolation
        if (!string.Equals(offer.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            return OfferErrors.CrossTenantOffer;

        // Verify offer is accepted and ready for conversion
        if (!offer.IsConvertible)
            return OfferErrors.InvalidStateTransition(offer.Status, "convert to contract");

        // Load plan for pricing tier snapshot, limits, and feature entitlements
        var plan = await dbContext.Plans
            .Include(p => p.PricingTiers)
            .Include(p => p.PlanFeatures)
            .FirstOrDefaultAsync(p => p.Id == offer.PlanId, cancellationToken);

        if (plan is null)
            return OfferErrors.PlanNotFound;

        var utcNow = DateTime.UtcNow;
        var effectiveAt = request.EffectiveAtUtc ?? utcNow;

        // Contract period must align with subscription period: endsAt = effectiveAt + DurationMonths + BonusMonths
        var endsAt = effectiveAt.AddMonths(offer.DurationMonths + plan.BonusMonths);

        // Resolve plan limits — fail if plan is missing (per prompt section #7)
        var bonusMonths = plan.BonusMonths;
        var maxStudents = plan.MaxStudents;
        var maxUsers = plan.MaxUsers;
        var maxBranches = plan.MaxBranches;
        var maxTeachers = plan.MaxTeachers;
        var storageGb = plan.StorageGB;
        var smsQuota = plan.SMSQuota;

        // Create the Contract aggregate using Offer-derived commercial terms + Plan limits
        var contractResult = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: request.ContractNumber,
            planId: offer.PlanId,
            effectiveAtUtc: effectiveAt,
            endsAtUtc: endsAt,
            durationMonths: offer.DurationMonths,
            monthlyListPrice: offer.MonthlyListPrice,
            contractualMonthlyValue: offer.MonthlyListPrice,
            currencyCode: offer.CurrencyCode,
            contractedAmount: offer.FinalAmount,
            discountAmount: offer.DiscountAmount,
            promotionReference: offer.PromotionName,
            promotionId: offer.PromotionId,
            promotionType: offer.PromotionType,
            chargedMonths: offer.ChargedMonths,
            bonusMonths: bonusMonths,
            maxStudents: maxStudents,
            maxUsers: maxUsers,
            maxBranches: maxBranches,
            maxTeachers: maxTeachers,
            storageGb: storageGb,
            smsQuota: smsQuota);

        if (!contractResult.IsSuccess)
            return contractResult.Errors!;

        var contract = contractResult.Value;

        // Validate that the entitlement snapshot is complete per prompt section #5
        var snapshotValidation = contract.ValidateSnapshotCompleteness();
        if (!snapshotValidation.IsSuccess)
            return snapshotValidation.Errors!;

        // Snapshot pricing tiers from the Plan catalog into the Contract
        {
            var seenDurations = new HashSet<int>();
            foreach (var planTier in plan.PricingTiers.OrderBy(t => t.DisplayOrder))
            {
                if (!seenDurations.Add(planTier.DurationMonths))
                    continue;

                var tierResult = ContractPricingTier.Create(
                    id: Guid.NewGuid(),
                    contractId: contract.Id,
                    durationMonths: planTier.DurationMonths,
                    tierPrice: planTier.TierPrice,
                    currencyCode: offer.CurrencyCode,
                    monthlyListPrice: offer.MonthlyListPrice,
                    displayOrder: planTier.DisplayOrder);

                if (!tierResult.IsSuccess)
                    return tierResult.Errors!;

                contract.AddPricingTier(tierResult.Value);
            }
        }

        // Snapshot feature entitlements from the Plan catalog into the Contract
        if (plan is not null)
        {
            foreach (var pf in plan.PlanFeatures.Where(f => f.IsEnabled))
            {
                var feature = await dbContext.Features
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

        // Copy benefits from the Offer snapshot (authoritative source)
        // Benefits are NOT accepted from the client request
        if (offer.Benefits is { Count: > 0 })
        {
            foreach (var offerBenefit in offer.Benefits)
            {
                var benefitResult = ContractBenefit.Create(
                    id: Guid.NewGuid(),
                    contractId: contract.Id,
                    benefitType: offerBenefit.BenefitType,
                    name: offerBenefit.Name,
                    description: offerBenefit.Description,
                    contractualValue: offerBenefit.ContractualValue,
                    currencyCode: offerBenefit.CurrencyCode);

                if (!benefitResult.IsSuccess)
                    return benefitResult.Errors!;

                var addBenefitResult = contract.AddBenefit(benefitResult.Value);
                if (!addBenefitResult.IsSuccess)
                    return addBenefitResult.Errors!;
            }
        }

        // Mark offer as converted (idempotent)
        var markConvertedResult = offer.MarkConverted(contract.Id, utcNow);
        if (!markConvertedResult.IsSuccess)
            return markConvertedResult.Errors!;

        dbContext.Contracts.Add(contract);
        await dbContext.SaveChangesAsync(cancellationToken);

        return contract.Id;
    }
}
