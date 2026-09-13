namespace Centerix.Application.Platform.Billing.Installments.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record UpdateInstallmentCommand(
    Guid Id,
    DateTime DueDateUtc,
    DateTime CoveredPeriodStartUtc,
    DateTime CoveredPeriodEndUtc,
    decimal Amount) : IRequest<Result<Updated>>;

public class UpdateInstallmentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdateInstallmentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        UpdateInstallmentCommand request,
        CancellationToken cancellationToken)
    {
        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return InstallmentErrors.TenantIdRequired;

        var installment = await dbContext.Installments
            .FirstOrDefaultAsync(i => i.Id == request.Id, cancellationToken);

        if (installment is null)
            return InstallmentErrors.NotFound;

        if (installment.TenantId != tenantId)
            return InstallmentErrors.CrossTenantAccess;

        // Validate covered period within contract
        var contract = await dbContext.Contracts
            .FirstOrDefaultAsync(c => c.Id == installment.ContractId, cancellationToken);

        if (contract is null)
            return InstallmentErrors.ContractNotFound;

        if (request.CoveredPeriodStartUtc < contract.EffectiveAtUtc)
            return InstallmentErrors.CoveredPeriodStartBeforeContract;

        if (request.CoveredPeriodEndUtc > contract.EndsAtUtc)
            return InstallmentErrors.CoveredPeriodExceedsContract;

        var updateResult = installment.Update(
            request.DueDateUtc,
            request.CoveredPeriodStartUtc,
            request.CoveredPeriodEndUtc,
            request.Amount);

        if (!updateResult.IsSuccess)
            return updateResult.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Installment.Update",
            entityType: nameof(Installment),
            entityId: request.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                request.DueDateUtc,
                request.CoveredPeriodStartUtc,
                request.CoveredPeriodEndUtc,
                request.Amount
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
