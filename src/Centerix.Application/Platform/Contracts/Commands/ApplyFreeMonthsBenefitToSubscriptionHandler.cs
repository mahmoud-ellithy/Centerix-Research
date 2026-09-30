namespace Centerix.Application.Platform.Contracts.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public class ApplyFreeMonthsBenefitToSubscriptionHandler(
    IAppDbContext dbContext) : IRequestHandler<ApplyFreeMonthsBenefitToSubscriptionCommand, Result<ApplyFreeMonthsBenefitResult>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<ApplyFreeMonthsBenefitResult>> Handle(
        ApplyFreeMonthsBenefitToSubscriptionCommand request,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= MaxDeadlockRetries; attempt++)
        {
            var result = await TryHandleAsync(request, cancellationToken);
            if (result.IsSuccess || !IsRetryableError(result))
                return result;

            if (attempt < MaxDeadlockRetries)
            {
                var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }

        return Error.Conflict(
            "FreeMonthsBenefit.ConcurrencyConflict",
            "This application request conflicted with another concurrent request. Please retry.");
    }

    private static bool IsRetryableError(Result<ApplyFreeMonthsBenefitResult> result)
    {
        return result.Errors?.Any(e => e.Code == "FreeMonthsBenefit.ConcurrencyConflict") ?? false;
    }

    private async Task<Result<ApplyFreeMonthsBenefitResult>> TryHandleAsync(
        ApplyFreeMonthsBenefitToSubscriptionCommand request,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            return await ExecuteApplyAsync(request, cancellationToken, transaction);
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict(
                "FreeMonthsBenefit.ConcurrencyConflict",
                "This application request conflicted with another concurrent request. Please retry.");
        }
    }

    private async Task<Result<ApplyFreeMonthsBenefitResult>> ExecuteApplyAsync(
        ApplyFreeMonthsBenefitToSubscriptionCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
        // 1. Load the benefit with its contract (for tenant isolation).
        // IgnoreQueryFilters: the benefit may belong to another tenant and is hidden by the
        // global filter; we check tenant ownership explicitly below to return CrossTenant.
        var benefit = await dbContext.FreeMonthsBenefits
            .IgnoreQueryFilters()
            .Include(b => b.Contract)
            .FirstOrDefaultAsync(b => b.Id == request.BenefitId, cancellationToken);

        if (benefit is null)
            return FreeMonthsBenefitErrors.NotFound(request.BenefitId);

        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId) || benefit.Contract.TenantId != tenantId)
            return FreeMonthsBenefitErrors.CrossTenantFreeMonthsBenefit;

        // 2. Idempotent: already Applied
        if (benefit.FulfillmentStatus == FreeMonthsFulfillmentStatus.AppliedToSubscription)
        {
            // Find the subscription to return current state
            var existingSubscription = await dbContext.TenantPlans
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    tp => tp.ContractId == benefit.ContractId && tp.TenantId == tenantId,
                    cancellationToken);

            return new ApplyFreeMonthsBenefitResult
            {
                BenefitId = benefit.Id,
                AppliedAtUtc = benefit.AppliedAtUtc,
                IsAlreadyApplied = true,
                SubscriptionId = existingSubscription?.Id ?? Guid.Empty,
                EffectiveEndsAtUtc = existingSubscription?.EffectiveEndsAtUtc ?? default
            };
        }

        // 3. Must be Granted before applying
        if (benefit.FulfillmentStatus != FreeMonthsFulfillmentStatus.Granted)
            return FreeMonthsBenefitErrors.NotGranted;

        // 4. Load the active subscription for this tenant+contract
        var subscription = await dbContext.TenantPlans
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                tp => tp.ContractId == benefit.ContractId && tp.TenantId == tenantId,
                cancellationToken);

        if (subscription is null)
            return Error.NotFound(
                "FreeMonthsBenefit.SubscriptionNotFound",
                $"No active subscription found for tenant '{tenantId}' on contract '{benefit.ContractId}'");

        // 5. Apply free months to the subscription (idempotent internally via AppliedFreeMonthsBenefitIds)
        var applyResult = subscription.ApplyFreeMonthsBenefit(benefit.Id, benefit.EntitlementMonths, DateTime.UtcNow);
        if (!applyResult.IsSuccess)
            return applyResult.Errors!;

        // 6. Mark the benefit as AppliedToSubscription
        var now = DateTime.UtcNow;
        var benefitApplyResult = benefit.MarkAppliedToSubscription(now);
        if (!benefitApplyResult.IsSuccess)
            return benefitApplyResult.Errors!;

        dbContext.StampAddedTenantIds(tenantId);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict(
                "FreeMonthsBenefit.ConcurrencyConflict",
                "This application request conflicted with another concurrent request. Please retry.");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return new ApplyFreeMonthsBenefitResult
        {
            BenefitId = benefit.Id,
            AppliedAtUtc = benefit.AppliedAtUtc,
            IsAlreadyApplied = false,
            SubscriptionId = subscription.Id,
            EffectiveEndsAtUtc = subscription.EffectiveEndsAtUtc
        };
    }

    private static bool IsDeadlockException(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is Microsoft.Data.SqlClient.SqlException sqlEx && sqlEx.Number == 1205)
                return true;
            current = current.InnerException;
        }
        return false;
    }
}
