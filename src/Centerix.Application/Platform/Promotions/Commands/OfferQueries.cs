namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Query to get an Offer by ID.
/// </summary>
public record GetOfferByIdQuery(Guid Id) : IRequest<Result<OfferDto>>;

/// <summary>
/// Query to list all Offers for the current tenant.
/// </summary>
public record ListOffersQuery() : IRequest<Result<List<OfferDto>>>;

public class GetOfferByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetOfferByIdQuery, Result<OfferDto>>
{
    public async Task<Result<OfferDto>> Handle(GetOfferByIdQuery request, CancellationToken cancellationToken)
    {
        var offer = await dbContext.Offers
            .Include(o => o.Benefits)
            .FirstOrDefaultAsync(o => o.Id == request.Id, cancellationToken);

        if (offer is null)
            return OfferErrors.NotFound(request.Id);

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

public class ListOffersHandler(IAppDbContext dbContext) : IRequestHandler<ListOffersQuery, Result<List<OfferDto>>>
{
    public async Task<Result<List<OfferDto>>> Handle(ListOffersQuery request, CancellationToken cancellationToken)
    {
        var offers = await dbContext.Offers
            .Include(o => o.Benefits)
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return offers.Select(o => new OfferDto
        {
            Id = o.Id,
            Status = (byte)o.Status,
            PlanId = o.PlanId,
            DurationMonths = o.DurationMonths,
            BaseAmount = o.BaseAmount,
            DiscountAmount = o.DiscountAmount,
            FinalAmount = o.FinalAmount,
            MonthlyListPrice = o.MonthlyListPrice,
            CurrencyCode = o.CurrencyCode,
            PromotionId = o.PromotionId,
            PromotionName = o.PromotionName,
            PromotionCode = o.PromotionCode,
            PromotionType = o.PromotionType,
            DiscountPercentage = o.DiscountPercentage,
            ChargedMonths = o.ChargedMonths,
            CalculatedAtUtc = o.CalculatedAtUtc,
            ExpiresAtUtc = o.ExpiresAtUtc,
            AcceptedAtUtc = o.AcceptedAtUtc,
            ConvertedAtUtc = o.ConvertedAtUtc,
            ContractId = o.ContractId,
            Benefits = o.Benefits.Select(b => new OfferBenefitDto
            {
                Id = b.Id,
                BenefitType = b.BenefitType,
                Name = b.Name,
                Description = b.Description,
                ContractualValue = b.ContractualValue,
                CurrencyCode = b.CurrencyCode
            }).ToList()
        }).ToList();
    }
}
