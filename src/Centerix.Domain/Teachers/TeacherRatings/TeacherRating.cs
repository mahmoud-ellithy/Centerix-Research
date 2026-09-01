namespace Centerix.Domain.Teachers.TeacherRatings;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Students;
using Centerix.Domain.Teachers.Teachers;

public class TeacherRating : AuditableEntity<Guid>
{
    public Guid TeacherId { get; private set; }
    public Guid StudentId { get; private set; }

    /// <summary>
    /// Optional reference to a not-yet-implemented Groups aggregate.
    /// Stored as plain Guid with NO FK constraint for now. The Groups entity
    /// (M-03 Schedule) should introduce the FK constraint.
    /// </summary>
    public Guid? GroupId { get; private set; }

    public byte Stars { get; private set; }
    public string? Comment { get; private set; }

    public byte PeriodMonth { get; private set; }
    public short PeriodYear { get; private set; }

    public Teacher Teacher { get; private set; } = default!;
    public Student Student { get; private set; } = default!;

    private TeacherRating() { }

    private TeacherRating(
        Guid id,
        Guid teacherId,
        Guid studentId,
        Guid? groupId,
        byte stars,
        string? comment,
        byte periodMonth,
        short periodYear)
        : base(id)
    {
        TeacherId = teacherId;
        StudentId = studentId;
        GroupId = groupId;
        Stars = stars;
        Comment = comment;
        PeriodMonth = periodMonth;
        PeriodYear = periodYear;
    }

    public static Result<TeacherRating> Create(
        Guid id,
        Guid teacherId,
        Guid studentId,
        Guid? groupId,
        byte stars,
        string? comment,
        byte periodMonth,
        short periodYear)
    {
        if (teacherId == Guid.Empty)
            return TeacherRatingErrors.TeacherIdRequired;

        if (studentId == Guid.Empty)
            return TeacherRatingErrors.StudentIdRequired;

        if (stars < 1 || stars > 5)
            return TeacherRatingErrors.StarsOutOfRange;

        if (!string.IsNullOrWhiteSpace(comment) && comment.Length > 500)
            return TeacherRatingErrors.CommentTooLong;

        if (periodMonth < 1 || periodMonth > 12)
            return TeacherRatingErrors.PeriodMonthOutOfRange;

        if (periodYear < 2000 || periodYear > 2100)
            return TeacherRatingErrors.PeriodYearRequired;

        return new TeacherRating(
            id,
            teacherId,
            studentId,
            groupId,
            stars,
            string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            periodMonth,
            periodYear);
    }
}