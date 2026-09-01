namespace Centerix.Domain.Teachers.SalaryPayments;

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
    public DateTimeOffset? PaidAt { get; private set; }

    public Teacher Teacher { get; private set; } = default!;

    private SalaryPayment() { }

    private SalaryPayment(
        Guid id,
        Guid teacherId,
        byte periodMonth,
        short periodYear,
        decimal grossAmount,
        decimal netAmount,
        SalaryPaymentStatus status,
        DateTimeOffset? paidAt)
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

    public static Result<SalaryPayment> Create(
        Guid id,
        Guid teacherId,
        byte periodMonth,
        short periodYear,
        decimal grossAmount,
        decimal netAmount,
        SalaryPaymentStatus status,
        DateTimeOffset? paidAt)
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

        if (!Enum.IsDefined(status))
            return SalaryPaymentErrors.InvalidStatus;

        return new SalaryPayment(id, teacherId, periodMonth, periodYear, grossAmount, netAmount, status, paidAt);
    }

    public Result<Updated> MarkPaid(DateTimeOffset paidAt)
    {
        if (Status == SalaryPaymentStatus.Paid)
            return SalaryPaymentErrors.DuplicatePayment;

        Status = SalaryPaymentStatus.Paid;
        PaidAt = paidAt;
        return Result.Updated;
    }

    public Result<Updated> Cancel()
    {
        if (Status == SalaryPaymentStatus.Paid)
            return SalaryPaymentErrors.InvalidStatus;

        Status = SalaryPaymentStatus.Cancelled;
        PaidAt = null;
        return Result.Updated;
    }
}