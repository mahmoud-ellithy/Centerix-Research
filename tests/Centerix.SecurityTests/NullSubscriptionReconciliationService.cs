namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;

/// <summary>
/// No-op implementation of ISubscriptionReconciliationService for tests that
/// directly construct AllocatePaymentHandler without needing reconciliation logic.
/// </summary>
public sealed class NullSubscriptionReconciliationService : ISubscriptionReconciliationService
{
    public static readonly NullSubscriptionReconciliationService Instance = new();

    public Task ReconcileAsync(string tenantId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
