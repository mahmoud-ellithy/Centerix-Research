namespace Centerix.Application.Platform.Promotions.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;
using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Query to get a Promotion by ID.
/// </summary>
public record GetPromotionByIdQuery(int Id) : IRequest<Result<PromotionDto>>;

/// <summary>
/// Query to list all Promotions.
/// </summary>
public record ListPromotionsQuery : IRequest<Result<List<PromotionDto>>>;

public class GetPromotionByIdHandler(IAppDbContext dbContext)
    : IRequestHandler<GetPromotionByIdQuery, Result<PromotionDto>>
{
    public async Task<Result<PromotionDto>> Handle(GetPromotionByIdQuery request, CancellationToken cancellationToken)
    {
        var promotion = await dbContext.Promotions
            .Where(p => p.Id == request.Id)
            .Select(p => new PromotionDto
            {
                Id = p.Id,
                Name = p.Name,
                Code = p.Code,
                Type = (byte)p.Type,
                Status = (byte)p.Status,
                PlanId = p.PlanId,
                DurationMonths = p.DurationMonths,
                StartsAtUtc = p.StartsAtUtc,
                EndsAtUtc = p.EndsAtUtc,
                Priority = p.Priority,
                Percentage = p.Percentage,
                FixedAmount = p.FixedAmount,
                PromotionalPrice = p.PromotionalPrice,
                ChargedMonths = p.ChargedMonths,
                CreatedAtUtc = p.CreatedAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (promotion is null)
            return PromotionErrors.NotFound(request.Id);

        return promotion;
    }
}

public class ListPromotionsHandler(IAppDbContext dbContext)
    : IRequestHandler<ListPromotionsQuery, Result<List<PromotionDto>>>
{
    public async Task<Result<List<PromotionDto>>> Handle(ListPromotionsQuery request, CancellationToken cancellationToken)
    {
        var promotions = await dbContext.Promotions
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.CreatedAtUtc)
            .Select(p => new PromotionDto
            {
                Id = p.Id,
                Name = p.Name,
                Code = p.Code,
                Type = (byte)p.Type,
                Status = (byte)p.Status,
                PlanId = p.PlanId,
                DurationMonths = p.DurationMonths,
                StartsAtUtc = p.StartsAtUtc,
                EndsAtUtc = p.EndsAtUtc,
                Priority = p.Priority,
                Percentage = p.Percentage,
                FixedAmount = p.FixedAmount,
                PromotionalPrice = p.PromotionalPrice,
                ChargedMonths = p.ChargedMonths,
                CreatedAtUtc = p.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return promotions;
    }
}
