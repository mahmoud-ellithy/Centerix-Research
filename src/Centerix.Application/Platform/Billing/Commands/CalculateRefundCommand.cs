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
/// Command to calculate the refund for an early cancellation.
/// This is a deterministic, side-effect-free calculation.
/// </summary>
public record CalculateRefundQuery(
    Guid ContractId,
    DateTime AsOfUtc) : IRequest<Result<RefundCalculationResult>>;

public class CalculateRefundHandler(
    IAppDbContext dbContext,
    IRefundCalculationService calculationService) : IRequestHandler<CalculateRefundQuery, Result<RefundCalculationResult>>
{
    public async Task<Result<RefundCalculationResult>> Handle(
        CalculateRefundQuery request,
        CancellationToken cancellationToken)
    {
        // Load the contract with its pricing tiers and benefits
        var contract = await dbContext.Contracts
            .Include(c => c.PricingTiers)
            .Include(c => c.Benefits)
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
        {
            return RefundErrors.ContractNotFound;
        }

        // Load invoices belonging to this contract, then trace payments through
        // the Invoice → PaymentAllocation → Payment chain (contract-scoped).
        // .ThenInclude(a => a.Invoice) ensures the Invoice navigation is loaded so the calculation
        // service can filter allocations by Invoice.ContractId — preventing cross-contract contamination.
        var payments = await dbContext.Payments
            .Include(p => p.Allocations)
                .ThenInclude(a => a.Invoice)
            .Where(p => p.TenantId == contract.TenantId
                && p.Status == PaymentStatus.Completed
                && p.Allocations.Any(a => a.Status == PaymentAllocationStatus.Active
                    && a.Invoice.ContractId == contract.Id))
            .ToListAsync(cancellationToken);

        // Perform the deterministic calculation
        var result = calculationService.Calculate(
            contract,
            request.AsOfUtc,
            payments,
            contract.Benefits);

        return result;
    }
}
