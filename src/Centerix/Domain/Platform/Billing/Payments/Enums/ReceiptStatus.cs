namespace Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Receipt lifecycle states.
/// </summary>
public enum ReceiptStatus : byte
{
    /// <summary>Receipt issued for a completed payment.</summary>
    Issued = 0,

    /// <summary>Receipt cancelled (future, e.g., if underlying payment is found invalid).</summary>
    Cancelled = 1
}
