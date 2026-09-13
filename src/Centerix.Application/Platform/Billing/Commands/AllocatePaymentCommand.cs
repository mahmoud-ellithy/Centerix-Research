namespace Centerix.Application.Platform.Billing.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;

using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public record AllocatePaymentCommand(Guid PaymentId, Guid InvoiceId, decimal AllocatedAmount, Guid? InstallmentId = null) : IRequest<Result<Updated>>;

public class AllocatePaymentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<AllocatePaymentCommand, Result<Updated>>
{
    /// <summary>
    /// Maximum number of retry attempts when a SQL Server deadlock (error 1205) occurs.
    /// Deadlocks can happen under Serializable isolation when concurrent transactions
    /// acquire range locks on the same resources. Bounded retry ensures the operation
    /// eventually succeeds without risking infinite loops.
    /// </summary>
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<Updated>> Handle(
        AllocatePaymentCommand request,
        CancellationToken cancellationToken)
    {
        // Retry loop for deadlock resilience. Under Serializable isolation, concurrent
        // transactions may deadlock on range locks. When SQL Server chooses this transaction
        // as the deadlock victim (error 1205), we retry with bounded exponential backoff.
        for (int attempt = 0; attempt <= MaxDeadlockRetries; attempt++)
        {
            var result = await TryHandleAsync(request, cancellationToken);

            // If the result is a deadlock error, retry after a short delay.
            // Deadlocks are transient and expected under high concurrency.
            if (result.IsSuccess || !IsRetryableError(result))
            {
                return result;
            }

            // Bounded exponential backoff: 50ms, 100ms, 200ms
            // This gives the other transaction time to complete before we retry.
            if (attempt < MaxDeadlockRetries)
            {
                var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }

        // All retries exhausted — return the last deadlock error.
        // The caller can retry at a higher level if needed.
        return PaymentErrors.AllocationConcurrencyConflict;
    }

    /// <summary>
    /// Determines whether a failed result should be retried.
    /// Only concurrency conflicts and deadlocks are retryable.
    /// </summary>
    private static bool IsRetryableError(Result<Updated> result)
    {
        return result.Errors?.Any(e => e.Code == PaymentErrors.AllocationConcurrencyConflict.Code) ?? false;
    }

    private async Task<Result<Updated>> TryHandleAsync(
        AllocatePaymentCommand request,
        CancellationToken cancellationToken)
    {
        // Transactions are only used on relational providers (SQL Server). The EF InMemory
        // provider does not support transactions, so we skip them there — the business-logic
        // invariants are still validated. On SQL Server, the transaction guarantees that the
        // allocation, invoice status update, and ledger settlement commit atomically.
        //
        // We use Serializable isolation level to prevent phantom reads and ensure that
        // concurrent allocations against the same Payment or Invoice are properly serialized.
        // This is the strongest isolation level and guarantees that:
        // 1. Concurrent reads of the same rows are blocked until the first transaction commits
        // 2. Range locks prevent phantom inserts that could violate financial invariants
        // 3. The read → validate → insert/update sequence is atomic
        //
        // Serializable isolation automatically applies range locks that prevent the race condition
        // where two transactions read the same remaining amount and both proceed to allocate.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            return await ExecuteAllocationAsync(request, cancellationToken, transaction);
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            // SQL Server deadlock victim (error 1205). The transaction was chosen
            // as the deadlock victim and rolled back by SQL Server. We return a concurrency
            // conflict error so the caller can retry.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return PaymentErrors.AllocationConcurrencyConflict;
        }
    }

