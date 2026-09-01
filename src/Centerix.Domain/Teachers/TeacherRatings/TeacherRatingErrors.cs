namespace Centerix.Domain.Teachers.TeacherRatings;

using Centerix.Domain.Common.Results;

public static class TeacherRatingErrors
{
    public static Error TeacherIdRequired =>
        Error.Validation("TeacherRating.TeacherId_Required", "Teacher is required");

    public static Error StudentIdRequired =>
        Error.Validation("TeacherRating.StudentId_Required", "Student is required");

    public static Error StarsRequired =>
        Error.Validation("TeacherRating.Stars_Required", "Stars rating is required");

    public static Error StarsOutOfRange =>
        Error.Validation("TeacherRating.Stars_OutOfRange", "Stars must be between 1 and 5");

    public static Error CommentTooLong =>
        Error.Validation("TeacherRating.Comment_TooLong", "Comment must be 500 characters or fewer");

    public static Error PeriodMonthRequired =>
        Error.Validation("TeacherRating.PeriodMonth_Required", "Period month is required");

    public static Error PeriodMonthOutOfRange =>
        Error.Validation("TeacherRating.PeriodMonth_OutOfRange", "Period month must be between 1 and 12");

    public static Error PeriodYearRequired =>
        Error.Validation("TeacherRating.PeriodYear_Required", "Period year is required");

    public static Error NotFound =>
        Error.NotFound("TeacherRating.NotFound", "Teacher rating was not found");
}