namespace Centerix.Application.Students.Commands;

using FluentValidation;

public class CreateAcademicYearValidator : AbstractValidator<CreateAcademicYearCommand>
{
    public CreateAcademicYearValidator()
    {
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
