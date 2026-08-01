namespace Centerix.Application.Platform.Referrals.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Referrals;

using MediatR;

public record CreateTenantReferralCodeCommand(string Code) : IRequest<Result<Created>>;

public class CreateTenantReferralCodeHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateTenantReferralCodeCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(CreateTenantReferralCodeCommand request, CancellationToken cancellationToken)
    {
        var result = TenantReferralCode.Create(
            Guid.NewGuid(),
            request.Code);

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        dbContext.TenantReferralCodes.Add(result.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantReferralCode.Create",
            entityType: nameof(TenantReferralCode),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.Code,
                result.Value.TimesUsed,
                result.Value.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
