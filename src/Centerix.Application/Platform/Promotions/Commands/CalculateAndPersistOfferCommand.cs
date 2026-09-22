namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to calculate and persist an Offer for a plan and duration.
/// The Offer is tenant-scoped and persisted as an immutable snapshot.
/// Benefits are NOT accepted from the client — they are sourced from
/// trusted commercial configuration. If no trusted source exists,
/// the Offer contains zero Benefits.
/// </summary>
public record CalculateAndPersistOfferCommand(
    int PlanId,
    int DurationMonths,
    DateTime? EvaluationTimeUtc = null) : IRequest<Result<OfferDto>>;

public class CalculateAndPersistOfferHandler(
    IAppDbContext dbContext,
    IPromotionCalculationService promotionCalculationService,
    ICurrentTenant currentTenant) : IRequestHandler<CalculateAndPersistOfferCommand, Result<OfferDto>>
{
    public async Task<Result<OfferDto>> Handle(CalculateAndPersistOfferCommand request, CancellationToken cancellationToken)
    {
        var tenantId = currentTenant.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return OfferErrors.TenantNotResolved;

        var plan = await dbContext.Plans
            .Include(p => p.PricingTiers)
            .Include(p => p.PlanFeatures)
            .FirstOrDefaultAsync(p => p.Id == request.PlanId, cancellationToken);

        if (plan is null)
            return PromotionErrors.PlanNotFound;

        var evaluationTime = request.EvaluationTimeUtc ?? DateTime.UtcNow;

        var candidatePromotions = await dbContext.Promotions
            .Where(p =>
                (p.PlanId == 0 || p.PlanId == request.PlanId) &&
                (p.DurationMonths == 0 || p.DurationMonths == request.DurationMonths))
            .ToListAsync(cancellationToken);

        var offerResult = promotionCalculationService.Calculate(
            plan,
            request.DurationMonths,
            evaluationTime,
            candidatePromotions);

        if (!offerResult.IsSuccess)
            return offerResult.Errors!;

        var calculated = offerResult.Value;

        // Persist the Offer as an immutable snapshot.
        // Plan IS authoritative at calculation time: all entitlement values,
        // feature codes and pricing tiers are copied into the Offer here.
        // AFTER this point the Offer is authoritative and Plan changes are irrelevant.
        var featureIds = plan.PlanFeatures
            .Where(f => f.IsEnabled)
            .Select(f => f.FeatureId)
            .ToList();

        var featureCodes = await dbContext.Features
            .AsNoTracking()
            .Where(f => featureIds.Contains(f.Id))
            .Select(f => f.Code)
            .ToListAsync(cancellationToken);

        var offer = Domain.Platform.Promotions.Offer.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            planId: calculated.PlanId,
            durationMonths: calculated.DurationMonths,
            baseAmount: calculated.BaseAmount,
            discountAmount: calculated.DiscountAmount,
            finalAmount: calculated.FinalAmount,
            monthlyListPrice: calculated.MonthlyListPrice,
            currencyCode: calculated.CurrencyCode,
            promotionId: calculated.PromotionId,
            promotionName: calculated.PromotionName,
            promotionCode: calculated.PromotionCode,
            promotionType: calculated.PromotionType,
            discountPercentage: calculated.DiscountPercentage,
            chargedMonths: calculated.ChargedMonths,
            calculatedAtUtc: calculated.CalculatedAtUtc,
            expiresAtUtc: calculated.ExpiresAtUtc ?? calculated.CalculatedAtUtc.AddHours(24),
            bonusMonths: plan.BonusMonths,
            maxStudents: plan.MaxStudents,
            maxUsers: plan.MaxUsers,
            maxBranches: plan.MaxBranches,
            maxTeachers: plan.MaxTeachers,
            storageGb: plan.StorageGB,
            smsQuota: plan.SMSQuota,
            entitlementSnapshotVersion: Domain.Platform.Promotions.Offer.CompleteEntitlementSnapshotVersion);

        if (!offer.IsSuccess)
            return offer.Errors!;

        // Capture feature entitlements (immutable Offer-owned snapshot children)
        foreach (var code in featureCodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var featureResult = OfferFeature.Create(Guid.NewGuid(), offer.Value.Id, code);
            if (!featureResult.IsSuccess)
                return featureResult.Errors!;

            offer.Value.AddFeature(featureResult.Value);
        }

        // Capture historical pricing tiers (immutable Offer-owned snapshot children)
        var seenDurations = new HashSet<int>();
        foreach (var tier in plan.PricingTiers.OrderBy(t => t.DisplayOrder))
        {
            if (!seenDurations.Add(tier.DurationMonths))
                continue;

            var tierResult = OfferPricingTier.Create(
                Guid.NewGuid(), offer.Value.Id, tier.DurationMonths, tier.TierPrice, tier.DisplayOrder);
            if (!tierResult.IsSuccess)
                return tierResult.Errors!;

            offer.Value.AddPricingTier(tierResult.Value);
        }

        dbContext.StampAddedTenantIds(tenantId);
        dbContext.Offers.Add(offer.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        return MapToDto(offer.Value);
    }

    private static OfferDto MapToDto(Domain.Platform.Promotions.Offer offer) => new()
    {
        Id = offer.Id,
        Status = (byte)offer.Status,
        PlanId = offer.PlanId,
        DurationMonths = offer.DurationMonths,
        BaseAmount = offer.BaseAmount,
        DiscountAmount = offer.DiscountAmount,
        FinalAmount = offer.FinalAmount,
        MonthlyListPrice = offer.MonthlyListPrice,
        CurrencyCode = offer.CurrencyCode,
        PromotionId = offer.PromotionId,
        PromotionName = offer.PromotionName,
        PromotionCode = offer.PromotionCode,
        PromotionType = offer.PromotionType,
        DiscountPercentage = offer.DiscountPercentage,
        ChargedMonths = offer.ChargedMonths,
        CalculatedAtUtc = offer.CalculatedAtUtc,
        ExpiresAtUtc = offer.ExpiresAtUtc,
        AcceptedAtUtc = offer.AcceptedAtUtc,
        ConvertedAtUtc = offer.ConvertedAtUtc,
        ContractId = offer.ContractId,
        Benefits = offer.Benefits.Select(b => new OfferBenefitDto
        {
            Id = b.Id,
            BenefitType = b.BenefitType,
            Name = b.Name,
            Description = b.Description,
            ContractualValue = b.ContractualValue,
            CurrencyCode = b.CurrencyCode
        }).ToList()
    };
}
