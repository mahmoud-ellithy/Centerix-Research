namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record UpdatePromotionCommand(
    int Id,
    string Name,
    PromotionType Type,
    int PlanId,
    int DurationMonths,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Priority,
    string? Code = null,
    decimal? Percentage = null,
    decimal? FixedAmount = null,
    decimal? PromotionalPrice = null,
    int? ChargedMonths = null) : IRequest<Result<Updated>>;

public class UpdatePromotionHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    IAuditWriter auditWriter) : IRequestHandler<UpdatePromotionCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(UpdatePromotionCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var promotion = await dbContext.Promotions
            .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);

        if (promotion is null)
            return PromotionErrors.NotFound(request.Id);

        var updateResult = promotion.Update(
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

        if (!updateResult.IsSuccess)
            return updateResult.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Promotion.Update",
            entityType: nameof(Promotion),
            entityId: promotion.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                promotion.Name,
                promotion.Code,
                Type = promotion.Type.ToString(),
                Status = promotion.Status.ToString(),
                promotion.PlanId,
                promotion.DurationMonths,
                promotion.StartsAtUtc,
                promotion.EndsAtUtc,
                promotion.Priority,
                promotion.Percentage,
                promotion.FixedAmount,
                promotion.PromotionalPrice,
                promotion.ChargedMonths
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
