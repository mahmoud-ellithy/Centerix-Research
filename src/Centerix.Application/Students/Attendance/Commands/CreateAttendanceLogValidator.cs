namespace Centerix.Application.Students.Attendance.Commands;

using Centerix.Domain.Students.Enums;

using FluentValidation;

public class CreateAttendanceLogValidator : AbstractValidator<CreateAttendanceLogCommand>
{
    public CreateAttendanceLogValidator()
    {
        RuleFor(x => x.StudentId)
            .NotEmpty();

        RuleFor(x => x.GroupId)
            .NotEmpty();

        RuleFor(x => x.SessionDate)
            .NotEmpty();

        RuleFor(x => x.Status)
            .IsInEnum();

        RuleFor(x => x.CheckInTime)
            .NotNull()
            .When(x => x.Status == AttendanceStatus.Present || x.Status == AttendanceStatus.Late);
    }
}
