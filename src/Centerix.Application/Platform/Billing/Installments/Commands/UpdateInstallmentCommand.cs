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

        // Only Pending installments can be updated.
        // This prevents mutation of settled or overdue financial history.
        if (installment.Status != InstallmentStatus.Pending)
            return InstallmentErrors.CannotUpdateNonPending;

        // Validate covered period within contract
        var contract = await dbContext.Contracts
            .FirstOrDefaultAsync(c => c.Id == installment.ContractId, cancellationToken);

        if (contract is null)
            return InstallmentErrors.ContractNotFound;

        if (request.CoveredPeriodStartUtc < contract.EffectiveAtUtc)
            return InstallmentErrors.CoveredPeriodStartBeforeContract;

        if (request.CoveredPeriodEndUtc > contract.EndsAtUtc)
            return InstallmentErrors.CoveredPeriodExceedsContract;

        // Validate total obligation invariant after the update
        var otherInstallmentsTotal = await dbContext.Installments
            .Where(i => i.ContractId == installment.ContractId
                && i.TenantId == tenantId
                && i.Id != installment.Id)
            .SumAsync(i => i.Amount, cancellationToken);

        if (otherInstallmentsTotal + request.Amount > contract.ContractedAmount)
            return InstallmentErrors.ScheduleExceedsContractObligation(
                otherInstallmentsTotal + request.Amount, contract.ContractedAmount);

        // Validate period integrity: no overlap with other installments
        var otherInstallments = await dbContext.Installments
            .Where(i => i.ContractId == installment.ContractId
                && i.TenantId == tenantId
                && i.Id != installment.Id)
            .Select(i => new { i.Id, i.CoveredPeriodStartUtc, i.CoveredPeriodEndUtc })
            .ToListAsync(cancellationToken);

        foreach (var other in otherInstallments)
        {
            if (request.CoveredPeriodStartUtc < other.CoveredPeriodEndUtc
                && request.CoveredPeriodEndUtc > other.CoveredPeriodStartUtc)
            {
                return InstallmentErrors.OverlappingPeriod(other.Id);
            }
        }

        // Apply the update (domain-level validation for Pending status happens inside entity)
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
