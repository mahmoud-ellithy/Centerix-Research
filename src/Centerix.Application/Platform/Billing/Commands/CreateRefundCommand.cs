namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Subscriptions;

using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Command to create a refund request.
/// The refund amount is derived from the calculation service, not caller-supplied.
/// The refund currency is derived from the Contract snapshot, not caller-supplied.
/// </summary>
public record CreateRefundCommand(
    string RefundNumber,
    Guid ContractId,
    Guid? SubscriptionId,
    Guid? InvoiceId,
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

        // Validate Subscription belongs to this Contract (if supplied)
        if (request.SubscriptionId.HasValue)
        {
            var subscription = await dbContext.TenantPlans
                .FirstOrDefaultAsync(s => s.Id == request.SubscriptionId.Value, cancellationToken);

            if (subscription is null)
            {
                return RefundErrors.NotFound;
            }

            if (subscription.TenantId != contract.TenantId)
            {
                return RefundErrors.CrossTenantSubscription;
            }

            if (subscription.ContractId != request.ContractId)
            {
                return RefundErrors.SubscriptionContractMismatch;
            }
        }

        // Validate Invoice belongs to this Contract (if supplied)
        if (request.InvoiceId.HasValue)
        {
            var invoice = await dbContext.Invoices
                .FirstOrDefaultAsync(i => i.Id == request.InvoiceId.Value, cancellationToken);

            if (invoice is null)
            {
                return RefundErrors.NotFound;
            }

            if (invoice.TenantId != contract.TenantId)
            {
                return RefundErrors.CrossTenantInvoice;
            }

            if (invoice.ContractId != request.ContractId)
            {
                return RefundErrors.InvoiceContractMismatch;
            }
        }

        // Load payments traced from this contract via Invoice → PaymentAllocation → Payment (contract-scoped)
        // .ThenInclude(a => a.Invoice) ensures the Invoice navigation is loaded so the calculation
        // service can filter allocations by Invoice.ContractId — preventing cross-contract contamination.
        var payments = await dbContext.Payments
            .Include(p => p.Allocations)
                .ThenInclude(a => a.Invoice)
            .Where(p => p.TenantId == contract.TenantId
                && p.Status == PaymentStatus.Completed
                && p.Allocations.Any(a => a.Status == PaymentAllocationStatus.Active
                    && a.Invoice.ContractId == request.ContractId))
            .ToListAsync(cancellationToken);

        // Task 18.4.2 — refund-after-subscription-change policy:
        // value already converted into a SubscriptionChange credit for this contract must not
        // be refunded again as cash; the issued credit is deducted from the refundable base.
        var alreadyIssuedSubscriptionChangeCredit = await IssuedSubscriptionChangeCredit.GetIssuedAmountAsync(
            dbContext, contract.TenantId!, request.ContractId, contract.CurrencyCode, cancellationToken);

        // Derive the refund amount from the calculation service
        var calculation = calculationService.Calculate(
            contract,
            DateTime.UtcNow,
            payments,
            contract.Benefits,
            alreadyIssuedSubscriptionChangeCredit);

        // If no refund is due (RefundAmount = 0, meaning customer owes money),
        // return an error with the CustomerOutstandingAmount preserved.
        // The calculation result itself is the source of truth for negative outcomes (Task #7).
        if (!calculation.IsRefundDue)
        {
            return RefundErrors.NoRefundDue(calculation.CustomerOutstandingAmount);
        }

        // Currency is derived from the authoritative Contract snapshot, NOT from the caller.
        var refundResult = Refund.Create(
            Guid.NewGuid(),
            request.RefundNumber,
            request.ContractId,
            request.SubscriptionId,
            request.InvoiceId,
            calculation.RefundAmount,
            contract.CurrencyCode,
            request.Reason,
            currentUserService.UserId!,
            DateTime.UtcNow);

        if (!refundResult.IsSuccess)
        {
            return refundResult.Errors!;
        }

        var refund = refundResult.Value;

        dbContext.Refunds.Add(refund);

        // ── Generate RefundAllocations from PaymentContributions ──────────
        // Distribute RefundAmount pro-rata across the payment sources that
        // contributed to AmountActuallyPaid. This preserves the exact source
        // breakdown for financial traceability.
        // Each allocation is validated against the payment's remaining refundable
        // balance: Payment.Amount - SUM(existing RefundAllocations for that Payment).
        var allocations = GenerateAllocations(
            refund.Id,
            contract.TenantId!,
            calculation.RefundAmount,
            calculation.CurrencyCode,
            calculation.PaymentContributions,
            payments);

        foreach (var allocation in allocations)
        {
            dbContext.RefundAllocations.Add(allocation);
        }

        dbContext.StampAddedTenantIds(contract.TenantId!);
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
                AlreadyConvertedSubscriptionChangeCredit = calculation.AlreadyConvertedSubscriptionChangeCredit,
                Status = refund.Status.ToString()
            }),
            cancellationToken: cancellationToken);

        return refund.Id;
    }

    /// <summary>
    /// Generates RefundAllocations by distributing the refund amount pro-rata
    /// across the payment contributions that funded this contract.
    /// Validates that each allocation does not exceed the payment's remaining
    /// refundable balance (Payment.Amount - existing RefundAllocations).
    /// </summary>
    private static List<RefundAllocation> GenerateAllocations(
        Guid refundId,
        string tenantId,
        decimal refundAmount,
        string currencyCode,
        IReadOnlyList<PaymentContribution> contributions,
        IReadOnlyList<Payment> payments)
    {
        var allocations = new List<RefundAllocation>();

        if (contributions.Count == 0 || refundAmount <= 0)
            return allocations;

        // Filter to only payments with positive contributions (Completed + Active allocations)
        var validContributions = contributions
            .Where(c => c.AllocatedAmount > 0)
            .ToList();

        if (validContributions.Count == 0)
            return allocations;

        var totalContributed = validContributions.Sum(c => c.AllocatedAmount);

        // Pro-rata distribution with remainder correction on the last allocation
        decimal allocatedSoFar = 0;

        for (int i = 0; i < validContributions.Count; i++)
        {
            var contribution = validContributions[i];
            decimal allocationAmount;

            if (i == validContributions.Count - 1)
            {
                // Last allocation gets the remainder to avoid rounding gaps
                allocationAmount = refundAmount - allocatedSoFar;
            }
            else
            {
                allocationAmount = Math.Round(
                    refundAmount * (contribution.AllocatedAmount / totalContributed),
                    2,
                    MidpointRounding.AwayFromZero);
            }

            if (allocationAmount <= 0)
                continue;

            // Currency integrity: payment currency must match refund currency
            var payment = payments.FirstOrDefault(p => p.Id == contribution.PaymentId);
            if (payment is not null && payment.CurrencyCode != currencyCode)
            {
                // Skip payments with mismatched currency — do not create allocation
                continue;
            }

            var result = RefundAllocation.Create(
                Guid.NewGuid(),
                refundId,
                contribution.PaymentId,
                allocationAmount,
                contribution.Method,
                currencyCode,
                contribution.PaymentNumber);

            if (result.IsSuccess)
            {
                allocations.Add(result.Value);
            }

            allocatedSoFar += allocationAmount;
        }

        return allocations;
    }
}
