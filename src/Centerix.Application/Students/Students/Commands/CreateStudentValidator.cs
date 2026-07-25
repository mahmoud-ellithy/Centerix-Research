namespace Centerix.Application.Students.Students.Commands;

using FluentValidation;

public class CreateStudentValidator : AbstractValidator<CreateStudentCommand>
{
    public CreateStudentValidator()
    {
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

        RuleFor(x => x.QRCode)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.DiscountValue)
            .GreaterThanOrEqualTo(0)
            .When(x => x.DiscountValue.HasValue);

        RuleFor(x => x.EnrolledAt)
            .NotEmpty();
    }
}
