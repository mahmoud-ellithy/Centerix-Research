namespace Centerix.Application.Common.Interfaces;

using Centerix.Domain.Platform.Billing.Installments;
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
/// The interface returns nullable/optional results for the cases where the rule algebra tolerates
/// absence (e.g. <see cref="GetCurrentPaymentMethodAsync"/> returns <c>null</c> when no completed
/// payment allocation exists yet). <c>Contract.NotFound</c> is signalled via <c>TenantScopeException</c>
/// only when the row exists under another tenant; a true miss returns <c>null</c> from the relevant
/// fact methods because the application command already verified ownership.
/// </para>
/// </remarks>
public interface IOwnerOnlyFactQuery
{
    /// <summary>
    /// Returns the contract's current status and payment terms for the supplied tenant, or
    /// <c>null</c> when no contract exists with that id under <paramref name="tenantId"/>.
    /// Throws <see cref="TenantScopeException"/> if the contract exists under a different tenant.
    /// </summary>
    Task<ContractCommercialFacts?> GetContractCommercialFactsAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the contract's effective-at instant and contracted amount, or <c>null</c> when no
    /// contract exists with that id under <paramref name="tenantId"/>. Throws
    /// <see cref="TenantScopeException"/> if the contract exists under a different tenant.
    /// </summary>
    Task<ContractSnapshotFacts?> GetContractSnapshotFactsAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total amount paid against this contract (sum of completed-payment amounts on
    /// active allocations scoped to the contract's invoices). Returns <c>0</c> when no completed
    /// payments exist. Throws <see cref="TenantScopeException"/> if the contract exists under a
    /// different tenant.
    /// </summary>
    Task<decimal> GetAmountPaidAsync(
        string tenantId,
        Guid contractId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the canonical (trimmed, upper-invariant) payment method observed on the most recent
    /// completed payment allocation for this contract, or <c>null</c> when no completed payment
    /// allocation exists yet. Throws <see cref="TenantScopeException"/> if the contract exists
    /// under a different tenant.
    /// </summary>
    Task<string?> GetCurrentPaymentMethodAsync(
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

/// <summary>Commercial header facts required by the rule algebra.</summary>
public sealed record ContractCommercialFacts(
    Guid ContractId,
    ContractStatus Status,
    PaymentTerms PaymentTerms);

/// <summary>Snapshot facts required by the rule algebra.</summary>
public sealed record ContractSnapshotFacts(
    Guid ContractId,
    DateTime EffectiveAtUtc,
    decimal ContractedAmount,
    int DurationMonths);

/// <summary>
/// Raised when a fact query resolves a row that does not belong to the supplied
/// <c>tenantId</c>. Callers MUST treat this as a hard security violation.
/// </summary>
public sealed class TenantScopeException : Exception
{
    public TenantScopeException(string message) : base(message) { }
}