    private async Task<Result<Updated>> ExecuteAllocationAsync(
        AllocatePaymentCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
        // Load payment with its current allocations. The Payment entity has a RowVersion
        // property that EF Core uses for optimistic concurrency detection.
        //
        // Lock order: Payment first, then Invoice. This deterministic order prevents deadlocks
        // when multiple concurrent allocations involve the same resources.
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

        // Load invoice with its allocations.
        // This ensures concurrent allocations against the same Invoice are serialized
        // by the Serializable isolation level's range locks.
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
        // Under Serializable isolation, concurrent transactions cannot read this Payment's
        // allocations until we commit, so this check is safe from race conditions.
        var currentAllocated = payment.GetAllocatedAmount();
        if (currentAllocated + request.AllocatedAmount > payment.Amount)
        {
            return PaymentErrors.AllocationExceedsPayment;
        }

        // Check allocation doesn't exceed invoice remaining amount.
        // Under Serializable isolation, concurrent transactions cannot read this Invoice's
        // allocations until we commit, so this check is safe from race conditions.
        var invoiceRemaining = invoice.GetRemainingAmount();
        if (request.AllocatedAmount > invoiceRemaining)
        {
            return PaymentErrors.AllocationExceedsInvoiceRemaining;
        }

        // Idempotency check: if an identical allocation already exists (same payment, invoice, amount),
        // return success without creating a duplicate. This prevents retry from creating duplicate
        // financial effects while still allowing legitimate different allocations.
        // IMPORTANT: This check happens AFTER the financial invariant checks to ensure that
        // concurrent allocations with the same amount are properly validated against the invariants
        // before being treated as idempotent retries. This prevents the race condition where two
        // concurrent allocations of the same amount both succeed when only one should.
        var existingAllocation = await dbContext.PaymentAllocations
            .Where(a => a.PaymentId == request.PaymentId
                && a.InvoiceId == request.InvoiceId
                && a.AllocatedAmount == request.AllocatedAmount
                && a.Status == PaymentAllocationStatus.Active
                && a.TenantId == payment.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingAllocation is not null)
        {
            // Idempotent retry — allocation already exists, no financial effect needed.
            // We've already verified the invariants above, so this is a safe retry.
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return Result.Updated;
        }

        // If an installment is specified, validate it exists and is valid for this tenant
        Installment? installment = null;
        if (request.InstallmentId.HasValue)
        {
            installment = await dbContext.Installments
                .FirstOrDefaultAsync(i => i.Id == request.InstallmentId.Value && i.TenantId == payment.TenantId, cancellationToken);

            if (installment is null)
                return InstallmentErrors.NotFound;

            if (installment.ContractId != invoice.ContractId)
                return Error.Conflict("PaymentAllocation.InstallmentContractMismatch",
                    "Installment does not belong to the same contract as the invoice.");

            // Validate allocation doesn't exceed installment remaining
            if (request.AllocatedAmount > installment.RemainingAmount)
                return InstallmentErrors.AllocationExceedsInstallment;
        }

        // Compute the current ledger balance by reconstructing from immutable movements.
        // This is the authoritative balance, not the cached RunningBalance.
        // Note: We use EntryType directly instead of IsDebit because IsDebit is a computed
        // property that cannot be translated to SQL by EF Core.
        var previousBalance = await dbContext.CustomerLedgerEntries
            .Where(e => e.TenantId == payment.TenantId)
            .SumAsync(e => e.EntryType == LedgerEntryType.InvoiceCharge ? e.Amount : -e.Amount, cancellationToken);

        var allocationResult = PaymentAllocation.Create(
            Guid.NewGuid(),
            request.PaymentId,
            request.InvoiceId,
            request.AllocatedAmount,
            DateTime.UtcNow,
            request.InstallmentId);

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

        // If an installment is specified, apply the allocation to settle it
        if (installment is not null)
        {
            var installmentResult = installment.ApplyAllocation(allocation, DateTime.UtcNow);
            if (!installmentResult.IsSuccess)
                return installmentResult.Errors!;
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

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another concurrent request modified the same Payment or Invoice row.
            // Roll back the transaction so no partial state remains.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return PaymentErrors.AllocationConcurrencyConflict;
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            // A concurrent transaction created an identical allocation between our idempotency
            // check and our insert. This is a safe idempotent retry — the allocation already
            // exists, so we return success without creating a duplicate.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return Result.Updated;
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            // SQL Server deadlock victim (error 1205). The transaction was chosen
            // as the deadlock victim and rolled back by SQL Server. We return a concurrency
            // conflict error so the caller can retry.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return PaymentErrors.AllocationConcurrencyConflict;
        }

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

    /// <summary>
    /// Checks whether the given DbUpdateException was caused by a duplicate key violation.
    /// SQL Server error 2601 = cannot insert duplicate key row in unique index.
    /// SQL Server error 2627 = violation of UNIQUE KEY constraint.
    /// </summary>
    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            return sqlEx.Number == 2601 || sqlEx.Number == 2627;
        }
        return false;
    }

    /// <summary>
    /// Checks whether the given exception was caused by a SQL Server deadlock.
    /// Deadlock victim error number is 1205. The SqlException may be wrapped in
    /// DbUpdateException and/or InvalidOperationException by EF Core's execution strategy.
    /// </summary>
    private static bool IsDeadlockException(Exception ex)
    {
        // Walk the inner exception chain to find a SqlException with error 1205.
        var current = ex;
        while (current is not null)
        {
            if (current is SqlException sqlEx && sqlEx.Number == 1205)
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }
}
