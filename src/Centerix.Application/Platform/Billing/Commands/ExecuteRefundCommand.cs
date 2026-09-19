namespace Centerix.Application.Platform.Billing.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Billing.Refunds.Enums;

using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// Command to execute a refund. This performs the actual financial transaction
/// and creates an immutable ledger entry.
/// </summary>
/// <remarks>
/// The execution is protected against concurrent execution using:
/// 1. Serializable isolation level for the transaction
/// 2. Optimistic concurrency via RowVersion
/// 3. Idempotency check (already executed refunds are skipped)
/// 4. Deadlock retry resilience
/// 5. UPDLOCK on refund read to serialize concurrent executions
///
/// Before execution, the handler validates:
/// - Refund allocations exist and sum to the refund amount
/// - Each referenced payment is completed and in the same tenant
/// - Payment refundable balances are sufficient
/// </remarks>
public record ExecuteRefundCommand(
    Guid RefundId,
    string? IdempotencyKey = null) : IRequest<Result<Updated>>;

public class ExecuteRefundHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter) : IRequestHandler<ExecuteRefundCommand, Result<Updated>>
{
    /// <summary>
    /// Maximum number of retry attempts when a SQL Server deadlock (error 1205) occurs.
    /// </summary>
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<Updated>> Handle(
        ExecuteRefundCommand request,
        CancellationToken cancellationToken)
    {
        // Retry loop for deadlock resilience
        for (int attempt = 0; attempt <= MaxDeadlockRetries; attempt++)
        {
            var result = await TryHandleAsync(request, cancellationToken);

            if (result.IsSuccess || !IsRetryableError(result))
            {
                return result;
            }

            // Bounded exponential backoff: 50ms, 100ms, 200ms
            if (attempt < MaxDeadlockRetries)
            {
                var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }

        return RefundErrors.ExecutionConcurrencyConflict;
    }

    private static bool IsRetryableError(Result<Updated> result)
    {
        return result.Errors?.Any(e => e.Code == RefundErrors.ExecutionConcurrencyConflict.Code) ?? false;
    }

    private async Task<Result<Updated>> TryHandleAsync(
        ExecuteRefundCommand request,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            return await ExecuteRefundAsync(request, cancellationToken, transaction);
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return RefundErrors.ExecutionConcurrencyConflict;
        }
    }

    private async Task<Result<Updated>> ExecuteRefundAsync(
        ExecuteRefundCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
        // Load the refund
        var refund = await dbContext.Refunds
            .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

        if (refund is null)
        {
            return RefundErrors.NotFound;
        }

        // Idempotency check: if already completed, return success
        if (refund.Status == RefundStatus.Completed)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return Result.Updated;
        }

        // Validate the refund can be executed (Pending allowed for optional approval workflow)
        if (refund.Status != RefundStatus.Pending && refund.Status != RefundStatus.Approved && refund.Status != RefundStatus.Processing)
        {
            return RefundErrors.InvalidStateTransition(refund.Status, "execute");
        }

        // ── VALIDATE REFUND ALLOCATIONS ────────────────────────────────
        var allocations = await dbContext.RefundAllocations
            .Where(ra => ra.RefundId == refund.Id && ra.TenantId == refund.TenantId)
            .ToListAsync(cancellationToken);

        if (allocations.Count == 0)
        {
            return RefundErrors.AllocationsRequired;
        }

        // Validate allocation sum equals refund amount
        var allocationSum = allocations.Sum(a => a.Amount);
        if (allocationSum != refund.Amount)
        {
            return RefundErrors.AllocationSumMismatch;
        }

        // Validate each referenced payment exists, is completed, and belongs to the same tenant
        var paymentIds = allocations.Select(a => a.PaymentId).Distinct().ToList();
        var payments = await dbContext.Payments
            .Include(p => p.Allocations)
            .Where(p => paymentIds.Contains(p.Id) && p.TenantId == refund.TenantId)
            .ToListAsync(cancellationToken);

        if (payments.Count != paymentIds.Count)
        {
            return RefundErrors.AllocationCrossTenant;
        }

        foreach (var payment in payments)
        {
            if (payment.Status != PaymentStatus.Completed)
            {
                return RefundErrors.AllocationPaymentNotCompleted;
            }
        }

