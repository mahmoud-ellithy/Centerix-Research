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

        // Per-Payment allocation slice for THIS contract.
        // We project allocation + payment in two stages: first fetch (PaymentId, sum) pairs,
        // then join back to Payment to fetch the immutable payment attributes. This avoids
        // GroupBy over a navigation property which is brittle across EF versions.
        var perPaymentSlice = await _db.PaymentAllocations
            .Where(a => a.Status == PaymentAllocationStatus.Active
                && a.Payment != null
                && a.Invoice != null
                && a.Invoice.ContractId == contractId
                && a.Payment.Status == PaymentStatus.Completed
                && a.Payment.CompletedAtUtc != null
                && a.Payment.TenantId == tenantId)
            .GroupBy(a => a.PaymentId)
            .Select(g => new
            {
                PaymentId = g.Key,
                AllocatedAmountForThisContract = g.Sum(x => x.AllocatedAmount)
            })
            .ToListAsync(cancellationToken);

        if (perPaymentSlice.Count == 0)
            return Array.Empty<CompletedPaymentFact>();

        var paymentIds = perPaymentSlice.Select(x => x.PaymentId).ToHashSet();

        var paymentRows = await _db.Payments
            .Where(p => p.TenantId == tenantId
                && paymentIds.Contains(p.Id)
                && p.Status == PaymentStatus.Completed
                && p.CompletedAtUtc != null)
            .Select(p => new
            {
                p.Id,
                CompletedAt = p.CompletedAtUtc!.Value,
                p.CurrencyCode,
                Method = p.Method
            })
            .ToListAsync(cancellationToken);

        var facts = new List<CompletedPaymentFact>(perPaymentSlice.Count);
        var byPayment = perPaymentSlice.ToDictionary(x => x.PaymentId);
        foreach (var p in paymentRows)
        {
            if (!byPayment.TryGetValue(p.Id, out var slice)) continue;
            facts.Add(new CompletedPaymentFact(
                PaymentId: p.Id,
                CompletedAtUtc: DateTime.SpecifyKind(p.CompletedAt, DateTimeKind.Utc),
                AllocatedAmountForThisContract: slice.AllocatedAmountForThisContract,
                CurrencyCode: p.CurrencyCode.Trim().ToUpperInvariant(),
                MethodCanonical: p.Method.ToString().Trim().ToUpperInvariant()));
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