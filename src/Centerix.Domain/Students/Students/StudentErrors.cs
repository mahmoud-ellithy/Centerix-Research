namespace Centerix.Domain.Students.Students;

using Centerix.Domain.Common.Results;

public static class StudentErrors
{
    public static Error BranchIdRequired =>
        Error.Validation("Student.BranchId_Required", "Branch is required");

    public static Error StageIdRequired =>
        Error.Validation("Student.StageId_Required", "Academic stage is required");

    public static Error YearIdRequired =>
        Error.Validation("Student.YearId_Required", "Academic year is required");

    public static Error FullNameArRequired =>
        Error.Validation("Student.FullNameAr_Required", "Arabic full name is required");

    public static Error FullNameEnRequired =>
        Error.Validation("Student.FullNameEn_Required", "English full name is required");

    public static Error DateOfBirthRequired =>
        Error.Validation("Student.DateOfBirth_Required", "Date of birth is required");

    public static Error DateOfBirthInFuture =>
        Error.Validation("Student.DateOfBirth_Future", "Date of birth cannot be in the future");

    public static Error InvalidGender =>
        Error.Validation("Student.InvalidGender", "Invalid gender value");

    public static Error InvalidDiscountType =>
        Error.Validation("Student.InvalidDiscountType", "Invalid discount type");

    public static Error InvalidDiscountValue =>
        Error.Validation("Student.InvalidDiscountValue", "Discount value must be non-negative");

    public static Error PercentageOutOfRange =>
        Error.Validation("Student.Percentage_OutOfRange", "Percentage discount must be between 0 and 100");

    public static Error InvalidStatus =>
        Error.Validation("Student.InvalidStatus", "Invalid student status");

    public static Error QRCodeRequired =>
        Error.Validation("Student.QRCode_Required", "QR code is required");

    public static Error QRCodeTooLong =>
        Error.Validation("Student.QRCode_TooLong", "QR code cannot exceed 100 characters");

    public static Error NotFound =>
        Error.NotFound("Student.NotFound", "Student was not found");

    public static Error AlreadyDeleted =>
        Error.Conflict("Student.AlreadyDeleted", "Student is already deleted");
}
