namespace Centerix.Infrastructure.Platform.Services;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IOwnerOnlyFactQuery"/>. This is the ONLY place in the
/// solution that translates eligibility-fact queries into SQL. Every method scopes by
/// <c>tenantId</c> AND by the entity id and defends against cross-tenant access via
/// <see cref="TenantScopeException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The implementation deliberately uses <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TSource}"/>
/// so that the global tenant filter does not silently hide a cross-tenant row; instead we
/// explicitly compare the loaded <c>TenantId</c> against the supplied <paramref name="tenantId"/>
/// and throw <see cref="TenantScopeException"/> on mismatch. A true miss returns <c>null</c>.
/// </para>
/// </remarks>
public sealed class OwnerOnlyFactQueryEfAdapter : IOwnerOnlyFactQuery
{
    private readonly IAppDbContext _db;

    public OwnerOnlyFactQueryEfAdapter(IAppDbContext db) => _db = db;

    public async Task<ContractCommercialFacts?> GetContractCommercialFactsAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Contracts
            .IgnoreQueryFilters()
            .Where(c => c.Id == contractId)
            .Select(c => new { c.Id, c.TenantId, c.Status, c.PaymentTerms })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null) return null;

        EnsureOwner(row.TenantId!, tenantId, nameof(ContractCommercialFacts), contractId);

        return new ContractCommercialFacts(row.Id, row.Status, row.PaymentTerms);
    }

    public async Task<ContractSnapshotFacts?> GetContractSnapshotFactsAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Contracts
            .IgnoreQueryFilters()
            .Where(c => c.Id == contractId)
            .Select(c => new
            {
                c.Id,
                c.TenantId,
                c.EffectiveAtUtc,
                c.ContractedAmount,
                c.DurationMonths
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null) return null;

        EnsureOwner(row.TenantId!, tenantId, nameof(ContractSnapshotFacts), contractId);

        return new ContractSnapshotFacts(
            row.Id,
            row.EffectiveAtUtc,
            row.ContractedAmount,
            row.DurationMonths);
    }

    public async Task<decimal> GetAmountPaidAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        // Confirm ownership first so a contract belonging to another tenant does not leak its total.
        var contractRow = await _db.Contracts
            .IgnoreQueryFilters()
            .Where(c => c.Id == contractId)
            .Select(c => new { c.TenantId })
            .FirstOrDefaultAsync(cancellationToken);

        if (contractRow is null) return 0m;
        EnsureOwner(contractRow.TenantId!, tenantId, nameof(GetAmountPaidAsync), contractId);

        var total = await _db.Payments
            .Where(p => p.TenantId == tenantId
                && p.Status == PaymentStatus.Completed
                && p.Allocations.Any(a =>
                    a.Status == PaymentAllocationStatus.Active
                    && a.Invoice != null
                    && a.Invoice.ContractId == contractId))
            .SumAsync(p => p.Allocations
                .Where(a => a.Status == PaymentAllocationStatus.Active
                    && a.Invoice != null
                    && a.Invoice.ContractId == contractId)
                .Sum(a => a.AllocatedAmount), cancellationToken);

        return total;
    }

    public async Task<string?> GetCurrentPaymentMethodAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        // Confirm ownership first so a contract belonging to another tenant does not leak its method.
        var contractRow = await _db.Contracts
            .IgnoreQueryFilters()
            .Where(c => c.Id == contractId)
            .Select(c => new { c.TenantId })
            .FirstOrDefaultAsync(cancellationToken);

        if (contractRow is null) return null;
        EnsureOwner(contractRow.TenantId!, tenantId, nameof(GetCurrentPaymentMethodAsync), contractId);

        var rawMethod = await _db.Payments
            .Where(p => p.TenantId == tenantId
                && p.Status == PaymentStatus.Completed
                && p.Allocations.Any(a =>
                    a.Status == PaymentAllocationStatus.Active
                    && a.Invoice != null
                    && a.Invoice.ContractId == contractId))
            .OrderByDescending(p => p.CompletedAtUtc)
            .Select(p => (PaymentMethod?)p.Method)
            .FirstOrDefaultAsync(cancellationToken);

        if (rawMethod is null) return null;
        var canonical = rawMethod.Value.ToString().Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(canonical) ? null : canonical;
    }

    public async Task<bool> HasOverdueInstallmentAsync(
        string tenantId,
        Guid contractId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        // Confirm ownership first.
        var contractRow = await _db.Contracts
            .IgnoreQueryFilters()
            .Where(c => c.Id == contractId)
            .Select(c => new { c.TenantId })
            .FirstOrDefaultAsync(cancellationToken);

        if (contractRow is null) return false;
        EnsureOwner(contractRow.TenantId!, tenantId, nameof(HasOverdueInstallmentAsync), contractId);

        return await _db.Installments
            .Where(i => i.ContractId == contractId
                && i.TenantId == tenantId
                && i.Status != InstallmentStatus.Cancelled
                && i.Status != InstallmentStatus.Paid
                && i.DueDateUtc < utcNow
                && (i.Amount - i.SettledAmount) > 0)
            .AnyAsync(cancellationToken);
    }

    private static void EnsureOwner(
        string rowTenantId,
        string expectedTenantId,
        string factName,
        Guid entityId)
    {
        if (!string.Equals(rowTenantId, expectedTenantId, StringComparison.Ordinal))
        {
            throw new TenantScopeException(
                $"Cross-tenant access blocked: fact '{factName}' for entity '{entityId}' " +
                $"belongs to a different tenant. Requested tenant='{expectedTenantId}'.");
        }
    }
}