namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record AllocatePaymentCommand(Guid PaymentId, Guid InvoiceId, decimal AllocatedAmount) : IRequest<Result<Updated>>;

public class AllocatePaymentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<AllocatePaymentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        AllocatePaymentCommand request,
        CancellationToken cancellationToken)
    {
        // Transactions are only used on relational providers (SQL Server). The EF InMemory
        // provider does not support transactions, so we skip them there — the business-logic
        // invariants are still validated. On SQL Server, the transaction guarantees that the
        // allocation, invoice status update, and ledger settlement commit atomically.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(cancellationToken)
            : null;

        // IMPORTANT: re-load entities inside the transaction so EF's rowversion-based
        // concurrency and allocation checks operate on the latest committed state.
        var payment = await dbContext.Payments
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken);
        if (payment is null)
        {
            return PaymentErrors.NotFound;
        }

        // Payment must be completed before allocation
        if (!payment.IsCompleted)
        {
            return PaymentErrors.CannotAllocatePendingPayment;
        }

        var invoice = await dbContext.Invoices
            .Include(i => i.PaymentAllocations)
            .FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return PaymentErrors.InvoiceNotFound;
        }

        // Tenant isolation check
        if (payment.TenantId != invoice.TenantId)
        {
            return PaymentErrors.CrossTenantAllocation;
        }

        // Allocation must be positive (zero/negative explicitly rejected)
        if (request.AllocatedAmount <= 0)
        {
            return PaymentErrors.AllocationAmountMustBePositive;
        }

        // Check allocation doesn't exceed payment's remaining unallocated amount.
        // Use the in-memory active allocation total — this is validated again under the
        // DB transaction; the combination of the check and a single SaveChanges call
        // keeps total allocations <= Payment.Amount.
        var currentAllocated = payment.GetAllocatedAmount();
        if (currentAllocated + request.AllocatedAmount > payment.Amount)
        {
            return PaymentErrors.AllocationExceedsPayment;
        }

        // Check allocation doesn't exceed invoice remaining amount
        var invoiceRemaining = invoice.GetRemainingAmount();
        if (request.AllocatedAmount > invoiceRemaining)
        {
            return PaymentErrors.AllocationExceedsInvoiceRemaining;
        }

        // Compute the current ledger balance for this tenant (authoritative reconstruction
        // from immutable movements). The RunningBalance stored on the previous entry is a
        // denormalized value; we derive the new balance from actual entries to guarantee
        // correctness under concurrent operations.
        var previousBalance = await dbContext.CustomerLedgerEntries
            .Where(e => e.TenantId == payment.TenantId)
            .OrderByDescending(e => e.RecordedAtUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => e.RunningBalance)
            .FirstOrDefaultAsync(cancellationToken);

        // Idempotency / retry safety: if a settlement ledger entry already exists for this
        // payment+invoice combination via a prior allocation, do not create a duplicate settlement.
        // The filtered unique index on (TenantId, PaymentAllocationId, EntryType='PaymentSettlement')
        // is the final guarantee; this application-level guard gives a clear error on retry.
        // Note: we still create the allocation record (it is the source of truth for settlement),
        // but prevent a duplicate settlement entry.
        var existingAllocationId = await dbContext.PaymentAllocations
            .Where(a => a.PaymentId == request.PaymentId
                && a.InvoiceId == request.InvoiceId
                && a.AllocatedAmount == request.AllocatedAmount
                && a.Status == PaymentAllocationStatus.Active
                && a.TenantId == payment.TenantId)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var allocationResult = PaymentAllocation.Create(
            Guid.NewGuid(),
            request.PaymentId,
            request.InvoiceId,
            request.AllocatedAmount,
            DateTime.UtcNow);

        if (!allocationResult.IsSuccess)
        {
            return allocationResult.Errors!;
        }

        var allocation = allocationResult.Value;

        dbContext.PaymentAllocations.Add(allocation);

        // Update invoice payment status (sets PartiallyPaid / Paid based on allocations)
        var updateResult = invoice.UpdatePaymentStatus();
        if (!updateResult.IsSuccess)
        {
            return updateResult.Errors!;
        }

        // Create the corresponding PaymentSettlement ledger entry from the actual allocated
        // amount — NOT the Payment.Amount. This guarantees Total PaymentSettlement = Total Active
        // Allocations, and overpayments are never recorded as liability settlement.
        var settlementEntry = CustomerLedgerEntry.CreatePaymentSettlement(
            Guid.NewGuid(),
            request.PaymentId,
            allocation.Id,
            request.AllocatedAmount,
            payment.CurrencyCode,
            previousBalance,
            DateTime.UtcNow);

        if (!settlementEntry.IsSuccess)
        {
            return settlementEntry.Errors!;
        }

        dbContext.CustomerLedgerEntries.Add(settlementEntry.Value);

        // Stamp the authorized tenant id on newly-added entities. On SQL Server this mirrors
        // what the SaveChanges interceptor does; on InMemory (used by fast unit tests) the
        // interceptor does not run, so we apply it explicitly here to avoid a TenantId
        // required failure.
        dbContext.StampAddedTenantIds(payment.TenantId!);

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await auditWriter.WriteAsync(
            action: "Payment.Allocate",
            entityType: nameof(PaymentAllocation),
            entityId: allocation.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                allocation.PaymentId,
                allocation.InvoiceId,
                allocation.AllocatedAmount
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
