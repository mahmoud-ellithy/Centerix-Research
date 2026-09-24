namespace Centerix.Application.Platform.Billing.Installments.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Contracts;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public record InstallmentScheduleItem(
    int SequenceNumber,
    DateTime DueDateUtc,
    DateTime CoveredPeriodStartUtc,
    DateTime CoveredPeriodEndUtc,
    decimal Amount);

public record CreateInstallmentScheduleCommand(
    Guid ContractId,
    Guid SubscriptionId,
    List<InstallmentScheduleItem> Installments) : IRequest<Result<List<Guid>>>;

public class CreateInstallmentScheduleHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateInstallmentScheduleCommand, Result<List<Guid>>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<List<Guid>>> Handle(
        CreateInstallmentScheduleCommand request,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= MaxDeadlockRetries; attempt++)
        {
            // Task 19 — hygiene: clear tracked entities on each retry so a rolled-back
            // attempt's Added installments do not leak into the next attempt's save
            // (which would either duplicate-insert or be silently dropped by the next
            // RollbackAsync, wasting a retry). Mirrors AllocatePaymentCommand /
            // ExecuteRefundCommand pattern.
            if (dbContext is DbContext dbc)
            {
                dbc.ChangeTracker.Clear();
            }

            var result = await TryHandleAsync(request, cancellationToken);

            if (result.IsSuccess || !IsRetryableError(result))
                return result;

            if (attempt < MaxDeadlockRetries)
            {
                var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken);
            }
        }

        return Error.Conflict("InstallmentSchedule.ConcurrencyConflict",
            "Schedule creation conflicted with another concurrent request. Please retry.");
    }

    private static bool IsRetryableError(Result<List<Guid>> result)
    {
        return result.Errors?.Any(e => e.Code.Contains("ConcurrencyConflict")) ?? false;
    }

    private async Task<Result<List<Guid>>> TryHandleAsync(
        CreateInstallmentScheduleCommand request,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            return await ExecuteAsync(request, cancellationToken, transaction);
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("InstallmentSchedule.ConcurrencyConflict",
                "Schedule creation conflicted with another concurrent request. Please retry.");
        }
    }

    private async Task<Result<List<Guid>>> ExecuteAsync(
        CreateInstallmentScheduleCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return InstallmentErrors.TenantIdRequired;

        if (request.Installments is null || request.Installments.Count == 0)
            return Error.Validation("InstallmentSchedule.Items_Required", "At least one installment is required.");

        var contract = await dbContext.Contracts
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
            return InstallmentErrors.ContractNotFound;

        if (contract.TenantId != tenantId)
            return InstallmentErrors.CrossTenantAccess;

        if (contract.Status != Domain.Platform.Contracts.Enums.ContractStatus.Active)
            return InstallmentErrors.ContractNotActive;

        if (request.SubscriptionId == Guid.Empty)
            return InstallmentErrors.SubscriptionRequired;

        var subscription = await dbContext.TenantPlans
            .FirstOrDefaultAsync(s => s.Id == request.SubscriptionId, cancellationToken);

        if (subscription is null)
            return InstallmentErrors.SubscriptionNotFound;

        if (subscription.TenantId != tenantId)
            return InstallmentErrors.SubscriptionBelongsToDifferentTenant;

        if (subscription.ContractId != request.ContractId)
            return InstallmentErrors.SubscriptionBelongsToDifferentContract;

        // Validate currency matches contract
        var currencyCode = contract.CurrencyCode;

        // Validate no existing installments for this contract
        var existingCount = await dbContext.Installments
            .CountAsync(i => i.ContractId == request.ContractId && i.TenantId == tenantId, cancellationToken);

        if (existingCount > 0)
            return InstallmentErrors.ScheduleAlreadyComplete;

        // Sort by sequence number for validation
        var sorted = request.Installments.OrderBy(i => i.SequenceNumber).ToList();

        // Validate sequence numbers are positive and unique
        var sequenceNumbers = sorted.Select(i => i.SequenceNumber).ToList();
        if (sequenceNumbers.Any(s => s <= 0))
            return InstallmentErrors.SequenceNumberMustBePositive;

        if (sequenceNumbers.Distinct().Count() != sequenceNumbers.Count)
            return Error.Validation("InstallmentSchedule.DuplicateSequence", "Sequence numbers must be unique.");

        // Validate covered periods: no overlap, no gap, within contract duration
        for (int idx = 0; idx < sorted.Count; idx++)
        {
            var item = sorted[idx];

            if (item.CoveredPeriodEndUtc <= item.CoveredPeriodStartUtc)
                return InstallmentErrors.CoveredPeriodInvalid;

            if (item.CoveredPeriodStartUtc < contract.EffectiveAtUtc)
                return InstallmentErrors.CoveredPeriodStartBeforeContract;

            if (item.CoveredPeriodEndUtc > contract.EndsAtUtc)
                return InstallmentErrors.CoveredPeriodExceedsContract;

            if (item.Amount <= 0)
                return InstallmentErrors.AmountMustBePositive;

            // Check contiguity (no gap) — previous end == current start
            if (idx > 0)
            {
                if (item.CoveredPeriodStartUtc != sorted[idx - 1].CoveredPeriodEndUtc)
                    return InstallmentErrors.GapInSchedule;
            }
        }

        // Validate total amount matches contract
        var totalAmount = sorted.Sum(i => i.Amount);
        if (totalAmount != contract.ContractedAmount)
            return InstallmentErrors.TotalScheduleAmountMismatch(contract.ContractedAmount, totalAmount);

        var createdIds = new List<Guid>();

        foreach (var item in sorted)
        {
            var id = Guid.NewGuid();
            var result = Installment.Create(
                id,
                request.ContractId,
                item.SequenceNumber,
                item.DueDateUtc,
                item.CoveredPeriodStartUtc,
                item.CoveredPeriodEndUtc,
                item.Amount,
                currencyCode,
                subscriptionId: request.SubscriptionId);

            if (!result.IsSuccess)
                return result.Errors!;

            dbContext.Installments.Add(result.Value);
            createdIds.Add(id);
        }

        dbContext.StampAddedTenantIds(tenantId);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("InstallmentSchedule.ConcurrencyConflict",
                "Schedule creation conflicted with another concurrent request. Please retry.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("InstallmentSchedule.ConcurrencyConflict",
                "Schedule creation conflicted with another concurrent request. Please retry.");
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("InstallmentSchedule.ConcurrencyConflict",
                "Schedule creation conflicted with another concurrent request. Please retry.");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Installment.Schedule.Create",
            entityType: nameof(Installment),
            entityId: request.ContractId.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                request.ContractId,
                InstallmentCount = createdIds.Count,
                TotalAmount = totalAmount
            }),
            cancellationToken: cancellationToken);

        return createdIds;
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
            return sqlEx.Number == 2601 || sqlEx.Number == 2627;
        return false;
    }

    private static bool IsDeadlockException(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is SqlException sqlEx && sqlEx.Number == 1205)
                return true;
            current = current.InnerException;
        }
        return false;
    }
}
