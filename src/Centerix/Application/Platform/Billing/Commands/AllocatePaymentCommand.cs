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

        // Load the payment with UPDLOCK to prevent concurrent allocations from exceeding
        // the payment amount. The lock is held until the transaction completes.
        Payment payment;
        if (dbContext.IsRelational)
        {
            // Use raw SQL with UPDLOCK to acquire an update lock on the payment row.
            // This prevents other concurrent transactions from modifying this payment
            // until we commit, guaranteeing total active allocations <= Payment.Amount.
            var paymentId = request.PaymentId;
            var payments = await dbContext.Payments
                .FromSqlRaw("SELECT * FROM Platform.Payments WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE PaymentId = {0}", paymentId)
                .Include(p => p.Allocations)
                .ToListAsync(cancellationToken);
            payment = payments.FirstOrDefault();
        }
        else
        {
            payment = await dbContext.Payments
                .Include(p => p.Allocations)
                .FirstOrDefaultAsync(p => p.Id == request.PaymentId, cancellationToken);
        }

        if (payment is null)
        {
            return PaymentErrors.NotFound;
        }

        // Payment must be completed before allocation
        if (!payment.IsCompleted)
        {
            return PaymentErrors.CannotAllocatePendingPayment;
        }

        // Load the invoice with UPDLOCK to prevent concurrent allocations from exceeding
        // the invoice total amount.
        Invoice invoice;
        if (dbContext.IsRelational)
        {
            var invoiceId = request.InvoiceId;
            var invoices = await dbContext.Invoices
                .FromSqlRaw("SELECT * FROM Platform.Invoices WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE InvoiceId = {0}", invoiceId)
                .Include(i => i.PaymentAllocations)
                .ToListAsync(cancellationToken);
            invoice = invoices.FirstOrDefault();
        }
        else
        {
            invoice = await dbContext.Invoices
                .Include(i => i.PaymentAllocations)
                .FirstOrDefaultAsync(i => i.Id == request.InvoiceId, cancellationToken);
        }

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

        // IDEMPOTENCY CHECK: If an active allocation already exists for this exact
        // payment+invoice+amount combination, do not create a duplicate. This handles
        // retry scenarios where the same logical allocation is submitted multiple times.
        // The unique index on (TenantId, PaymentId, InvoiceId, AllocatedAmount) with
        // filter on Active status is the final guarantee at the database level.
        var existingAllocation = await dbContext.PaymentAllocations
            .Where(a => a.PaymentId == request.PaymentId
                && a.InvoiceId == request.InvoiceId
                && a.AllocatedAmount == request.AllocatedAmount
                && a.Status == PaymentAllocationStatus.Active
                && a.TenantId == payment.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingAllocation is not null)
        {
            // Allocation already exists — return success without creating duplicates.
            // This is idempotent: retry does not create duplicate financial effects.
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return Result.Updated;
        }

        // Compute the current ledger balance for this tenant by reconstructing from
        // immutable movements (SUM of all entries). This is the authoritative balance
        // and guarantees correctness under concurrent operations.
        var previousBalance = await dbContext.CustomerLedgerEntries
            .Where(e => e.TenantId == payment.TenantId)
            .SumAsync(e => e.IsDebit ? e.Amount : -e.Amount, cancellationToken);

        // Create the allocation record
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
