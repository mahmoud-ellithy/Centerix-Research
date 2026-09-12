namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Query to calculate a commercial offer for a plan and duration, applying the best eligible promotion.
/// </summary>
public record CalculateOfferQuery(
    int PlanId,
    int DurationMonths,
    DateTime? EvaluationTimeUtc = null) : IRequest<Result<CalculatedOfferDto>>;

public class CalculateOfferHandler(
    IAppDbContext dbContext,
    IPromotionCalculationService promotionCalculationService) : IRequestHandler<CalculateOfferQuery, Result<CalculatedOfferDto>>
{
    public async Task<Result<CalculatedOfferDto>> Handle(CalculateOfferQuery request, CancellationToken cancellationToken)
    {
        var plan = await dbContext.Plans
            .Include(p => p.PricingTiers)
            .FirstOrDefaultAsync(p => p.Id == request.PlanId, cancellationToken);

        if (plan is null)
            return PromotionErrors.PlanNotFound;

        var evaluationTime = request.EvaluationTimeUtc ?? DateTime.UtcNow;

        // Fetch all promotions matching this plan (PlanId == 0 means all plans) or with matching planId
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

        var offer = offerResult.Value;

        return new CalculatedOfferDto
        {
            PlanId = offer.PlanId,
            DurationMonths = offer.DurationMonths,
            BaseAmount = offer.BaseAmount,
            DiscountAmount = offer.DiscountAmount,
            FinalAmount = offer.FinalAmount,
            PromotionId = offer.PromotionId,
            PromotionName = offer.PromotionName,
            PromotionCode = offer.PromotionCode,
            PromotionType = offer.PromotionType,
            DiscountPercentage = offer.DiscountPercentage,
            ChargedMonths = offer.ChargedMonths,
            MonthlyListPrice = offer.MonthlyListPrice,
            CurrencyCode = offer.CurrencyCode,
            CalculatedAtUtc = offer.CalculatedAtUtc,
            ExpiresAtUtc = offer.ExpiresAtUtc
        };
    }
}
