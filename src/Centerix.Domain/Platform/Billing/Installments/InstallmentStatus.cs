namespace Centerix.Domain.Platform.Billing.Installments;

/// <summary>
/// Lifecycle status for an installment (payment obligation).
/// Status transitions are domain-controlled: clients cannot arbitrarily set status.
/// </summary>
public enum InstallmentStatus : byte
{
    /// <summary>Installment created, not yet due, no payment allocated.</summary>
    Pending = 0,

    /// <summary>Partially paid: 0 &lt; SettledAmount &lt; Amount.</summary>
    PartiallyPaid = 1,

    /// <summary>Fully paid: SettledAmount &gt;= Amount.</summary>
    Paid = 2,

    /// <summary>Overdue: RemainingAmount &gt; 0 AND DueDateUtc &lt; now.</summary>
    Overdue = 3,

    /// <summary>Cancelled through explicit domain operation. Not mutable from API.</summary>
    Cancelled = 4
}
