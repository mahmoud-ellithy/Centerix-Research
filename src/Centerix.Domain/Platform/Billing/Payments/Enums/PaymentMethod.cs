namespace Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Payment methods supported by the system. Preserves original method for future refunds.
/// </summary>
public enum PaymentMethod : byte
{
    /// <summary>Cash payment (manual receipt).</summary>
    Cash = 0,

    /// <summary>Digital wallet (e.g., Vodafone Cash, Etisalat Cash).</summary>
    Wallet = 1,

    /// <summary>InstaPay (Egyptian instant payment network).</summary>
    InstaPay = 2,

    /// <summary>Credit/Debit card.</summary>
    Card = 3,

    /// <summary>Bank transfer.</summary>
    BankTransfer = 4,

    /// <summary>Other/unspecified method.</summary>
    Other = 5
}
