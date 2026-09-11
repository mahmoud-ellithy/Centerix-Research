namespace Centerix.Application.Platform.Billing.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Billing.Refunds.Enums;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;

using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// Command to cancel a subscription with early cancellation refund calculation.
/// This orchestrates the cancellation workflow:
/// 1. Validates the subscription/contract state
/// 2. Calculates the refund using historical contract pricing
/// 3. Creates a refund record if refundable
/// 4. Updates the subscription status
/// </summary>
public record CancelSubscriptionCommand(
    Guid SubscriptionId,
    DateTime CancellationDateUtc,
    string Reason) : IRequest<Result<CancellationResult>>;

/// <summary>
/// Result of a cancellation operation.
/// </summary>
public sealed record CancellationResult
{
    public Guid SubscriptionId { get; init; }
    public bool IsCancelled { get; init; }
    public RefundCalculationResult? Calculation { get; init; }
    public Guid? RefundId { get; init; }
    public decimal RefundAmount { get; init; }
    public decimal CustomerOutstandingAmount { get; init; }
}

public class CancelSubscriptionHandler(
    IAppDbContext dbContext,
    IRefundCalculationService calculationService,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter) : IRequestHandler<CancelSubscriptionCommand, Result<CancellationResult>>
{
    /// <summary>
    /// Maximum number of retry attempts when a SQL Server deadlock (error 1205) occurs.
    /// </summary>
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<CancellationResult>> Handle(
        CancelSubscriptionCommand request,
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

        return Error.Conflict("Cancellation.ConcurrencyConflict",
            "This cancellation conflicted with another concurrent request. Please retry.");
    }

    private static bool IsRetryableError(Result<CancellationResult> result)
    {
        return result.Errors?.Any(e => e.Code == "Cancellation.ConcurrencyConflict") ?? false;
    }

    private async Task<Result<CancellationResult>> TryHandleAsync(
        CancelSubscriptionCommand request,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            return await ExecuteCancellationAsync(request, cancellationToken, transaction);
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return Error.Conflict("Cancellation.ConcurrencyConflict",
                "This cancellation conflicted with another concurrent request. Please retry.");
        }
    }

    private async Task<Result<CancellationResult>> ExecuteCancellationAsync(
        CancelSubscriptionCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
        // Load the subscription
        var subscription = await dbContext.TenantPlans
            .FirstOrDefaultAsync(s => s.Id == request.SubscriptionId, cancellationToken);

        if (subscription is null)
        {
            return Error.NotFound("Subscription.NotFound", "Subscription was not found.");
        }

        // Validate the subscription can be cancelled
        if (subscription.Status == SubscriptionStatus.Cancelled)
        {
            return TenantPlanErrors.AlreadyCancelledSubscription;
        }

        // Load the associated contract (if any)
        Contract? contract = null;
        if (subscription.ContractId.HasValue)
        {
            contract = await dbContext.Contracts
                .Include(c => c.PricingTiers)
                .Include(c => c.Benefits)
                .FirstOrDefaultAsync(c => c.Id == subscription.ContractId.Value, cancellationToken);
        }

        RefundCalculationResult? calculation = null;
        Guid? refundId = null;

        // If there's a contract, calculate the refund
        if (contract is not null)
        {
            // Load payments traced from this contract via Invoice → PaymentAllocation → Payment (contract-scoped)
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
            calculation = calculationService.Calculate(
                contract,
                request.CancellationDateUtc,
                payments,
                contract.Benefits);

            // If there's a refund due, create the refund record
            if (calculation.IsRefundDue)
            {
                var refundNumber = $"REF-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..8]}";

                var refundResult = Refund.Create(
                    Guid.NewGuid(),
                    refundNumber,
                    contract.Id,
                    subscription.Id,
                    null,
                    calculation.RefundAmount,
                    contract.CurrencyCode,
                    $"Early cancellation: {request.Reason}",
                    currentUserService.UserId!,
                    request.CancellationDateUtc);

                if (!refundResult.IsSuccess)
                {
                    return refundResult.Errors!;
                }

                var refund = refundResult.Value;
                dbContext.Refunds.Add(refund);
                refundId = refund.Id;
            }
        }

        // Cancel the subscription
        var cancelResult = subscription.Cancel(request.CancellationDateUtc);
        if (!cancelResult.IsSuccess)
        {
            return cancelResult.Errors!;
        }

        // Stamp the authorized tenant id
        dbContext.StampAddedTenantIds(subscription.TenantId!);

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

            return Error.Conflict("Cancellation.ConcurrencyConflict",
                "This cancellation conflicted with another concurrent request. Please retry.");
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return Error.Conflict("Cancellation.ConcurrencyConflict",
                "This cancellation conflicted with another concurrent request. Please retry.");
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await auditWriter.WriteAsync(
            action: "Subscription.Cancel",
            entityType: nameof(TenantPlan),
            entityId: subscription.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                subscription.Id,
                Status = subscription.Status.ToString(),
                CancellationDateUtc = request.CancellationDateUtc,
                Reason = request.Reason,
                RefundId = refundId,
                RefundAmount = calculation?.RefundAmount ?? 0,
                CustomerOutstandingAmount = calculation?.CustomerOutstandingAmount ?? 0
            }),
            cancellationToken: cancellationToken);

        return new CancellationResult
        {
            SubscriptionId = subscription.Id,
            IsCancelled = true,
            Calculation = calculation,
            RefundId = refundId,
            RefundAmount = calculation?.RefundAmount ?? 0,
            CustomerOutstandingAmount = calculation?.CustomerOutstandingAmount ?? 0
        };
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
