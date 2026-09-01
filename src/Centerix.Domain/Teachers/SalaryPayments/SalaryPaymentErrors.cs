namespace Centerix.Domain.Teachers.SalaryPayments;

using Centerix.Domain.Common.Results;

public static class SalaryPaymentErrors
{
    public static Error TeacherIdRequired =>
        Error.Validation("SalaryPayment.TeacherId_Required", "Teacher is required");

    public static Error PeriodMonthRequired =>
        Error.Validation("SalaryPayment.PeriodMonth_Required", "Period month is required");

    public static Error PeriodMonthOutOfRange =>
        Error.Validation("SalaryPayment.PeriodMonth_OutOfRange", "Period month must be between 1 and 12");

    public static Error PeriodYearRequired =>
        Error.Validation("SalaryPayment.PeriodYear_Required", "Period year is required");

    public static Error GrossAmountRequired =>
        Error.Validation("SalaryPayment.GrossAmount_Required", "Gross amount must be greater than zero");

    public static Error NetAmountRequired =>
        Error.Validation("SalaryPayment.NetAmount_Required", "Net amount must be greater than zero");

    public static Error InvalidStatus =>
        Error.Validation("SalaryPayment.InvalidStatus", "Invalid salary payment status");

    public static Error NotFound =>
        Error.NotFound("SalaryPayment.NotFound", "Salary payment was not found");

    public static Error DuplicatePayment =>
        Error.Conflict("SalaryPayment.Duplicate", "A salary payment for this teacher in this period already exists");
}