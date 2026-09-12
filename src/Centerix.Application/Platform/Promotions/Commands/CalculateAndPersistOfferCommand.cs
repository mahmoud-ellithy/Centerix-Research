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

        // Persist the Offer as an immutable snapshot
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
            expiresAtUtc: calculated.ExpiresAtUtc ?? calculated.CalculatedAtUtc.AddHours(24));

        if (!offer.IsSuccess)
            return offer.Errors!;

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
