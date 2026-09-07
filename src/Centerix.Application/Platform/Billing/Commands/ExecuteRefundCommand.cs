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
/// </remarks>
public record ExecuteRefundCommand(
    Guid RefundId) : IRequest<Result<Updated>>;

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

        // Mark as processing (skip if already Processing, e.g., from Pending → Processing → Completed)
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

        // Create the refund settlement ledger entry
        var ledgerEntry = CustomerLedgerEntry.CreateRefundSettlement(
            Guid.NewGuid(),
            refund.Id,
            refund.Amount,
            refund.CurrencyCode,
            previousBalance,
            DateTime.UtcNow);

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
            // RowVersion conflict: another concurrent transaction modified this refund.
            // This is retryable — the caller's retry loop (via IsRetryableError) will
            // re-read the refund and find it either Completed or still Pending/Approved.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return RefundErrors.ExecutionConcurrencyConflict;
        }
        catch (DbUpdateException ex) when (IsDuplicateRefundNumberException(ex))
        {
            // Duplicate key on the unique constraint UX_Refunds_TenantId_RefundNumber.
            // This means a concurrent transaction already created a refund with the same
            // RefundNumber for this tenant. Re-read to check if it was for this same refund
            // (idempotent retry) or a genuine conflict.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            var existing = await dbContext.Refunds
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

            if (existing?.Status == RefundStatus.Completed)
            {
                // Refund was already executed by the concurrent transaction — idempotent success.
                return Result.Updated;
            }

            // Genuine RefundNumber collision — not retryable.
            return RefundErrors.DuplicateRefundNumber;
        }
        catch (DbUpdateException ex) when (IsDuplicateLedgerKeyException(ex))
        {
            // Duplicate key on CustomerLedgerEntry unique filter UX_CustomerLedgerEntries_SettlementByAllocation.
            // This means a concurrent transaction already created the same settlement.
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
                ExecutedAtUtc = refund.ExecutedAtUtc
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
