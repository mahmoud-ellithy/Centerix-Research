namespace Centerix.Domain.Platform.Billing.Payments.Enums;

/// <summary>
/// Customer ledger entry types. Immutable financial movements.
/// </summary>
public enum LedgerEntryType : byte
{
    /// <summary>Invoice charge (increases customer balance).</summary>
    InvoiceCharge = 0,

    /// <summary>Payment settlement (decreases customer balance).</summary>
    PaymentSettlement = 1,

    /// <summary>Credit creation (future credit module).</summary>
    CreditCreation = 2,

    /// <summary>Credit usage (future credit module).</summary>
    CreditUsage = 3,

    /// <summary>Credit expiry (future credit module).</summary>
    CreditExpiry = 4,

    /// <summary>Manual adjustment (future).</summary>
    Adjustment = 5
}
