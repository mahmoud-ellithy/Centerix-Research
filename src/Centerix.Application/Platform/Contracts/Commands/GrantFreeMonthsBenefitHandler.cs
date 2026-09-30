namespace Centerix.Application.Platform.Contracts.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public class GrantFreeMonthsBenefitHandler(
    IAppDbContext dbContext) : IRequestHandler<GrantFreeMonthsBenefitCommand, Result<GrantFreeMonthsBenefitResult>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<GrantFreeMonthsBenefitResult>> Handle(
        GrantFreeMonthsBenefitCommand request,
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
            "This grant request conflicted with another concurrent request. Please retry.");
    }

    private static bool IsRetryableError(Result<GrantFreeMonthsBenefitResult> result)
    {
        return result.Errors?.Any(e => e.Code == "FreeMonthsBenefit.ConcurrencyConflict") ?? false;
    }

    private async Task<Result<GrantFreeMonthsBenefitResult>> TryHandleAsync(
        GrantFreeMonthsBenefitCommand request,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            return await ExecuteGrantAsync(request, cancellationToken, transaction);
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict(
                "FreeMonthsBenefit.ConcurrencyConflict",
                "This grant request conflicted with another concurrent request. Please retry.");
        }
    }

    private async Task<Result<GrantFreeMonthsBenefitResult>> ExecuteGrantAsync(
        GrantFreeMonthsBenefitCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
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

        // Idempotent: already Granted
        if (benefit.FulfillmentStatus == FreeMonthsFulfillmentStatus.Granted ||
            benefit.FulfillmentStatus == FreeMonthsFulfillmentStatus.AppliedToSubscription)
        {
            return new GrantFreeMonthsBenefitResult
            {
                BenefitId = benefit.Id,
                GrantedAtUtc = benefit.GrantedAtUtc ?? DateTime.UtcNow,
                IsAlreadyGranted = true
            };
        }

        if (benefit.FulfillmentStatus != FreeMonthsFulfillmentStatus.Pending)
            return FreeMonthsBenefitErrors.InvalidFulfillmentStatus;

        if (benefit.EligibilityStatus != FreeMonthsEligibilityStatus.Eligible)
            return FreeMonthsBenefitErrors.NotEligible;

        var now = DateTime.UtcNow;
        var grantResult = benefit.Grant(now);
        if (!grantResult.IsSuccess)
            return grantResult.Errors!;

        dbContext.StampAddedTenantIds(benefit.Contract.TenantId!);

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
                "This grant request conflicted with another concurrent request. Please retry.");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return new GrantFreeMonthsBenefitResult
        {
            BenefitId = benefit.Id,
            GrantedAtUtc = benefit.GrantedAtUtc ?? now,
            IsAlreadyGranted = false
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
