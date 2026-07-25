namespace Centerix.Application.Students.Students.Commands;

using FluentValidation;

public class UpdateStudentValidator : AbstractValidator<UpdateStudentCommand>
{
    public UpdateStudentValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty();

        RuleFor(x => x.BranchId)
            .NotEmpty();

        RuleFor(x => x.StageId)
            .GreaterThan(0);

        RuleFor(x => x.YearId)
            .GreaterThan(0);

        RuleFor(x => x.FullNameAr)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.FullNameEn)
            .MaximumLength(200);

        RuleFor(x => x.Phone)
            .MaximumLength(20);

        RuleFor(x => x.DiscountValue)
            .GreaterThanOrEqualTo(0)
            .When(x => x.DiscountValue.HasValue);
    }
}
