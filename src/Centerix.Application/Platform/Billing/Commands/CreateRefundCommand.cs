namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Contracts;

using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to create a refund request.
/// The refund amount is derived from the calculation service, not caller-supplied.
/// </summary>
public record CreateRefundCommand(
    string RefundNumber,
    Guid ContractId,
    Guid? SubscriptionId,
    Guid? InvoiceId,
    string CurrencyCode,
    string Reason) : IRequest<Result<Guid>>;

public class CreateRefundHandler(
    IAppDbContext dbContext,
    IRefundCalculationService calculationService,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter) : IRequestHandler<CreateRefundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateRefundCommand request,
        CancellationToken cancellationToken)
    {
        // Verify the contract exists
        var contract = await dbContext.Contracts
            .Include(c => c.PricingTiers)
            .Include(c => c.Benefits)
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
        {
            return RefundErrors.ContractNotFound;
        }

        // Load payments traced from this contract via Invoice → PaymentAllocation → Payment (tenant-scoped)
        var payments = await dbContext.Payments
            .Include(p => p.Allocations)
            .Where(p => p.TenantId == contract.TenantId
                && p.Status == PaymentStatus.Completed
                && p.Allocations.Any(a => a.Status == PaymentAllocationStatus.Active
                    && a.Invoice.ContractId == request.ContractId))
            .ToListAsync(cancellationToken);

        // Derive the refund amount from the calculation service
        var calculation = calculationService.Calculate(
            contract,
            DateTime.UtcNow,
            payments,
            contract.Benefits);

        // If no refund is due (RefundAmount = 0, meaning customer owes money),
        // return an error with the CustomerOutstandingAmount preserved.
        // The calculation result itself is the source of truth for negative outcomes (Task #7).
        if (!calculation.IsRefundDue)
        {
            return RefundErrors.NoRefundDue(calculation.CustomerOutstandingAmount);
        }

        var refundResult = Refund.Create(
            Guid.NewGuid(),
            request.RefundNumber,
            request.ContractId,
            request.SubscriptionId,
            request.InvoiceId,
            calculation.RefundAmount,
            request.CurrencyCode,
            request.Reason,
            currentUserService.UserId!,
            DateTime.UtcNow);

        if (!refundResult.IsSuccess)
        {
            return refundResult.Errors!;
        }

        var refund = refundResult.Value;

        dbContext.Refunds.Add(refund);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Refund.Create",
            entityType: nameof(Refund),
            entityId: refund.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                refund.RefundNumber,
                refund.ContractId,
                refund.SubscriptionId,
                refund.InvoiceId,
                refund.Amount,
                refund.CurrencyCode,
                refund.Reason,
                CustomerOutstandingAmount = calculation.CustomerOutstandingAmount,
                Status = refund.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return refund.Id;
    }
}
