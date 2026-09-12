namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to accept an Offer. The customer confirms the commercial terms.
/// The Offer must be in Calculated status and not expired.
/// </summary>
public record AcceptOfferCommand(Guid OfferId) : IRequest<Result<OfferDto>>;

public class AcceptOfferHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant) : IRequestHandler<AcceptOfferCommand, Result<OfferDto>>
{
    public async Task<Result<OfferDto>> Handle(AcceptOfferCommand request, CancellationToken cancellationToken)
    {
        var tenantId = currentTenant.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return OfferErrors.TenantNotResolved;

        var offer = await dbContext.Offers
            .FirstOrDefaultAsync(o => o.Id == request.OfferId, cancellationToken);

        if (offer is null)
            return OfferErrors.NotFound(request.OfferId);

        // Tenant isolation
        if (!string.Equals(offer.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            return OfferErrors.CrossTenantOffer;

        var utcNow = DateTime.UtcNow;

        // Accept (domain validates status + expiration)
        var acceptResult = offer.Accept(utcNow);
        if (!acceptResult.IsSuccess)
            return acceptResult.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        return MapToDto(offer);
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
        ContractId = offer.ContractId
    };
}
