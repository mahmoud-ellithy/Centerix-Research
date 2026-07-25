namespace Centerix.Application.Students.Commands;

using FluentValidation;

public class UpdateAcademicYearValidator : AbstractValidator<UpdateAcademicYearCommand>
{
    public UpdateAcademicYearValidator()
    {
        RuleFor(x => x.Id)
            .GreaterThan(0);

        RuleFor(x => x.StageId)
            .GreaterThan(0);

        RuleFor(x => x.YearCode)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.YearName)
            .NotEmpty()
            .MaximumLength(200);
    }
}
