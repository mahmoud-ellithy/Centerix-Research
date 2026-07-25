namespace Centerix.Application.Students.Lookups.Commands;

using FluentValidation;

public class CreateAcademicStageValidator : AbstractValidator<CreateAcademicStageCommand>
{
    public CreateAcademicStageValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.SortOrder)
            .InclusiveBetween((byte)0, (byte)255);
    }
}
