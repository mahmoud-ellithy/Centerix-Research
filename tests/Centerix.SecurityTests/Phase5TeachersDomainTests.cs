namespace Centerix.SecurityTests;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.SalaryPayments;
using Centerix.Domain.Teachers.Teachers;

using Xunit;

/// <summary>
/// Phase 5 Teachers remediation domain invariants (F-02 / H-02):
/// the SalaryPayment lifecycle is Pending → Paid | Cancelled with terminal states, the
/// initial state is NOT caller-supplied, and Teacher soft-delete guards hold.
/// Pure domain — no infrastructure, no provider.
/// </summary>
public class Phase5TeachersDomainTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Result<SalaryPayment> CreateValidPayment(
        Guid? teacherId = null,
        byte month = 9,
        short year = 2026,
        decimal gross = 5000m,
        decimal net = 4500m)
        => SalaryPayment.Create(
            Guid.NewGuid(), teacherId ?? Guid.NewGuid(), month, year, gross, net);

    private static Result<Teacher> CreateValidTeacher()
        => Teacher.Create(
            Guid.NewGuid(), $"user-{Guid.NewGuid():N}", Guid.NewGuid(),
            "Ahmed Ali", "01000000000", "BSc", 5, TeacherStatus.Active, Today);

    // ------------------------------------------------------------------
    // Creation (F-02: the initial state can no longer be supplied/bypassed)
    // ------------------------------------------------------------------

    [Fact]
    public void SalaryPayment_Create_AlwaysStartsPending_WithNullPaidAt()
    {
        var result = CreateValidPayment();

        Assert.True(result.IsSuccess, string.Join(",", result.Errors?.Select(e => e.Code) ?? []));
        Assert.Equal(SalaryPaymentStatus.Pending, result.Value.Status);
        Assert.Null(result.Value.PaidAt);
    }

    [Fact]
    public void SalaryPayment_Create_RejectsInvalidPeriodOrAmounts()
    {
        var badMonth = CreateValidPayment(month: 0);
        var badMonthHigh = CreateValidPayment(month: 13);
        var badYear = CreateValidPayment(year: 1999);
        var badGross = CreateValidPayment(gross: 0m);
        var badNet = CreateValidPayment(net: -1m);

        Assert.Contains(badMonth.Errors!, e => e.Code == "SalaryPayment.PeriodMonth_OutOfRange");
        Assert.Contains(badMonthHigh.Errors!, e => e.Code == "SalaryPayment.PeriodMonth_OutOfRange");
        Assert.Contains(badYear.Errors!, e => e.Code == "SalaryPayment.PeriodYear_Required");
        Assert.Contains(badGross.Errors!, e => e.Code == "SalaryPayment.GrossAmount_Required");
        Assert.Contains(badNet.Errors!, e => e.Code == "SalaryPayment.NetAmount_Required");
    }

    [Fact]
    public void SalaryPayment_Create_RejectsEmptyTeacherId()
    {
        var result = SalaryPayment.Create(Guid.NewGuid(), Guid.Empty, 9, 2026, 100m, 100m);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "SalaryPayment.TeacherId_Required");
    }

    // ------------------------------------------------------------------
    // Transitions (F-02: Pending → Paid | Cancelled, terminal states)
    // ------------------------------------------------------------------

    [Fact]
    public void SalaryPayment_MarkPaid_FromPending_Succeeds_AndStampsStatusAndPaidAtTogether()
    {
        var payment = CreateValidPayment().Value;
        var paidAt = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

        var result = payment.MarkPaid(paidAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Paid, payment.Status);
        Assert.Equal(paidAt, payment.PaidAt);
    }

    [Fact]
    public void SalaryPayment_MarkPaid_FromPaid_Fails_AsDuplicate()
    {
        var payment = CreateValidPayment().Value;
        var firstPaidAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(payment.MarkPaid(firstPaidAt).IsSuccess);

        var retry = payment.MarkPaid(DateTime.UtcNow);

        Assert.False(retry.IsSuccess);
        Assert.Contains(retry.Errors!, e => e.Code == "SalaryPayment.Duplicate");
        Assert.Equal(SalaryPaymentStatus.Paid, payment.Status);
        Assert.Equal(firstPaidAt, payment.PaidAt); // unchanged
    }

    [Fact]
    public void SalaryPayment_MarkPaid_FromCancelled_IsRejected()
    {
        // Regression for H-02: a Cancelled payment used to be markable as Paid.
        var payment = CreateValidPayment().Value;
        Assert.True(payment.Cancel().IsSuccess);

        var result = payment.MarkPaid(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "SalaryPayment.InvalidStatus");
        Assert.Equal(SalaryPaymentStatus.Cancelled, payment.Status); // stays Cancelled
        Assert.Null(payment.PaidAt); // and never gets a paid timestamp
    }

    [Fact]
    public void SalaryPayment_Cancel_FromPending_Succeeds()
    {
        var payment = CreateValidPayment().Value;

        var result = payment.Cancel();

        Assert.True(result.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Cancelled, payment.Status);
        Assert.Null(payment.PaidAt);
    }

    [Fact]
    public void SalaryPayment_Cancel_FromPaid_IsRejected()
    {
        var payment = CreateValidPayment().Value;
        Assert.True(payment.MarkPaid(DateTime.UtcNow).IsSuccess);

        var result = payment.Cancel();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "SalaryPayment.InvalidStatus");
        Assert.Equal(SalaryPaymentStatus.Paid, payment.Status); // stays Paid
        Assert.NotNull(payment.PaidAt);
    }

    [Fact]
    public void SalaryPayment_Cancel_FromCancelled_RemainsNoOpSuccess_DocumentedAmbiguity()
    {
        // DOCUMENTED AMBIGUITY (not a new rule): the project defines no explicit
        // idempotency semantics for repeated Cancel. The pre-existing behavior —
        // a second Cancel on an already-Cancelled payment succeeds as a no-op — is
        // asserted here as-is, pending a product decision.
        var payment = CreateValidPayment().Value;
        Assert.True(payment.Cancel().IsSuccess);

        var repeat = payment.Cancel();

        Assert.True(repeat.IsSuccess);
        Assert.Equal(SalaryPaymentStatus.Cancelled, payment.Status);
        Assert.Null(payment.PaidAt);
    }

    // ------------------------------------------------------------------
    // Teacher soft-delete guards
    // ------------------------------------------------------------------

    [Fact]
    public void Teacher_SoftDelete_SetsInactive_AndBlocksFurtherMutations()
    {
        var teacher = CreateValidTeacher().Value;

        Assert.True(teacher.SoftDelete().IsSuccess);
        Assert.True(teacher.IsDeleted());
        Assert.Equal(TeacherStatus.Inactive, teacher.Status);

        Assert.False(teacher.SoftDelete().IsSuccess);
        Assert.Contains(teacher.SoftDelete().Errors!, e => e.Code == "Teacher.AlreadyDeleted");

        var update = teacher.Update(Guid.NewGuid(), "New Name", "01000000000", null, 3, TeacherStatus.Active);
        Assert.False(update.IsSuccess);
        Assert.Contains(update.Errors!, e => e.Code == "Teacher.AlreadyDeleted");

        var changeStatus = teacher.ChangeStatus(TeacherStatus.Active);
        Assert.False(changeStatus.IsSuccess);
        Assert.Contains(changeStatus.Errors!, e => e.Code == "Teacher.AlreadyDeleted");
    }
}
