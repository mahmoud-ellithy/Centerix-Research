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

        // Load plan for pricing tier snapshot
        var plan = await dbContext.Plans
            .Include(p => p.PricingTiers)
            .FirstOrDefaultAsync(p => p.Id == offer.PlanId, cancellationToken);

        var utcNow = DateTime.UtcNow;
        var effectiveAt = request.EffectiveAtUtc ?? utcNow;
        var endsAt = effectiveAt.AddMonths(offer.DurationMonths);

        // Create the Contract aggregate using ONLY Offer-derived values
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
            chargedMonths: offer.ChargedMonths);

        if (!contractResult.IsSuccess)
            return contractResult.Errors!;

        var contract = contractResult.Value;

        // Snapshot pricing tiers from the Plan catalog into the Contract
        if (plan is not null)
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
