namespace Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Payment lifecycle states. A payment counts toward settlement ONLY when <see cref="Completed"/>.
/// <see cref="Refunded"/> is intentionally NOT here — the future Refund module will create
/// separate refund transactions rather than mutating a completed payment's status.
/// </summary>
public enum PaymentStatus : byte
{
    /// <summary>Payment created but not yet submitted to provider.</summary>
    Pending = 0,

    /// <summary>Payment submitted to provider, awaiting confirmation.</summary>
    Processing = 1,

    /// <summary>Payment confirmed by provider. Counts toward settlement. Immutable.</summary>
    Completed = 2,

    /// <summary>Payment failed. Does NOT count toward settlement.</summary>
    Failed = 3
}
