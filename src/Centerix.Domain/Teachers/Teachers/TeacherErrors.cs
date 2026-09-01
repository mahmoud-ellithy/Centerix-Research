namespace Centerix.Domain.Teachers.Teachers;

using Centerix.Domain.Common.Results;

public static class TeacherErrors
{
    public static Error UserIdRequired =>
        Error.Validation("Teacher.UserId_Required", "User is required");

    public static Error BranchIdRequired =>
        Error.Validation("Teacher.BranchId_Required", "Branch is required");

    public static Error FullNameRequired =>
        Error.Validation("Teacher.FullName_Required", "Full name is required");

    public static Error FullNameTooLong =>
        Error.Validation("Teacher.FullName_TooLong", "Full name must be 200 characters or fewer");

    public static Error PhoneRequired =>
        Error.Validation("Teacher.Phone_Required", "Phone is required");

    public static Error PhoneTooLong =>
        Error.Validation("Teacher.Phone_TooLong", "Phone must be 30 characters or fewer");

    public static Error QualificationTooLong =>
        Error.Validation("Teacher.Qualification_TooLong", "Qualification must be 200 characters or fewer");

    public static Error YearsExpOutOfRange =>
        Error.Validation("Teacher.YearsExp_OutOfRange", "Years of experience must be between 0 and 255");

    public static Error InvalidStatus =>
        Error.Validation("Teacher.InvalidStatus", "Invalid teacher status");

    public static Error NotFound =>
        Error.NotFound("Teacher.NotFound", "Teacher was not found");

    public static Error AlreadyDeleted =>
        Error.Conflict("Teacher.AlreadyDeleted", "Teacher is already deleted");

    public static Error DuplicateUser =>
        Error.Conflict("Teacher.DuplicateUser", "A teacher is already linked to this user");
}