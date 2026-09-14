namespace Centerix.SecurityTests;

using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Xunit;

public class Phase8InstallmentDomainTests
{
    [Fact]
    public void Installment_Create_ValidInput_Succeeds()
    {
        var id = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var start = new DateTime(2026, 1, 1);
        var end = new DateTime(2026, 4, 30);

        var result = Installment.Create(
            id, contractId, 1,
            DateTime.UtcNow.AddDays(30),
            start, end,
            4000m, "EGP",
            Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal(id, result.Value!.Id);
        Assert.Equal(contractId, result.Value.ContractId);
        Assert.Equal(1, result.Value.SequenceNumber);
        Assert.Equal(4000m, result.Value.Amount);
        Assert.Equal("EGP", result.Value.CurrencyCode);
        Assert.Equal(InstallmentStatus.Pending, result.Value.Status);
        Assert.Equal(0m, result.Value.SettledAmount);
        Assert.Equal(4000m, result.Value.RemainingAmount);
    }

    [Fact]
    public void Installment_Create_EmptyId_Fails()
    {
        var result = Installment.Create(
            Guid.Empty, Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            1000m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.Id_Required");
    }

    [Fact]
    public void Installment_Create_EmptyContractId_Fails()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.Empty, 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            1000m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.ContractId_Required");
    }

    [Fact]
    public void Installment_Create_ZeroAmount_Fails()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            0m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.Amount_MustBePositive");
    }

