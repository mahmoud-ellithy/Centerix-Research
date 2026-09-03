namespace Centerix.Domain.Teachers.SalaryPayments;

using System.ComponentModel.DataAnnotations;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.Teachers;

public class SalaryPayment : AuditableEntity<Guid>
{
    public Guid TeacherId { get; private set; }

    public byte PeriodMonth { get; private set; }
    public short PeriodYear { get; private set; }

    public decimal GrossAmount { get; private set; }
    public decimal NetAmount { get; private set; }

    public SalaryPaymentStatus Status { get; private set; }
    public DateTime? PaidAt { get; private set; }

    public Teacher Teacher { get; private set; } = default!;

    /// <summary>
    /// Optimistic concurrency token (SQL Server rowversion). Guards the financial
    /// state machine (MarkPaid / Cancel) against silent last-write-wins races.
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; internal set; }

    private SalaryPayment() { }

    private SalaryPayment(
        Guid id,
        Guid teacherId,
        byte periodMonth,
        short periodYear,
        decimal grossAmount,
        decimal netAmount,
        SalaryPaymentStatus status,
        DateTime? paidAt)
        : base(id)
    {
        TeacherId = teacherId;
        PeriodMonth = periodMonth;
        PeriodYear = periodYear;
        GrossAmount = grossAmount;
        NetAmount = netAmount;
        Status = status;
        PaidAt = paidAt;
    }

    /// <summary>
    /// Creates a new salary payment in its only valid initial state: Pending with
    /// PaidAt = null. The initial state is deliberately NOT caller-supplied — the
    /// lifecycle may only move forward through MarkPaid / Cancel.
    /// </summary>
    public static Result<SalaryPayment> Create(
        Guid id,
        Guid teacherId,
        byte periodMonth,
        short periodYear,
        decimal grossAmount,
        decimal netAmount)
    {
        if (teacherId == Guid.Empty)
            return SalaryPaymentErrors.TeacherIdRequired;

        if (periodMonth < 1 || periodMonth > 12)
            return SalaryPaymentErrors.PeriodMonthOutOfRange;

        if (periodYear < 2000 || periodYear > 2100)
            return SalaryPaymentErrors.PeriodYearRequired;

        if (grossAmount <= 0)
            return SalaryPaymentErrors.GrossAmountRequired;

        if (netAmount <= 0)
            return SalaryPaymentErrors.NetAmountRequired;

        return new SalaryPayment(
            id,
            teacherId,
            periodMonth,
            periodYear,
            grossAmount,
            netAmount,
            SalaryPaymentStatus.Pending,
            paidAt: null);
    }

    public Result<Updated> MarkPaid(DateTime paidAt)
    {
        if (Status == SalaryPaymentStatus.Paid)
            return SalaryPaymentErrors.DuplicatePayment;

        // Cancelled is terminal; a cancelled payment can never become Paid.
        if (Status == SalaryPaymentStatus.Cancelled)
            return SalaryPaymentErrors.InvalidStatus;

        // Status and PaidAt mutate together as one atomic domain transition; both are
        // persisted in the same SaveChangesAsync (and the same row under RowVersion).
        Status = SalaryPaymentStatus.Paid;
        PaidAt = paidAt;
        return Result.Updated;
    }

    public Result<Updated> Cancel()
    {
        // A paid payment can no longer be cancelled.
        if (Status == SalaryPaymentStatus.Paid)
            return SalaryPaymentErrors.InvalidStatus;

        // NOTE (documented ambiguity, not a new business rule): cancelling an
        // already-Cancelled payment is a no-op that succeeds. The project defines no
        // explicit idempotency semantics for repeated Cancel; the current behavior is
        // preserved as-is pending a product decision.
        Status = SalaryPaymentStatus.Cancelled;
        PaidAt = null;
        return Result.Updated;
    }
}