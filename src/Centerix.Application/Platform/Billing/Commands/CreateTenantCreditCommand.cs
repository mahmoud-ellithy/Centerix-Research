namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;

using MediatR;

public record CreateTenantCreditCommand(
    decimal Amount,
    byte SourceType,
    Guid? SourceId) : IRequest<Result<Created>>;

public class CreateTenantCreditHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateTenantCreditCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateTenantCreditCommand request,
        CancellationToken cancellationToken)
    {
        var creditResult = TenantCredit.Create(
            Guid.NewGuid(),
            request.Amount,
            (CreditSourceType)request.SourceType,
            request.SourceId);

        if (!creditResult.IsSuccess)
        {
            return creditResult.Errors!;
        }

        dbContext.TenantCredits.Add(creditResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TenantCredit.Create",
            entityType: nameof(TenantCredit),
            entityId: creditResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                creditResult.Value.Amount,
                SourceType = creditResult.Value.SourceType.ToString(),
                creditResult.Value.SourceId,
                Status = creditResult.Value.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
