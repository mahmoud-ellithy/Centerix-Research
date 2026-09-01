namespace Centerix.Domain.Teachers.Teachers;

using System.ComponentModel.DataAnnotations;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Teachers.Enums;

public class Teacher : SoftDeletableEntity<Guid>
{
    public string UserId { get; private set; } = default!;
    public Guid BranchId { get; private set; }

    public string FullName { get; private set; } = default!;
    public string Phone { get; private set; } = default!;
    public string? Qualification { get; private set; }
    public byte? YearsExp { get; private set; }

    public TeacherStatus Status { get; private set; }
    public DateOnly JoinedAt { get; private set; }

    [Timestamp]
    public byte[]? RowVersion { get; internal set; }

    public Branch Branch { get; private set; } = default!;

    private Teacher() { }

    private Teacher(
        Guid id,
        string userId,
        Guid branchId,
        string fullName,
        string phone,
        string? qualification,
        byte? yearsExp,
        TeacherStatus status,
        DateOnly joinedAt)
        : base(id)
    {
        UserId = userId;
        BranchId = branchId;
        FullName = fullName;
        Phone = phone;
        Qualification = qualification;
        YearsExp = yearsExp;
        Status = status;
        JoinedAt = joinedAt;
    }

    public static Result<Teacher> Create(
        Guid id,
        string userId,
        Guid branchId,
        string fullName,
        string phone,
        string? qualification,
        byte? yearsExp,
        TeacherStatus status,
        DateOnly joinedAt)
    {
        var error = Validate(branchId, fullName, phone, qualification, yearsExp, status);
        if (error is not null)
            return error;

        if (string.IsNullOrWhiteSpace(userId))
            return TeacherErrors.UserIdRequired;

        return new Teacher(
            id,
            userId.Trim(),
            branchId,
            fullName.Trim(),
            phone.Trim(),
            string.IsNullOrWhiteSpace(qualification) ? null : qualification.Trim(),
            yearsExp,
            status,
            joinedAt);
    }

    public Result<Updated> Update(
        Guid branchId,
        string fullName,
        string phone,
        string? qualification,
        byte? yearsExp,
        TeacherStatus status)
    {
        if (IsDeleted())
            return TeacherErrors.AlreadyDeleted;

        var error = Validate(branchId, fullName, phone, qualification, yearsExp, status);
        if (error is not null)
            return error;

        BranchId = branchId;
        FullName = fullName.Trim();
        Phone = phone.Trim();
        Qualification = string.IsNullOrWhiteSpace(qualification) ? null : qualification.Trim();
        YearsExp = yearsExp;
        Status = status;

        return Result.Updated;
    }

    public Result<Updated> ChangeStatus(TeacherStatus newStatus)
    {
        if (IsDeleted())
            return TeacherErrors.AlreadyDeleted;

        if (!Enum.IsDefined(newStatus))
            return TeacherErrors.InvalidStatus;

        Status = newStatus;
        return Result.Updated;
    }

    public Result<Updated> SoftDelete()
    {
        if (IsDeleted())
            return TeacherErrors.AlreadyDeleted;

        DeletedAtUtc = DateTimeOffset.UtcNow;
        Status = TeacherStatus.Inactive;

        return Result.Updated;
    }

    private static Error? Validate(
        Guid branchId,
        string fullName,
        string phone,
        string? qualification,
        byte? yearsExp,
        TeacherStatus status)
    {
        if (branchId == Guid.Empty)
            return TeacherErrors.BranchIdRequired;

        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 200)
            return TeacherErrors.FullNameRequired;

        if (string.IsNullOrWhiteSpace(phone))
            return TeacherErrors.PhoneRequired;

        if (phone.Length > 30)
            return TeacherErrors.PhoneTooLong;

        if (!string.IsNullOrWhiteSpace(qualification) && qualification.Length > 200)
            return TeacherErrors.QualificationTooLong;

        if (yearsExp.HasValue && yearsExp.Value > 100)
            return TeacherErrors.YearsExpOutOfRange;

        if (!Enum.IsDefined(status))
            return TeacherErrors.InvalidStatus;

        return null;
    }
}