namespace Centerix.Application.Platform.Contracts.Commands;

using System.Data;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// Command to mark a contract benefit as delivered (physical gift handover).
/// Idempotent: if the benefit is already delivered, returns success without mutation.
/// Uses optimistic concurrency to prevent duplicate delivery events.
/// </summary>
public record MarkBenefitDeliveredCommand(
    Guid ContractId,
    Guid BenefitId) : IRequest<Result<BenefitDeliveryResult>>;

/// <summary>
/// Result of a benefit delivery operation.
/// </summary>
public sealed record BenefitDeliveryResult
{
    public Guid BenefitId { get; init; }
    public bool IsDelivered { get; init; }
    public DateTime? DeliveredAtUtc { get; init; }
}

public class MarkBenefitDeliveredHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter) : IRequestHandler<MarkBenefitDeliveredCommand, Result<BenefitDeliveryResult>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<BenefitDeliveryResult>> Handle(
        MarkBenefitDeliveredCommand request,
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

        return Error.Conflict("BenefitDelivery.ConcurrencyConflict",
            "This delivery request conflicted with another concurrent request. Please retry.");
    }

    private static bool IsRetryableError(Result<BenefitDeliveryResult> result)
    {
        return result.Errors?.Any(e => e.Code == "BenefitDelivery.ConcurrencyConflict") ?? false;
    }

    private async Task<Result<BenefitDeliveryResult>> TryHandleAsync(
        MarkBenefitDeliveredCommand request,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            return await ExecuteDeliveryAsync(request, cancellationToken, transaction);
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("BenefitDelivery.ConcurrencyConflict",
                "This delivery request conflicted with another concurrent request. Please retry.");
        }
    }

    private async Task<Result<BenefitDeliveryResult>> ExecuteDeliveryAsync(
        MarkBenefitDeliveredCommand request,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction)
    {
        var contract = await dbContext.Contracts
            .Include(c => c.Benefits)
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
            return ContractErrors.ContractNotFound(request.ContractId);

        var benefit = contract.Benefits.FirstOrDefault(b => b.Id == request.BenefitId);
        if (benefit is null)
            return ContractErrors.Benefit.NotFound(request.BenefitId);

        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId) || contract.TenantId != tenantId)
            return ContractErrors.Benefit.CrossTenantBenefit;

        // Already delivered - idempotent
        if (benefit.IsGranted)
        {
            return new BenefitDeliveryResult
            {
                BenefitId = benefit.Id,
                IsDelivered = true,
                DeliveredAtUtc = benefit.GrantedAtUtc
            };
        }

        // Must be eligible before delivery
        if (benefit.EligibilityStatus != BenefitEligibilityStatus.Eligible
            && benefit.EligibilityStatus != BenefitEligibilityStatus.Delivered)
        {
            return ContractErrors.Benefit.NotEligible;
        }

        var now = DateTime.UtcNow;
        var deliveredBy = currentUserService.UserId;

        var grantResult = benefit.MarkGranted(now, deliveredBy);
        if (!grantResult.IsSuccess)
            return grantResult.Errors!;

        dbContext.StampAddedTenantIds(contract.TenantId!);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("BenefitDelivery.ConcurrencyConflict",
                "This delivery request conflicted with another concurrent request. Please retry.");
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("BenefitDelivery.ConcurrencyConflict",
                "This delivery request conflicted with another concurrent request. Please retry.");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Benefit.Delivered",
            entityType: nameof(ContractBenefit),
            entityId: benefit.Id.ToString(),
            oldValue: AuditPayload.Serialize(new { EligibilityStatus = BenefitEligibilityStatus.Eligible.ToString() }),
            newValue: AuditPayload.Serialize(new
            {
                EligibilityStatus = BenefitEligibilityStatus.Delivered.ToString(),
                DeliveredAtUtc = now,
                DeliveredBy = deliveredBy,
                benefit.Name,
                benefit.ContractualValue,
                benefit.CurrencyCode
            }),
            cancellationToken: cancellationToken);

        return new BenefitDeliveryResult
        {
            BenefitId = benefit.Id,
            IsDelivered = true,
            DeliveredAtUtc = now
        };
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
