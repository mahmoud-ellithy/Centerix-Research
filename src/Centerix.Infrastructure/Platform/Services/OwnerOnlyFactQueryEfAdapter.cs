namespace Centerix.Infrastructure.Platform.Services;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
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
/// The implementation deliberately uses
/// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TSource}"/> so the global
/// tenant filter does not silently hide a cross-tenant row; instead we explicitly compare the
/// loaded <c>TenantId</c> against the supplied <paramref name="tenantId"/> and throw
/// <see cref="TenantScopeException"/> on mismatch. A true miss returns <c>null</c>.
/// </para>
/// <para>
/// All completed-payment lookups filter by <c>Status = Completed</c> AND by the chain
/// Payment → Allocation → Invoice → ContractId = supplied. This is the existing Centerix
/// financial model — we do not invent a parallel payment-total model.
/// </para>
/// </remarks>
public sealed class OwnerOnlyFactQueryEfAdapter : IOwnerOnlyFactQuery
{
    private readonly IAppDbContext _db;

    public OwnerOnlyFactQueryEfAdapter(IAppDbContext db) => _db = db;

    public async Task<ContractFacts?> GetContractFactsAsync(
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
                c.Status,
                c.PaymentTerms,
                c.EffectiveAtUtc,
                c.DurationMonths,
                c.ContractedAmount,
                c.CurrencyCode
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null) return null;

        EnsureOwner(row.TenantId!, tenantId, nameof(GetContractFactsAsync), contractId);

        return new ContractFacts(
            row.Id,
            row.Status,
            row.PaymentTerms,
            DateTime.SpecifyKind(row.EffectiveAtUtc, DateTimeKind.Utc),
            row.DurationMonths,
            row.ContractedAmount,
            row.CurrencyCode);
    }

    public async Task<IReadOnlyList<CompletedPaymentFact>> GetCompletedPaymentsAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        // Ownership guard runs against the contract; we do NOT simply trust the global filter
        // because we want a hard exception (not a silent zero-row result) on cross-tenant access.
        var contractRow = await _db.Contracts
            .IgnoreQueryFilters()
            .Where(c => c.Id == contractId)
            .Select(c => new { c.TenantId })
            .FirstOrDefaultAsync(cancellationToken);

        if (contractRow is null)
            return Array.Empty<CompletedPaymentFact>();

        EnsureOwner(contractRow.TenantId!, tenantId, nameof(GetCompletedPaymentsAsync), contractId);

        // Completed payments whose allocation chain links back to this contract.
        var rows = await _db.Payments
            .Where(p => p.TenantId == tenantId
                && p.Status == PaymentStatus.Completed
                && p.CompletedAtUtc != null
                && p.Allocations.Any(a =>
                    a.Status == PaymentAllocationStatus.Active
                    && a.Invoice != null
                    && a.Invoice.ContractId == contractId))
            .Select(p => new
            {
                p.Id,
                CompletedAt = p.CompletedAtUtc!.Value,
                p.CurrencyCode,
                p.Amount,
                Method = p.Method
            })
            .ToListAsync(cancellationToken);

        var facts = new List<CompletedPaymentFact>(rows.Count);
        foreach (var r in rows)
        {
            facts.Add(new CompletedPaymentFact(
                PaymentId: r.Id,
                CompletedAtUtc: DateTime.SpecifyKind(r.CompletedAt, DateTimeKind.Utc),
                Amount: r.Amount,
                CurrencyCode: r.CurrencyCode.Trim().ToUpperInvariant(),
                MethodCanonical: r.Method.ToString().Trim().ToUpperInvariant()));
        }

        return facts;
    }

    public async Task<bool> HasOverdueInstallmentAsync(
        string tenantId,
        Guid contractId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
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