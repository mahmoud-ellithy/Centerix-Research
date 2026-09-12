namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using MediatR;

public record CreatePromotionCommand(
    string Name,
    PromotionType Type,
    int PlanId,
    int DurationMonths,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Priority = 0,
    string? Code = null,
    decimal? Percentage = null,
    decimal? FixedAmount = null,
    decimal? PromotionalPrice = null,
    int? ChargedMonths = null) : IRequest<Result<int>>;

public class CreatePromotionHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    IAuditWriter auditWriter) : IRequestHandler<CreatePromotionCommand, Result<int>>
{
    public async Task<Result<int>> Handle(CreatePromotionCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var promotionResult = Promotion.Create(
            id: 0,
            name: request.Name,
            type: request.Type,
            planId: request.PlanId,
            durationMonths: request.DurationMonths,
            startsAtUtc: request.StartsAtUtc,
            endsAtUtc: request.EndsAtUtc,
            priority: request.Priority,
            code: request.Code,
            percentage: request.Percentage,
            fixedAmount: request.FixedAmount,
            promotionalPrice: request.PromotionalPrice,
            chargedMonths: request.ChargedMonths);

        if (!promotionResult.IsSuccess)
            return promotionResult.Errors!;

        dbContext.Promotions.Add(promotionResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Promotion.Create",
            entityType: nameof(Promotion),
            entityId: promotionResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                promotionResult.Value.Name,
                promotionResult.Value.Code,
                Type = promotionResult.Value.Type.ToString(),
                Status = promotionResult.Value.Status.ToString(),
                promotionResult.Value.PlanId,
                promotionResult.Value.DurationMonths,
                promotionResult.Value.StartsAtUtc,
                promotionResult.Value.EndsAtUtc,
                promotionResult.Value.Priority,
                promotionResult.Value.Percentage,
                promotionResult.Value.FixedAmount,
                promotionResult.Value.PromotionalPrice,
                promotionResult.Value.ChargedMonths
            }),
            cancellationToken: cancellationToken);

        return promotionResult.Value.Id;
    }
}
