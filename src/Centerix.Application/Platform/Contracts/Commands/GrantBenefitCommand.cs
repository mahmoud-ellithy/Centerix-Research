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
/// Explicit GRANT operation for a PhysicalGift ContractBenefit.
/// Transitions FulfillmentStatus from Pending → Granted.
/// Requires:
///   * BenefitType == PhysicalGift
///   * EligibilityStatus == Eligible
///   * FulfillmentStatus == Pending
/// Idempotent: already-Granted or already-Delivered calls return the existing state.
/// Does NOT set Delivered — use <see cref="MarkBenefitDeliveredCommand"/> for that.
/// </summary>
public record GrantBenefitCommand(
    Guid ContractId,
    Guid BenefitId) : IRequest<Result<BenefitGrantResult>>;

/// <summary>
/// Result of a benefit grant operation.
/// </summary>
public sealed record BenefitGrantResult
{
    public Guid BenefitId { get; init; }
    public FulfillmentStatus FulfillmentStatus { get; init; }
    public DateTime? GrantedAtUtc { get; init; }
    public string? GrantedBy { get; init; }
}

public class GrantBenefitHandler(
    IAppDbContext dbContext,
    ICurrentUser currentUserService,
    IAuditWriter auditWriter) : IRequestHandler<GrantBenefitCommand, Result<BenefitGrantResult>>
{
    private const int MaxDeadlockRetries = 3;

    public async Task<Result<BenefitGrantResult>> Handle(
        GrantBenefitCommand request,
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

        return Error.Conflict("BenefitGrant.ConcurrencyConflict",
            "This grant request conflicted with another concurrent request. Please retry.");
    }

    private static bool IsRetryableError(Result<BenefitGrantResult> result)
    {
        return result.Errors?.Any(e => e.Code == "BenefitGrant.ConcurrencyConflict") ?? false;
    }

    private async Task<Result<BenefitGrantResult>> TryHandleAsync(
        GrantBenefitCommand request,
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

            return Error.Conflict("BenefitGrant.ConcurrencyConflict",
                "This grant request conflicted with another concurrent request. Please retry.");
        }
    }

    private async Task<Result<BenefitGrantResult>> ExecuteGrantAsync(
        GrantBenefitCommand request,
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

        // Only PhysicalGift benefits participate in the grant/delivery lifecycle.
        if (benefit.BenefitType != ContractBenefitType.PhysicalGift)
            return ContractErrors.Benefit.OnlyPhysicalGiftCanBeDelivered;

        // Idempotent: already granted.
        if (benefit.FulfillmentStatus == FulfillmentStatus.Granted)
        {
            return new BenefitGrantResult
            {
                BenefitId = benefit.Id,
                FulfillmentStatus = benefit.FulfillmentStatus,
                GrantedAtUtc = benefit.GrantedAtUtc,
                GrantedBy = benefit.GrantedBy
            };
        }

        // Idempotent: already delivered — return terminal state.
        if (benefit.FulfillmentStatus == FulfillmentStatus.Delivered)
        {
            return new BenefitGrantResult
            {
                BenefitId = benefit.Id,
                FulfillmentStatus = benefit.FulfillmentStatus,
                GrantedAtUtc = benefit.GrantedAtUtc,
                GrantedBy = benefit.GrantedBy
            };
        }

        // Domain guards.
        if (benefit.EligibilityStatus != BenefitEligibilityStatus.Eligible)
            return ContractErrors.Benefit.NotEligible;

        if (benefit.FulfillmentStatus != FulfillmentStatus.Pending)
            return ContractErrors.Benefit.InvalidFulfillmentTransition;

        var now = DateTime.UtcNow;
        var grantedBy = currentUserService.UserId;

        var grantResult = benefit.Grant(now, grantedBy, contract.TenantId);
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

            return Error.Conflict("BenefitGrant.ConcurrencyConflict",
                "This grant request conflicted with another concurrent request. Please retry.");
        }
        catch (Exception ex) when (IsDeadlockException(ex))
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);

            return Error.Conflict("BenefitGrant.ConcurrencyConflict",
                "This grant request conflicted with another concurrent request. Please retry.");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Benefit.Granted",
            entityType: nameof(ContractBenefit),
            entityId: benefit.Id.ToString(),
            oldValue: AuditPayload.Serialize(new { FulfillmentStatus = FulfillmentStatus.Pending.ToString() }),
            newValue: AuditPayload.Serialize(new
            {
                FulfillmentStatus = FulfillmentStatus.Granted.ToString(),
                GrantedAtUtc = now,
                GrantedBy = grantedBy,
                benefit.Name,
                benefit.ContractualValue,
                benefit.CurrencyCode
            }),
            cancellationToken: cancellationToken);

        return new BenefitGrantResult
        {
            BenefitId = benefit.Id,
            FulfillmentStatus = benefit.FulfillmentStatus,
            GrantedAtUtc = benefit.GrantedAtUtc,
            GrantedBy = benefit.GrantedBy
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