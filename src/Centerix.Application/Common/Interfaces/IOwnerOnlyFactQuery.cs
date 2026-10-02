namespace Centerix.Application.Common.Interfaces;

using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Promotions.Enums;

/// <summary>
/// Tenant-scoped, read-only fact query used to assemble an <c>EligibilityContext</c>. This is the
/// SINGLE I/O seam between the closed rule algebra and the database: <c>EligibilityContextBuilder</c>
/// may ONLY consult facts via this interface. The interface is implemented once in the infrastructure
/// layer by <c>OwnerOnlyFactQueryEfAdapter</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes the authorized <c>tenantId</c> as the first parameter. Implementations MUST
/// scope database queries by <c>tenantId</c> AND by the entity id. Returning a fact for the wrong
/// tenant is a security violation; <see cref="TenantScopeException"/> is thrown when the row exists
/// but does not belong to the supplied tenant.
/// </para>
/// <para>
/// The fact queries return immutable aggregates so the rule engine can derive <c>AmountPaid</c>,
/// <c>PaymentMethod</c>, and <c>CompletedBy</c> from a single, atomic snapshot — avoiding N+1
/// database queries for composite rules.
/// </para>
/// </remarks>
public interface IOwnerOnlyFactQuery
{
    /// <summary>
    /// Returns the commercial + lifecycle facts for the supplied tenant/contract pair, or
    /// <c>null</c> when no contract exists with that id under <paramref name="tenantId"/>.
    /// Throws <see cref="TenantScopeException"/> if the contract exists under a different tenant.
    /// </summary>
    Task<ContractFacts?> GetContractFactsAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the authoritative list of completed-payment facts for the supplied tenant/contract
    /// pair. Empty when no qualifying payments exist. The list is currency-tagged so the rule
    /// algebra can reject mismatched currencies without re-querying. Throws
    /// <see cref="TenantScopeException"/> if the contract exists under a different tenant.
    /// </summary>
    Task<IReadOnlyList<CompletedPaymentFact>> GetCompletedPaymentsAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> iff the contract has at least one overdue installment at <paramref name="utcNow"/>.
    /// Overdue = status != Paid/Cancelled AND due-date &lt; utcNow AND remaining amount &gt; 0.
    /// </summary>
    Task<bool> HasOverdueInstallmentAsync(
        string tenantId,
        Guid contractId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authoritative commercial + lifecycle facts for a single contract. Returned as one immutable
/// aggregate so <c>EligibilityContextBuilder</c> needs only one query for the contract header.
/// </summary>
public sealed record ContractFacts(
    Guid ContractId,
    ContractStatus Status,
    PaymentTerms PaymentTerms,
    DateTime EffectiveAtUtc,
    int DurationMonths,
    decimal ContractedAmount,
    string CurrencyCode);

/// <summary>
/// Raised when a fact query resolves a row that does not belong to the supplied
/// <c>tenantId</c>. Callers MUST treat this as a hard security violation.
/// </summary>
public sealed class TenantScopeException : Exception
{
    public TenantScopeException(string message) : base(message) { }
}