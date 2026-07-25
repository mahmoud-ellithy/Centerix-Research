namespace Centerix.Domain.Students.Students;

using System.ComponentModel.DataAnnotations;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Lookups;

public class Student : AuditableEntity<Guid>
{
    public Guid BranchId { get; private set; }
    public int StageId { get; private set; }
    public int YearId { get; private set; }

    public string FullNameAr { get; private set; } = default!;
    public string FullNameEn { get; private set; } = default!;

    public DateOnly DateOfBirth { get; private set; }
    public Gender Gender { get; private set; }

    public string Phone { get; private set; } = default!;
    public string QRCode { get; private set; } = default!;

    public DiscountType DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }

    public StudentStatus Status { get; private set; }
    public DateOnly EnrolledAt { get; private set; }

    [Timestamp]
    public byte[]? RowVersion { get; internal set; }

    public Branch Branch { get; private set; } = default!;
    public AcademicStage Stage { get; private set; } = default!;
    public AcademicYear Year { get; private set; } = default!;

    private Student() { }

    private Student(
        Guid id,
        Guid branchId,
        int stageId,
        int yearId,
        string fullNameAr,
        string fullNameEn,
        DateOnly dateOfBirth,
        Gender gender,
        string phone,
        string qrCode,
        DiscountType discountType,
        decimal discountValue,
        StudentStatus status,
        DateOnly enrolledAt)
        : base(id)
    {
        BranchId = branchId;
        StageId = stageId;
        YearId = yearId;
        FullNameAr = fullNameAr;
        FullNameEn = fullNameEn;
        DateOfBirth = dateOfBirth;
        Gender = gender;
        Phone = phone;
        QRCode = qrCode;
        DiscountType = discountType;
        DiscountValue = discountValue;
        Status = status;
        EnrolledAt = enrolledAt;
    }

    public static Result<Student> Create(
        Guid id,
        Guid branchId,
        int stageId,
        int yearId,
        string fullNameAr,
        string fullNameEn,
        DateOnly dateOfBirth,
        Gender gender,
        string phone,
        string qrCode,
        DiscountType discountType,
        decimal discountValue,
        StudentStatus status,
        DateOnly enrolledAt)
    {
        var error = Validate(
            branchId, stageId, yearId, fullNameAr, fullNameEn,
            dateOfBirth, gender, qrCode, discountType, discountValue, status);

        if (error is not null)
            return error;

        return new Student(
            id,
            branchId,
            stageId,
            yearId,
            fullNameAr.Trim(),
            fullNameEn.Trim(),
            dateOfBirth,
            gender,
            phone.Trim(),
            qrCode.Trim(),
            discountType,
            discountValue,
            status,
            enrolledAt);
    }

    public Result<Updated> Update(
        Guid branchId,
        int stageId,
        int yearId,
        string fullNameAr,
        string fullNameEn,
        DateOnly dateOfBirth,
        Gender gender,
        string phone,
        DiscountType discountType,
        decimal discountValue,
        StudentStatus status)
    {
        if (IsDeleted())
            return StudentErrors.AlreadyDeleted;

        var error = Validate(
            branchId, stageId, yearId, fullNameAr, fullNameEn,
            dateOfBirth, gender, QRCode, discountType, discountValue, status);

        if (error is not null)
            return error;

        BranchId = branchId;
        StageId = stageId;
        YearId = yearId;
        FullNameAr = fullNameAr.Trim();
        FullNameEn = fullNameEn.Trim();
        DateOfBirth = dateOfBirth;
        Gender = gender;
        Phone = phone.Trim();
        DiscountType = discountType;
        DiscountValue = discountValue;
        Status = status;

        return Result.Updated;
    }

    public Result<Updated> ChangeStatus(StudentStatus newStatus)
    {
        if (IsDeleted())
            return StudentErrors.AlreadyDeleted;

        if (!Enum.IsDefined(newStatus))
            return StudentErrors.InvalidStatus;

        Status = newStatus;
        return Result.Updated;
    }

    public Result<Updated> SoftDelete()
    {
        if (IsDeleted())
            return StudentErrors.AlreadyDeleted;

        DeletedAtUtc = DateTimeOffset.UtcNow;
        // DeletedBy is stamped by AuditableEntityInterceptor from the current user name.
        Status = StudentStatus.Inactive;

        return Result.Updated;
    }

    private static Error? Validate(
        Guid branchId,
        int stageId,
        int yearId,
        string fullNameAr,
        string fullNameEn,
        DateOnly dateOfBirth,
        Gender gender,
        string qrCode,
        DiscountType discountType,
        decimal discountValue,
        StudentStatus status)
    {
        if (branchId == Guid.Empty)
            return StudentErrors.BranchIdRequired;

        if (stageId <= 0)
            return StudentErrors.StageIdRequired;

        if (yearId <= 0)
            return StudentErrors.YearIdRequired;

        if (string.IsNullOrWhiteSpace(fullNameAr) || fullNameAr.Length > 200)
            return StudentErrors.FullNameArRequired;

        if (string.IsNullOrWhiteSpace(fullNameEn) || fullNameEn.Length > 200)
            return StudentErrors.FullNameEnRequired;

        if (dateOfBirth == default)
            return StudentErrors.DateOfBirthRequired;

        if (dateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow))
            return StudentErrors.DateOfBirthInFuture;

        if (!Enum.IsDefined(gender))
            return StudentErrors.InvalidGender;

        if (string.IsNullOrWhiteSpace(qrCode))
            return StudentErrors.QRCodeRequired;

        if (qrCode.Length > 100)
            return StudentErrors.QRCodeTooLong;

        if (!Enum.IsDefined(discountType))
            return StudentErrors.InvalidDiscountType;

        if (discountValue < 0)
            return StudentErrors.InvalidDiscountValue;

        if (discountType == DiscountType.Percentage && (discountValue < 0 || discountValue > 100))
            return StudentErrors.PercentageOutOfRange;

        if (!Enum.IsDefined(status))
            return StudentErrors.InvalidStatus;

        return null;
    }
}
