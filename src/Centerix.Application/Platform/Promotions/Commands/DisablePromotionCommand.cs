namespace Centerix.Application.Platform.Promotions.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Promotions;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record DisablePromotionCommand(int Id) : IRequest<Result<Updated>>;

public class DisablePromotionHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    IAuditWriter auditWriter) : IRequestHandler<DisablePromotionCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(DisablePromotionCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

        var promotion = await dbContext.Promotions
            .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);

        if (promotion is null)
            return PromotionErrors.NotFound(request.Id);

        var result = promotion.Deactivate();
        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Promotion.Disable",
            entityType: nameof(Promotion),
            entityId: promotion.Id.ToString(),
            newValue: AuditPayload.Serialize(new { Status = promotion.Status.ToString() }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
