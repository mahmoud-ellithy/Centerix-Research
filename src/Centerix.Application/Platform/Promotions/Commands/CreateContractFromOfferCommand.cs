namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Subscriptions;
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

        // Load the offer with ALL its snapshot children — benefits, features, pricing tiers.
        // These are the ONLY commercial/entitlement sources; the current Plan is never read.
        var offer = await dbContext.Offers
            .Include(o => o.Benefits)
            .Include(o => o.Features)
            .Include(o => o.PricingTiers)
            .FirstOrDefaultAsync(o => o.Id == request.OfferId, cancellationToken);

        if (offer is null)
            return OfferErrors.NotFound(request.OfferId);

        // Tenant isolation
        if (!string.Equals(offer.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            return OfferErrors.CrossTenantOffer;

        // Verify offer is accepted and ready for conversion
        if (!offer.IsConvertible)
            return OfferErrors.InvalidStateTransition(offer.Status, "convert to contract");

        // The accepted Offer must be a complete commercial snapshot. Legacy
        // (version-0) offers predate the full snapshot and CANNOT be safely
        // converted — historical values are never fabricated from the current Plan.
        if (!offer.HasCompleteEntitlementSnapshot)
            return Error.Validation(
                "Offer.IncompleteSnapshot",
                $"Offer '{offer.Id}' does not carry a complete entitlement snapshot " +
                $"(version {offer.EntitlementSnapshotVersion}, expected >= " +
                $"{Offer.CompleteEntitlementSnapshotVersion}). It cannot be converted " +
                "to a Contract; recalculate the Offer.");

        var utcNow = DateTime.UtcNow;
        var effectiveAt = request.EffectiveAtUtc ?? utcNow;

        // Contract period uses ONLY the Offer snapshot: DurationMonths + BonusMonths.
        // (identical semantics to TenantPlan's BaseEndsAtUtc → EffectiveEndsAt calculation).
        var endsAt = TenantPlan.ComputeEffectiveEndsAtUtc(effectiveAt, offer.DurationMonths, offer.BonusMonths);

        // Resolve entitlements — exclusively from the Offer snapshot
        var bonusMonths = offer.BonusMonths;
        var maxStudents = offer.MaxStudents;
        var maxUsers = offer.MaxUsers;
        var maxBranches = offer.MaxBranches;
        var maxTeachers = offer.MaxTeachers;
        var storageGb = offer.StorageGB;
        var smsQuota = offer.SMSQuota;

        // Create the Contract aggregate using ONLY Offer-derived commercial terms + Offer entitlements
        // Commercial snapshot: GrossAmount = BaseAmount (pre-discount), ContractedAmount = FinalAmount (post-discount)
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
            grossAmount: offer.BaseAmount,
            contractedAmount: offer.FinalAmount,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
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

        // Snapshot pricing tiers from the Offer snapshot into the Contract
        // (historical tiers captured at Offer calculation time)
        {
            var seenDurations = new HashSet<int>();
            foreach (var offerTier in offer.PricingTiers.OrderBy(t => t.DisplayOrder))
            {
                if (!seenDurations.Add(offerTier.DurationMonths))
                    continue;

                var tierResult = ContractPricingTier.Create(
                    id: Guid.NewGuid(),
                    contractId: contract.Id,
                    durationMonths: offerTier.DurationMonths,
                    tierPrice: offerTier.TierPrice,
                    currencyCode: offer.CurrencyCode,
                    monthlyListPrice: offer.MonthlyListPrice,
                    displayOrder: offerTier.DisplayOrder);

                if (!tierResult.IsSuccess)
                    return tierResult.Errors!;

                contract.AddPricingTier(tierResult.Value);
            }
        }

        // Snapshot feature entitlements from the Offer snapshot into the Contract
        {
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var offerFeature in offer.Features)
            {
                if (!seenCodes.Add(offerFeature.FeatureCode))
                    continue;

                var featureResult = ContractFeature.Create(contract.Id, offerFeature.FeatureCode);
                if (!featureResult.IsSuccess)
                    return featureResult.Errors!;

                var addFeatureResult = contract.AddContractFeature(featureResult.Value);
                if (!addFeatureResult.IsSuccess)
                    return addFeatureResult.Errors!;
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
