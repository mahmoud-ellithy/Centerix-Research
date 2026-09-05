namespace Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Allocation status for a payment allocation record.
/// </summary>
public enum PaymentAllocationStatus : byte
{
    /// <summary>Allocation active and applied to invoice.</summary>
    Active = 0,

    /// <summary>Allocation reversed (future refund module).</summary>
    Reversed = 1
}