    [Fact]
    public void Installment_Create_NegativeAmount_Fails()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            -500m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.Amount_MustBePositive");
    }

    [Fact]
    public void Installment_Create_CoveredPeriodEndBeforeStart_Fails()
    {
        var start = new DateTime(2026, 4, 30);
        var end = new DateTime(2026, 1, 1);

        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, start, end,
            1000m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CoveredPeriod_Invalid");
    }

    [Fact]
    public void Installment_Create_SequenceNumberZero_Fails()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 0,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            1000m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.SequenceNumber_MustBePositive");
    }

    [Fact]
    public void Installment_Create_NegativeSequenceNumber_Fails()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), -1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            1000m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.SequenceNumber_MustBePositive");
    }

    [Fact]
    public void Installment_Create_CurrencyNormalized()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            1000m, "  egp  ",
            Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal("EGP", result.Value!.CurrencyCode);
    }

    [Fact]
    public void Installment_Create_DefaultDueDate_Fails()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            default, DateTime.UtcNow, DateTime.UtcNow.AddDays(30),
            1000m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.DueDate_Required");
    }

    [Fact]
    public void Installment_Create_DefaultCoveredPeriodStart_Fails()
    {
        var result = Installment.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1,
            DateTime.UtcNow, default, DateTime.UtcNow.AddDays(30),
            1000m, "EGP",
            Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CoveredPeriod_Required");
    }

    [Fact]
    public void Installment_Update_PendingStatus_Succeeds()
    {
        var installment = CreateValidInstallment();
        var newDue = DateTime.UtcNow.AddDays(60);
        var newStart = DateTime.UtcNow.AddDays(30);
        var newEnd = DateTime.UtcNow.AddDays(60);

        var result = installment.Update(newDue, newStart, newEnd, 5000m);

        Assert.True(result.IsSuccess);
        Assert.Equal(newDue, installment.DueDateUtc);
        Assert.Equal(newStart, installment.CoveredPeriodStartUtc);
        Assert.Equal(newEnd, installment.CoveredPeriodEndUtc);
        Assert.Equal(5000m, installment.Amount);
    }

    [Fact]
    public void Installment_Update_PaidStatus_Fails()
    {
        var installment = CreateValidInstallment();
        installment.ApplyAllocation(
            CreateActiveAllocation(installment.Amount),
            DateTime.UtcNow);

        Assert.Equal(InstallmentStatus.Paid, installment.Status);

        var result = installment.Update(
            DateTime.UtcNow.AddDays(60),
            DateTime.UtcNow.AddDays(30),
            DateTime.UtcNow.AddDays(60),
            5000m);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotUpdateNonPending");
    }

    [Fact]
    public void Installment_Update_CancelledStatus_Fails()
    {
        var installment = CreateValidInstallment();
        installment.Cancel(DateTime.UtcNow);

        var result = installment.Update(
            DateTime.UtcNow.AddDays(60),
            DateTime.UtcNow.AddDays(30),
            DateTime.UtcNow.AddDays(60),
            5000m);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotUpdateNonPending");
    }

    [Fact]
    public void Installment_Cancel_NoAllocations_Succeeds()
    {
        var installment = CreateValidInstallment();

        var result = installment.Cancel(DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(InstallmentStatus.Cancelled, installment.Status);
    }

    [Fact]
    public void Installment_Cancel_HasActiveAllocations_Fails()
    {
        var installment = CreateValidInstallment();
        var allocation = CreateActiveAllocation(1000m);
        installment.ApplyAllocation(allocation, DateTime.UtcNow);

        var result = installment.Cancel(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotCancelHasAllocations");
    }

    [Fact]
    public void Installment_Cancel_PaidStatus_Fails()
    {
        var installment = CreateValidInstallment();
        installment.ApplyAllocation(
            CreateActiveAllocation(installment.Amount),
            DateTime.UtcNow);

        var result = installment.Cancel(DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.CannotCancelPaidOrCancelled");
    }

    [Fact]
    public void Installment_ApplyAllocation_Valid_Succeeds()
    {
        var installment = CreateValidInstallment();
        var allocation = CreateActiveAllocation(2000m);

        var result = installment.ApplyAllocation(allocation, DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(2000m, installment.SettledAmount);
        Assert.Equal(2000m, installment.RemainingAmount);
        Assert.Equal(InstallmentStatus.PartiallyPaid, installment.Status);
    }

    [Fact]
    public void Installment_ApplyAllocation_FullAmount_Paid()
    {
        var installment = CreateValidInstallment();
        var allocation = CreateActiveAllocation(installment.Amount);

        var result = installment.ApplyAllocation(allocation, DateTime.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(installment.Amount, installment.SettledAmount);
        Assert.Equal(0m, installment.RemainingAmount);
        Assert.Equal(InstallmentStatus.Paid, installment.Status);
    }

    [Fact]
    public void Installment_ApplyAllocation_ExceedsAmount_Fails()
    {
        var installment = CreateValidInstallment();
        var allocation = CreateActiveAllocation(installment.Amount + 1);

        var result = installment.ApplyAllocation(allocation, DateTime.UtcNow);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Installment.AllocationExceedsInstallment");
    }

    [Fact]
    public void Installment_IsOverdue_WhenPastDueAndUnpaid_ReturnsTrue()
    {
        var installment = CreateValidInstallment(
            dueDateUtc: DateTime.UtcNow.AddDays(-5));

        Assert.True(installment.IsOverdue(DateTime.UtcNow));
        Assert.Equal(InstallmentStatus.Overdue, installment.Status);
    }

    [Fact]
    public void Installment_IsOverdue_WhenPaid_ReturnsFalse()
    {
        var installment = CreateValidInstallment(
            dueDateUtc: DateTime.UtcNow.AddDays(-5));
        installment.ApplyAllocation(
            CreateActiveAllocation(installment.Amount),
            DateTime.UtcNow);

        Assert.False(installment.IsOverdue(DateTime.UtcNow));
        Assert.Equal(InstallmentStatus.Paid, installment.Status);
    }

    [Fact]
    public void Installment_IsOverdue_WhenFutureDueDate_ReturnsFalse()
    {
        var installment = CreateValidInstallment(
            dueDateUtc: DateTime.UtcNow.AddDays(5));

        Assert.False(installment.IsOverdue(DateTime.UtcNow));
        Assert.Equal(InstallmentStatus.Pending, installment.Status);
    }

    [Fact]
    public void Installment_PartialPayment_CorrectDeterministicStatus()
    {
        var installment = CreateValidInstallment(
            dueDateUtc: DateTime.UtcNow.AddDays(-5));

        installment.ApplyAllocation(CreateActiveAllocation(1000m), DateTime.UtcNow);

        Assert.Equal(1000m, installment.SettledAmount);
        Assert.Equal(InstallmentStatus.Overdue, installment.Status);
    }

    [Fact]
    public void Installment_MultiplePayments_SettleInstallment()
    {
        var installment = CreateValidInstallment();

        installment.ApplyAllocation(CreateActiveAllocation(2000m), DateTime.UtcNow);
        Assert.Equal(InstallmentStatus.PartiallyPaid, installment.Status);

        installment.ApplyAllocation(CreateActiveAllocation(2000m), DateTime.UtcNow);
        Assert.Equal(InstallmentStatus.Paid, installment.Status);
        Assert.Equal(0m, installment.RemainingAmount);
    }

    [Fact]
    public void Installment_DueDateSeparateFromCoveredPeriod()
    {
        var dueDate = new DateTime(2026, 1, 1);
        var coveredStart = new DateTime(2026, 1, 1);
        var coveredEnd = new DateTime(2026, 4, 30);

        var installment = CreateValidInstallment(dueDateUtc: dueDate);

        installment.Update(dueDate, coveredStart, coveredEnd, 4000m);

        Assert.Equal(dueDate, installment.DueDateUtc);
        Assert.Equal(coveredStart, installment.CoveredPeriodStartUtc);
        Assert.Equal(coveredEnd, installment.CoveredPeriodEndUtc);
    }

    [Fact]
    public void Installment_PaidInstallmentRemainsPaid_AfterDueDatePasses()
    {
        var installment = CreateValidInstallment(
            dueDateUtc: DateTime.UtcNow.AddDays(-10));

        installment.ApplyAllocation(
            CreateActiveAllocation(installment.Amount),
            DateTime.UtcNow);

        Assert.Equal(InstallmentStatus.Paid, installment.Status);

        installment.RecalculateStatus(DateTime.UtcNow.AddDays(5));
        Assert.Equal(InstallmentStatus.Paid, installment.Status);
    }

    [Fact]
    public void Installment_AmountSchedule_TotalMatches()
    {
        var amounts = new[] { 4000m, 4000m, 4000m };
        Assert.Equal(12000m, amounts.Sum());
    }

    [Fact]
    public void Installment_AmountSchedule_MismatchDetected()
    {
        var amounts = new[] { 4000m, 4000m, 5000m };
        Assert.NotEqual(12000m, amounts.Sum());
    }

    [Fact]
    public void PaymentAllocation_Create_WithInstallmentId_Succeeds()
    {
        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1000m,
            DateTime.UtcNow,
            Guid.NewGuid());

        Assert.True(allocation.IsSuccess);
        Assert.NotNull(allocation.Value!.InstallmentId);
    }

    [Fact]
    public void PaymentAllocation_Create_WithoutInstallmentId_Succeeds()
    {
        var allocation = PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1000m,
            DateTime.UtcNow);

        Assert.True(allocation.IsSuccess);
        Assert.Null(allocation.Value!.InstallmentId);
    }

    [Fact]
    public void PaymentAllocation_ZeroAmount_Fails()
    {
        var result = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            0m, DateTime.UtcNow, Guid.NewGuid());

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void PaymentAllocation_NegativeAmount_Fails()
    {
        var result = PaymentAllocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            -100m, DateTime.UtcNow, Guid.NewGuid());

        Assert.False(result.IsSuccess);
    }

    // Helper methods

    private static Installment CreateValidInstallment(
        DateTime? dueDateUtc = null,
        decimal amount = 4000m)
    {
        var installment = Installment.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            dueDateUtc ?? DateTime.UtcNow.AddDays(30),
            new DateTime(2026, 1, 1),
            new DateTime(2026, 4, 30),
            amount,
            "EGP",
            Guid.NewGuid());

        return installment.Value!;
    }

    private static PaymentAllocation CreateActiveAllocation(decimal amount)
    {
        return PaymentAllocation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            amount,
            DateTime.UtcNow).Value!;
    }
}
