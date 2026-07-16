namespace Centerix.Application.Platform.Commands;

using FluentValidation;

public class CreatePlanValidator : AbstractValidator<CreatePlanCommand>
{
    public CreatePlanValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.MonthlyPrice)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.MaxStudents)
            .GreaterThan(0);

        RuleFor(x => x.MaxUsers)
            .GreaterThan(0);

        RuleFor(x => x.MaxBranches)
            .GreaterThan(0);

        RuleFor(x => x.StorageGB)
            .GreaterThan(0);

        RuleFor(x => x.SMSQuota)
            .GreaterThanOrEqualTo(0);
    }
}