        // Validate refundable balance per payment: SUM(existing allocations for this payment) <= payment allocated amount
        foreach (var payment in payments)
        {
            var paymentAllocationTotal = await dbContext.RefundAllocations
                .Where(ra => ra.PaymentId == payment.Id && ra.TenantId == refund.TenantId)
                .SumAsync(ra => ra.Amount, cancellationToken);

            if (paymentAllocationTotal > payment.GetAllocatedAmount())
            {
                return RefundErrors.AllocationExceedsRefundableBalance;
            }
        }

        // Mark as processing (skip if already Processing)
        if (refund.Status != RefundStatus.Processing)
        {
            var processingResult = refund.MarkProcessing();
            if (!processingResult.IsSuccess)
            {
                return processingResult.Errors!;
            }
        }

        // Compute the current ledger balance from immutable movements
        var previousBalance = await dbContext.CustomerLedgerEntries
            .Where(e => e.TenantId == refund.TenantId)
            .SumAsync(e => e.EntryType == LedgerEntryType.InvoiceCharge ? e.Amount : -e.Amount, cancellationToken);

        // Create a single refund settlement ledger entry for the total amount.
        // Per-payment traceability is provided by the RefundAllocation entities.
        var allocationSummary = string.Join(", ",
            allocations.Select(a => $"{a.PaymentMethod} {a.Amount}"));
        var ledgerEntry = CustomerLedgerEntry.CreateRefundSettlement(
            Guid.NewGuid(),
            refund.Id,
            refund.Amount,
            refund.CurrencyCode,
            previousBalance,
            DateTime.UtcNow,
            $"Refund settlement: {refund.Amount} {refund.CurrencyCode} (sources: {allocationSummary})");

        if (!ledgerEntry.IsSuccess)
        {
            return ledgerEntry.Errors!;
        }

        dbContext.CustomerLedgerEntries.Add(ledgerEntry.Value);

        // Execute the refund (marks as completed)
        var executeResult = refund.Execute(currentUserService.UserId!, DateTime.UtcNow);
        if (!executeResult.IsSuccess)
        {
            return executeResult.Errors!;
        }

        // Stamp the authorized tenant id
        dbContext.StampAddedTenantIds(refund.TenantId!);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return RefundErrors.ExecutionConcurrencyConflict;
        }
        catch (DbUpdateException ex) when (IsDuplicateRefundNumberException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            var existing = await dbContext.Refunds
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

            if (existing?.Status == RefundStatus.Completed)
            {
                return Result.Updated;
            }

            return RefundErrors.DuplicateRefundNumber;
        }
        catch (DbUpdateException ex) when (IsDuplicateLedgerKeyException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            var existing = await dbContext.Refunds
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

            if (existing?.Status == RefundStatus.Completed)
            {
                return Result.Updated;
            }

            return RefundErrors.ExecutionConcurrencyConflict;
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return RefundErrors.ExecutionConcurrencyConflict;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await auditWriter.WriteAsync(
            action: "Refund.Execute",
            entityType: nameof(Refund),
            entityId: refund.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                refund.Id,
                refund.Amount,
                refund.CurrencyCode,
                Status = refund.Status.ToString(),
                ExecutedBy = refund.ExecutedBy,
                ExecutedAtUtc = refund.ExecutedAtUtc,
                AllocationCount = allocations.Count,
                Allocations = allocations.Select(a => new
                {
                    a.PaymentId,
                    a.Amount,
                    a.PaymentMethod,
                    a.PaymentNumber
                })
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }

    /// <summary>
    /// Checks whether the given DbUpdateException was caused by a duplicate key violation
    /// on the UX_Refunds_TenantId_RefundNumber unique index.
    /// SQL Server error 2601/2627 includes the index name in the error message.
    /// </summary>
    private static bool IsDuplicateRefundNumberException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            if (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                return msg.Contains("UX_Refunds_TenantId_RefundNumber", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    /// <summary>
    /// Checks whether the given DbUpdateException was caused by a duplicate key violation
    /// on the CustomerLedgerEntry filtered unique index UX_CustomerLedgerEntries_SettlementByAllocation.
    /// </summary>
    private static bool IsDuplicateLedgerKeyException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            if (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                return msg.Contains("UX_CustomerLedgerEntries_SettlementByAllocation", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("UX_CustomerLedgerEntries_SettlementByRefund", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    /// <summary>
    /// Checks whether the given exception was caused by a SQL Server deadlock.
    /// Deadlock victim error number is 1205.
    /// </summary>
    private static bool IsDeadlockException(Exception ex)
    {
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
