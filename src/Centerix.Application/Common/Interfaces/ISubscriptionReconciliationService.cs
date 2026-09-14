namespace Centerix.Application.Common.Interfaces;

/// <summary>
/// Deterministic, idempotent reconciliation of a tenant's Subscription state from:
/// - Subscription service period
/// - Installment/obligation state and DueDate
/// - Successful PaymentAllocation settlement
/// - Centrally controlled Grace Period
///
/// PastDue and Suspended are system-derived states — they cannot be set by any administrator.
/// Running reconciliation twice must produce the same final state (idempotent).
/// </summary>
public interface ISubscriptionReconciliationService
{
    /// <summary>
    /// Reconciles the subscription state for the given tenant. Callable from application
    /// services, domain workflows, or after payment allocation. A later scheduling mechanism
    /// can invoke this safely.
    /// </summary>
    Task ReconcileAsync(string tenantId, CancellationToken cancellationToken = default);
}
