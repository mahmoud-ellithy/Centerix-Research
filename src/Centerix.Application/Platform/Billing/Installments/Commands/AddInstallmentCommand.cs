namespace Centerix.Application.Platform.Billing.Installments.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public record AddInstallmentCommand(
    Guid ContractId,
    Guid SubscriptionId,
    int SequenceNumber,
    DateTime DueDateUtc,
    DateTime CoveredPeriodStartUtc,
    DateTime CoveredPeriodEndUtc,
    decimal Amount) : IRequest<Result<Guid>>;

public class AddInstallmentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<AddInstallmentCommand, Result<Guid>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<Guid>> Handle(
        AddInstallmentCommand request,
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

        return Error.Conflict("Installment.ConcurrencyConflict",
            "Request conflicted with another concurrent request. Please retry.");
    }

    private static bool IsRetryableError(Result<Guid> result)
    {
        return result.Errors?.Any(e => e.Code.Contains("ConcurrencyConflict")) ?? false;
    }

    private async Task<Result<Guid>> TryHandleAsync(
        AddInstallmentCommand request,
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

            return Error.Conflict("Installment.ConcurrencyConflict",
                "Request conflicted with another concurrent request. Please retry.");
        }
    }

    private async Task<Result<Guid>> ExecuteAsync(
        AddInstallmentCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return InstallmentErrors.TenantIdRequired;

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

        // Validate no duplicate sequence number
        var existingSequences = await dbContext.Installments
            .Where(i => i.ContractId == request.ContractId && i.TenantId == tenantId)
            .Select(i => i.SequenceNumber)
            .ToListAsync(cancellationToken);

        if (existingSequences.Contains(request.SequenceNumber))
            return InstallmentErrors.DuplicateSequenceNumber(request.SequenceNumber);

        // Validate covered period within contract
        if (request.CoveredPeriodEndUtc <= request.CoveredPeriodStartUtc)
            return InstallmentErrors.CoveredPeriodInvalid;

        if (request.CoveredPeriodStartUtc < contract.EffectiveAtUtc)
            return InstallmentErrors.CoveredPeriodStartBeforeContract;

        if (request.CoveredPeriodEndUtc > contract.EndsAtUtc)
            return InstallmentErrors.CoveredPeriodExceedsContract;

        if (request.Amount <= 0)
            return InstallmentErrors.AmountMustBePositive;

        // Validate total obligation invariant: SUM(existing + new) <= Contract.ContractedAmount
        var existingTotalAmount = await dbContext.Installments
            .Where(i => i.ContractId == request.ContractId && i.TenantId == tenantId)
            .SumAsync(i => i.Amount, cancellationToken);

        if (existingTotalAmount + request.Amount > contract.ContractedAmount)
            return InstallmentErrors.ScheduleExceedsContractObligation(
                existingTotalAmount + request.Amount, contract.ContractedAmount);

        // Validate period integrity: no overlap, no duplicate coverage with existing installments
        var existingInstallments = await dbContext.Installments
            .Where(i => i.ContractId == request.ContractId && i.TenantId == tenantId)
            .Select(i => new { i.Id, i.CoveredPeriodStartUtc, i.CoveredPeriodEndUtc })
            .ToListAsync(cancellationToken);

        foreach (var existing in existingInstallments)
        {
            // Overlap: new start < existing end AND new end > existing start
            if (request.CoveredPeriodStartUtc < existing.CoveredPeriodEndUtc
                && request.CoveredPeriodEndUtc > existing.CoveredPeriodStartUtc)
            {
                return InstallmentErrors.OverlappingPeriod(existing.Id);
            }
        }

        var id = Guid.NewGuid();
        var result = Installment.Create(
            id,
            request.ContractId,
            request.SequenceNumber,
            request.DueDateUtc,
            request.CoveredPeriodStartUtc,
            request.CoveredPeriodEndUtc,
            request.Amount,
            contract.CurrencyCode,
            subscriptionId: request.SubscriptionId);

        if (!result.IsSuccess)
            return result.Errors!;

        dbContext.Installments.Add(result.Value);

        dbContext.StampAddedTenantIds(tenantId);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("Installment.ConcurrencyConflict",
                "Request conflicted with another concurrent request. Please retry.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("Installment.ConcurrencyConflict",
                "Request conflicted with another concurrent request. Please retry.");
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("Installment.ConcurrencyConflict",
                "Request conflicted with another concurrent request. Please retry.");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Installment.Add",
            entityType: nameof(Installment),
            entityId: id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                request.ContractId,
                request.SequenceNumber,
                request.Amount,
                request.CoveredPeriodStartUtc,
                request.CoveredPeriodEndUtc
            }),
            cancellationToken: cancellationToken);

        return id;
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
