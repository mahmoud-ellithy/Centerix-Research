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
/// Concurrency strategy:
/// 1. Serializable isolation level for the transaction
/// 2. UPDLOCK + HOLDLOCK on Payment reads to serialize concurrent refund source consumption
/// 3. Optimistic concurrency via RowVersion
/// 4. Idempotency key validation (same key = idempotent, different key = IdempotencyKeyConflict)
/// 5. Deadlock retry resilience with ChangeTracker.Clear() before each retry
///
/// Before execution, the handler validates:
/// - Refund allocations exist and sum to the refund amount
/// - Each referenced payment is completed, in the same tenant, and same currency
/// - Payment refundable balance: Payment.Amount - SUM(existing RefundAllocations for that Payment)
/// </remarks>
public record ExecuteRefundCommand(
    Guid RefundId,
    string? IdempotencyKey = null) : IRequest<Result<Updated>>;

public class ExecuteRefundHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter) : IRequestHandler<ExecuteRefundCommand, Result<Updated>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<Updated>> Handle(
        ExecuteRefundCommand request,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= MaxDeadlockRetries; attempt++)
        {
            if (dbContext is DbContext dbc)
            {
                dbc.ChangeTracker.Clear();
            }

            var result = await TryHandleAsync(request, cancellationToken);

            if (result.IsSuccess || !IsRetryableError(result))
            {
                return result;
            }

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
        var refund = await dbContext.Refunds
            .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

        if (refund is null)
        {
            return RefundErrors.NotFound;
        }

        // ── IDEMPOTENCY CHECK ───────────────────────────────────────────
        // If already completed, verify the idempotency key matches.
        if (refund.Status == RefundStatus.Completed)
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey)
                && !string.IsNullOrWhiteSpace(refund.IdempotencyKey)
                && refund.IdempotencyKey != request.IdempotencyKey)
            {
                return RefundErrors.AllocationIdempotencyKeyConflict;
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return Result.Updated;
        }

        if (refund.Status != RefundStatus.Pending
            && refund.Status != RefundStatus.Approved
            && refund.Status != RefundStatus.Processing)
        {
            return RefundErrors.InvalidStateTransition(refund.Status, "execute");
        }

        // ── IDEMPOTENCY KEY CONFLICT CHECK (before execution) ───────────
        // If this refund already has a stored idempotency key from a prior execution
        // attempt (Processing status), verify it matches.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey)
            && !string.IsNullOrWhiteSpace(refund.IdempotencyKey)
            && refund.IdempotencyKey != request.IdempotencyKey)
        {
            return RefundErrors.AllocationIdempotencyKeyConflict;
        }

        // ── VALIDATE REFUND ALLOCATIONS ────────────────────────────────
        var allocations = await dbContext.RefundAllocations
            .Where(ra => ra.RefundId == refund.Id && ra.TenantId == refund.TenantId)
            .ToListAsync(cancellationToken);

        if (allocations.Count == 0)
        {
            return RefundErrors.AllocationsRequired;
        }

        var allocationSum = allocations.Sum(a => a.Amount);
        if (allocationSum != refund.Amount)
        {
            return RefundErrors.AllocationSumMismatch;
        }

        // ── VALIDATE PAYMENTS (with UPDLOCK on SQL Server) ──────────────
        // Lock payment rows to serialize concurrent refund executions against
        // the same payment source. This prevents the TOCTOU race where two
        // concurrent refunds both read sufficient balance and both proceed.
        // The UPDLOCK is acquired via a separate raw SQL query under the
        // Serializable transaction, then the full entity is loaded via EF Core.
        var paymentIds = allocations.Select(a => a.PaymentId).Distinct().ToList();
        var payments = new List<Payment>();

        if (dbContext.IsRelational && dbContext is DbContext efDb)
        {
            foreach (var paymentId in paymentIds)
            {
                // Acquire UPDLOCK on the payment row to serialize concurrent reads
                var conn = efDb.Database.GetDbConnection();
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction!.GetDbTransaction();
                cmd.CommandText = "SELECT 1 FROM Platform.Payments WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE PaymentId = @p0 AND TenantId = @p1";
                var p0 = cmd.CreateParameter(); p0.ParameterName = "@p0"; p0.Value = paymentId;
                var p1 = cmd.CreateParameter(); p1.ParameterName = "@p1"; p1.Value = refund.TenantId!;
                cmd.Parameters.Add(p0); cmd.Parameters.Add(p1);
                if (conn.State != System.Data.ConnectionState.Open)
                    await conn.OpenAsync(cancellationToken);
                var lockResult = await cmd.ExecuteScalarAsync(cancellationToken);
                if (lockResult is null)
                {
                    return RefundErrors.AllocationCrossTenant;
                }
            }
        }

        // Load payments with allocations (under the acquired locks on SQL Server)
        payments = await dbContext.Payments
            .Include(p => p.Allocations)
            .Where(p => paymentIds.Contains(p.Id) && p.TenantId == refund.TenantId)
            .ToListAsync(cancellationToken);

        if (payments.Count != paymentIds.Count)
        {
            return RefundErrors.AllocationCrossTenant;
        }

        // ── VALIDATE EACH PAYMENT ──────────────────────────────────────
        foreach (var payment in payments)
        {
            // Payment must be completed
            if (payment.Status != PaymentStatus.Completed)
            {
                return RefundErrors.AllocationPaymentNotCompleted;
            }

            // Currency integrity: Payment.CurrencyCode must match Refund.CurrencyCode
            if (payment.CurrencyCode != refund.CurrencyCode)
            {
                return RefundErrors.CurrencyMismatch;
            }

            // Refundable balance: Payment.Amount - SUM(RefundAllocations for OTHER refunds
            // that are Processing or Completed). Pending refunds have not consumed the balance yet.
            // Under Serializable isolation + UPDLOCK on Payment, the next transaction sees the
            // updated status after the first transaction commits.
            // We load the allocations with their refund status to compute this correctly,
            // because EF Core subqueries may not translate well across entity boundaries.
            var otherAllocations = await dbContext.RefundAllocations
                .Where(ra => ra.PaymentId == payment.Id
                    && ra.TenantId == refund.TenantId
                    && ra.RefundId != refund.Id)
                .ToListAsync(cancellationToken);

            decimal existingRefundedAmount = 0;
            if (otherAllocations.Count > 0)
            {
                var otherRefundIds = otherAllocations.Select(a => a.RefundId).Distinct().ToList();
                var otherRefunds = await dbContext.Refunds
                    .Where(r => otherRefundIds.Contains(r.Id)
                        && (r.Status == RefundStatus.Processing || r.Status == RefundStatus.Completed))
                    .ToListAsync(cancellationToken);
                var settledRefundIds = otherRefunds.Select(r => r.Id).ToHashSet();
                existingRefundedAmount = otherAllocations
                    .Where(a => settledRefundIds.Contains(a.RefundId))
                    .Sum(a => a.Amount);
            }

            var refundableAmount = payment.Amount - existingRefundedAmount;
            if (refundableAmount < 0)
            {
                refundableAmount = 0;
            }

            // Find the allocation for this payment in this refund
            var allocationForPayment = allocations.First(a => a.PaymentId == payment.Id);

            // PaymentMethod authority: allocation snapshot must match the authoritative Payment.Method
            if (allocationForPayment.PaymentMethod != payment.Method.ToString())
            {
                return RefundErrors.PaymentMethodMismatch;
            }

            if (allocationForPayment.Amount > refundableAmount)
            {
                return RefundErrors.InsufficientPaymentSource;
            }
        }

        // ── PROCEED WITH EXECUTION ─────────────────────────────────────
        if (refund.Status != RefundStatus.Processing)
        {
            var processingResult = refund.MarkProcessing();
            if (!processingResult.IsSuccess)
            {
                return processingResult.Errors!;
            }
        }

        var previousBalance = await dbContext.CustomerLedgerEntries
            .Where(e => e.TenantId == refund.TenantId)
            .SumAsync(e => e.EntryType == LedgerEntryType.InvoiceCharge ? e.Amount : -e.Amount, cancellationToken);

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

        // Execute: mark completed and store the idempotency key
        var executeResult = refund.Execute(currentUserService.UserId!, DateTime.UtcNow, request.IdempotencyKey);
        if (!executeResult.IsSuccess)
        {
            return executeResult.Errors!;
        }

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

            var existing = await dbContext.Refunds
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

            if (existing?.Status == RefundStatus.Completed
                && existing.IdempotencyKey == request.IdempotencyKey)
            {
                return Result.Updated;
            }

            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey)
                && existing is not null
                && existing.IdempotencyKey == request.IdempotencyKey
                && existing.Status != RefundStatus.Completed)
            {
                return RefundErrors.AllocationIdempotencyKeyConflict;
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
        catch (DbUpdateException ex) when (IsDuplicateIdempotencyKeyException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            var existing = await dbContext.Refunds
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

            if (existing?.Status == RefundStatus.Completed
                && existing.IdempotencyKey == request.IdempotencyKey)
            {
                return Result.Updated;
            }

            return RefundErrors.AllocationIdempotencyKeyConflict;
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            var existing = await dbContext.Refunds
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RefundId, cancellationToken);

            if (existing?.Status == RefundStatus.Completed
                && existing.IdempotencyKey == request.IdempotencyKey)
            {
                return Result.Updated;
            }

            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey)
                && existing is not null
                && existing.IdempotencyKey == request.IdempotencyKey
                && existing.Status != RefundStatus.Completed)
            {
                return RefundErrors.AllocationIdempotencyKeyConflict;
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
                IdempotencyKey = refund.IdempotencyKey,
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

    private static bool IsDuplicateIdempotencyKeyException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            if (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                return msg.Contains("UX_Refunds_TenantId_IdempotencyKey", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

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
