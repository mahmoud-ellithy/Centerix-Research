namespace Centerix.SecurityTests;

using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;

using Xunit;

/// <summary>
/// Phase 3 domain invariants for the education module (M-01):
/// Student, Branch, AcademicStage, AcademicYear. These exercise the static factories
/// and instance mutators only — no infrastructure. They are the contract that
/// handlers, controllers, and the SQL schema must satisfy.
/// </summary>
public class Phase3DomainTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // ------------------------------------------------------------------
    // Student
    // ------------------------------------------------------------------

    [Fact]
    public void Student_Create_Valid_ReturnsSuccess_AndPersistsTrimmedValues()
    {
        var result = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1,
            "  طالب  ", "  John Doe  ",
            new DateOnly(2010, 1, 1), Gender.Male, " 01000000000 ",
            "  QR-001  ", DiscountType.Percentage, 10m,
            StudentStatus.Active, Today);

        Assert.True(result.IsSuccess, string.Join(",", result.Errors?.Select(e => e.Code) ?? []));
        var s = result.Value;
        Assert.Equal("طالب", s.FullNameAr);
        Assert.Equal("John Doe", s.FullNameEn);
        Assert.Equal("01000000000", s.Phone);
        Assert.Equal("QR-001", s.QRCode);
        Assert.Equal(StudentStatus.Active, s.Status);
        Assert.False(s.IsDeleted());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Student_Create_RejectsBlankArabicName(string? name)
    {
        var result = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, name!, null,
            null, null, null, "QR", null, null, StudentStatus.Active, Today);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Student.FullNameAr_Required");
    }

    [Fact]
    public void Student_Create_RejectsEmptyBranchId()
    {
        var result = Student.Create(
            Guid.NewGuid(), Guid.Empty, 1, 1, "X", null,
            null, null, null, "QR", null, null, StudentStatus.Active, Today);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Student.BranchId_Required");
    }

    [Fact]
    public void Student_Create_RejectsNonPositiveStageOrYear()
    {
        var badStage = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 0, 1, "X", null, null, null, null, "QR", null, null, StudentStatus.Active, Today);
        var badYear = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 0, "X", null, null, null, null, "QR", null, null, StudentStatus.Active, Today);
        Assert.False(badStage.IsSuccess);
        Assert.False(badYear.IsSuccess);
        Assert.Contains(badStage.Errors!, e => e.Code == "Student.StageId_Required");
        Assert.Contains(badYear.Errors!, e => e.Code == "Student.YearId_Required");
    }

    [Fact]
    public void Student_Create_RejectsDateOfBirthInFuture()
    {
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        var result = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, "X", null, future, null, null, "QR", null, null, StudentStatus.Active, Today);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Student.DateOfBirth_Future");
    }

    [Fact]
    public void Student_Create_RejectsPercentageDiscountOutOfRange()
    {
        // Above 100 is rejected by the percentage-range rule.
        var tooHigh = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, "X", null, null, null, null, "QR",
            DiscountType.Percentage, 150m, StudentStatus.Active, Today);
        Assert.False(tooHigh.IsSuccess);
        Assert.Contains(tooHigh.Errors!, e => e.Code == "Student.Percentage_OutOfRange");

        // Negative is rejected by the generic non-negative rule (fires first).
        var negative = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, "X", null, null, null, null, "QR",
            DiscountType.Percentage, -1m, StudentStatus.Active, Today);
        Assert.False(negative.IsSuccess);
        Assert.Contains(negative.Errors!, e => e.Code == "Student.InvalidDiscountValue");
    }

    [Fact]
    public void Student_Create_RequiresQRCode()
    {
        var result = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, "X", null, null, null, null, "  ", null, null, StudentStatus.Active, Today);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Student.QRCode_Required");
    }

    [Fact]
    public void Student_Update_AppliesChangesAndPreservesQR()
    {
        var created = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, "A", null, null, null, null, "QR", null, null, StudentStatus.Active, Today).Value;

        var update = created.Update(
            Guid.NewGuid(), 2, 2, "B", "EN", null, Gender.Female, "010", DiscountType.Fixed, 50m, StudentStatus.Inactive);

        Assert.True(update.IsSuccess, string.Join(",", update.Errors?.Select(e => e.Code) ?? []));
        Assert.Equal("B", created.FullNameAr);
        Assert.Equal("EN", created.FullNameEn);
        Assert.Equal("010", created.Phone);
        Assert.Equal(StudentStatus.Inactive, created.Status);
        Assert.Equal("QR", created.QRCode); // QR is immutable on update
    }

    [Fact]
    public void Student_ChangeStatus_RejectsInvalidEnum()
    {
        var s = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, "A", null, null, null, null, "QR", null, null, StudentStatus.Active, Today).Value;
        Assert.False(s.ChangeStatus(unchecked((StudentStatus)999)).IsSuccess);
    }

    [Fact]
    public void Student_SoftDelete_FlipsStatusToInactive_AndIsIdempotentDenied()
    {
        var s = Student.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, "A", null, null, null, null, "QR", null, null, StudentStatus.Active, Today).Value;

        Assert.True(s.SoftDelete().IsSuccess);
        Assert.True(s.IsDeleted());
        Assert.Equal(StudentStatus.Inactive, s.Status);
        Assert.False(s.SoftDelete().IsSuccess);
        Assert.False(s.ChangeStatus(StudentStatus.Active).IsSuccess);
    }

    // ------------------------------------------------------------------
    // Branch
    // ------------------------------------------------------------------

    [Fact]
    public void Branch_Create_RequiresName_AndOptionallyValidatesPhone()
    {
        Assert.False(Branch.Create(Guid.NewGuid(), "").IsSuccess);
        Assert.False(Branch.Create(Guid.NewGuid(), "X", phone: "abc").IsSuccess);
        Assert.True(Branch.Create(Guid.NewGuid(), "Main", phone: "+201000000000").IsSuccess);
        Assert.True(Branch.Create(Guid.NewGuid(), "Main", phone: null).IsSuccess);
    }

    [Fact]
    public void Branch_Update_AndLifecycle()
    {
        var b = Branch.Create(Guid.NewGuid(), "Main", phone: "01000000000").Value;
        Assert.True(b.Update("HQ", null, null, null).IsSuccess);
        Assert.Equal("HQ", b.Name);
        Assert.True(b.Deactivate().IsSuccess);
        Assert.False(b.IsActive);
        Assert.True(b.Activate().IsSuccess);
        Assert.True(b.IsActive);

        Assert.True(b.SoftDelete().IsSuccess);
        Assert.True(b.IsDeleted());
        Assert.False(b.Update("X", null, null, null).IsSuccess); // already deleted
    }

    // ------------------------------------------------------------------
    // AcademicStage / AcademicYear (lookups)
    // ------------------------------------------------------------------

    [Fact]
    public void AcademicStage_Create_NormalizesCodeToUpper_AndRequiresFields()
    {
        Assert.False(AcademicStage.Create(0, "", "Name", 1).IsSuccess);
        Assert.False(AcademicStage.Create(0, "K", "", 1).IsSuccess);

        var ok = AcademicStage.Create(0, " primary ", " Primary ", 5).Value;
        Assert.Equal("PRIMARY", ok.Code);
        Assert.Equal("Primary", ok.DisplayName);
    }

    [Fact]
    public void AcademicYear_Create_NormalizesCodeToUpper_AndRequiresStage()
    {
        Assert.False(AcademicYear.Create(0, 0, "Y1", "Year 1").IsSuccess);
        Assert.False(AcademicYear.Create(0, 1, "", "Year 1").IsSuccess);
        Assert.False(AcademicYear.Create(0, 1, "Y1", "").IsSuccess);

        var ok = AcademicYear.Create(0, 1, " y1 ", " Year 1 ").Value;
        Assert.Equal("Y1", ok.YearCode);
        Assert.Equal("Year 1", ok.YearName);
    }

    [Fact]
    public void AcademicStage_Update_NormalizesCodeToUpper()
    {
        var s = AcademicStage.Create(0, "K1", "K1", 1).Value;
        Assert.True(s.Update(" k2 ", "K2", 2).IsSuccess);
        Assert.Equal("K2", s.Code);
        Assert.Equal((byte)2, s.SortOrder);
    }

    [Fact]
    public void AcademicYear_Update_PreservesStageId_WhenSupplied()
    {
        var y = AcademicYear.Create(0, 1, "Y1", "Year 1").Value;
        Assert.True(y.Update(2, "Y2", "Year 2").IsSuccess);
        Assert.Equal(2, y.StageId);
        Assert.Equal("Y2", y.YearCode);
        Assert.Equal("Year 2", y.YearName);
    }
}