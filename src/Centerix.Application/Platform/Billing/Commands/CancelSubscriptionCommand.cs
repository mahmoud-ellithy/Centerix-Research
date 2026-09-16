namespace Centerix.Application.Platform.Billing.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
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
/// Authoritative cancellation command. This is the SINGLE business workflow for
/// cancelling a subscription. It orchestrates:
/// 1. Platform authorization (IPlatformAdminGuard)
/// 2. Subscription state validation (domain rules)
/// 3. Refund calculation using historical contract pricing (IRefundCalculationService)
/// 4. Refund creation when refundable (Refund.Create)
/// 5. Future unpaid installment cancellation
/// 6. Subscription status update (domain Cancel)
/// 7. Tenant lifecycle sync (SetValidUpTo + TenantRegistrySync)
/// 8. Audit trail
/// </summary>
/// <remarks>
/// Legacy command (Centerix.Application.Platform.Commands.CancelSubscriptionCommand)
/// delegates to this command for paid contract scenarios. This command MUST NOT be bypassed
/// for subscriptions with an associated Contract — financial rules must always apply.
/// </remarks>
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
    IPlatformAdminGuard platformAdminGuard,
    ITenantRegistrySync tenantRegistrySync,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IRequestHandler<CancelSubscriptionCommand, Result<CancellationResult>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<CancellationResult>> Handle(
        CancelSubscriptionCommand request,
        CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;

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
        var subscription = await dbContext.TenantPlans
            .FirstOrDefaultAsync(s => s.Id == request.SubscriptionId, cancellationToken);

        if (subscription is null)
        {
            return Error.NotFound("Subscription.NotFound", "Subscription was not found.");
        }

        if (subscription.Status == SubscriptionStatus.Cancelled)
        {
            return new CancellationResult
            {
                SubscriptionId = subscription.Id,
                IsCancelled = false,
                Calculation = null,
                RefundId = null,
                RefundAmount = 0,
                CustomerOutstandingAmount = 0
            };
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (request.CancellationDateUtc > now)
        {
            return TenantPlanErrors.CancellationDateInFuture;
        }

        if (request.CancellationDateUtc < subscription.StartsAtUtc)
        {
            return TenantPlanErrors.CancellationDateBeforeSubscriptionStart;
        }

        var oldValue = AuditPayload.Serialize(new
        {
            Status = subscription.Status.ToString(),
            subscription.ContractId
        });

        Contract? contract = null;
        if (subscription.ContractId.HasValue)
        {
            contract = await dbContext.Contracts
                .Include(c => c.PricingTiers)
                .Include(c => c.Benefits)
                .FirstOrDefaultAsync(c => c.Id == subscription.ContractId.Value, cancellationToken);

            if (contract is not null && contract.TenantId != subscription.TenantId)
            {
                return Error.Forbidden("Cancellation.CrossTenantContract",
                    "Contract does not belong to the same tenant as the subscription.");
            }
        }

        RefundCalculationResult? calculation = null;
        Guid? refundId = null;

        if (contract is not null)
        {
            var existingRefund = await dbContext.Refunds
                .FirstOrDefaultAsync(r => r.SubscriptionId == subscription.Id, cancellationToken);

            if (existingRefund is not null)
            {
                refundId = existingRefund.Id;
            }
            else
            {
                var payments = await dbContext.Payments
                    .Include(p => p.Allocations)
                        .ThenInclude(a => a.Invoice)
                    .Where(p => p.TenantId == contract.TenantId
                        && p.Status == PaymentStatus.Completed
                        && p.Allocations.Any(a => a.Status == PaymentAllocationStatus.Active
                            && a.Invoice.ContractId == contract.Id))
                    .ToListAsync(cancellationToken);

                calculation = calculationService.Calculate(
                    contract,
                    request.CancellationDateUtc,
                    payments,
                    contract.Benefits);

                if (calculation.IsRefundDue)
                {
                    var refundNumber = $"REF-{now:yyyyMMdd}-{Guid.NewGuid().ToString()[..8]}";

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
        }

        if (subscription.ContractId.HasValue)
        {
            var futureInstallments = await dbContext.Installments
                .Include(i => i.PaymentAllocations)
                .Where(i => i.SubscriptionId == subscription.Id
                    && i.ContractId == subscription.ContractId.Value
                    && i.Status != InstallmentStatus.Paid
                    && i.Status != InstallmentStatus.Cancelled)
                .ToListAsync(cancellationToken);

            foreach (var installment in futureInstallments)
            {
                if (installment.PaymentAllocations.Any(a => a.Status == PaymentAllocationStatus.Active))
                    continue;

                var cancelResult = installment.Cancel(now);
                if (!cancelResult.IsSuccess)
                {
                    return cancelResult.Errors!;
                }
            }
        }

        var cancelSubscriptionResult = subscription.Cancel(request.CancellationDateUtc);
        if (!cancelSubscriptionResult.IsSuccess)
        {
            return cancelSubscriptionResult.Errors!;
        }

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

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id.ToString() == subscription.TenantId, cancellationToken);

        if (tenant is not null)
        {
            tenant.SetValidUpTo(now);
            await tenantRegistrySync.SyncLifecycleAsync(tenant, cancellationToken);
        }

        await auditWriter.WriteAsync(
            action: "Subscription.Cancel",
            entityType: nameof(TenantPlan),
            entityId: subscription.Id.ToString(),
            oldValue: oldValue,
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
