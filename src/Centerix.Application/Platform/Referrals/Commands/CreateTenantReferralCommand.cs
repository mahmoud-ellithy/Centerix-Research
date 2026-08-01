namespace Centerix.Application.Platform.Referrals.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Referrals;
using Centerix.Domain.Platform.Referrals.Enums;

using MediatR;

public record CreateTenantReferralCommand(
    string ReferredTenantId,
    Guid ReferralCodeId,
    byte RewardType,
    decimal RewardValue,
    string RewardAppliedTo) : IRequest<Result<Created>>;

public class CreateTenantReferralHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter,
    ICurrentTenant currentTenant) : IRequestHandler<CreateTenantReferralCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(CreateTenantReferralCommand request, CancellationToken cancellationToken)
    {
        var referrerTenantId = currentTenant.TenantId ?? string.Empty;

        var rewardType = (ReferralRewardType)request.RewardType;

        var result = TenantReferral.Create(
            Guid.NewGuid(),
            referrerTenantId,
            request.ReferredTenantId,
            request.ReferralCodeId,
            rewardType,
            request.RewardValue);

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        dbContext.TenantReferrals.Add(result.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantReferral.Create",
            entityType: nameof(TenantReferral),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.ReferrerTenantId,
                result.Value.ReferredTenantId,
                result.Value.ReferralCodeId,
                result.Value.Status,
                result.Value.RewardType,
                result.Value.RewardValue
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
