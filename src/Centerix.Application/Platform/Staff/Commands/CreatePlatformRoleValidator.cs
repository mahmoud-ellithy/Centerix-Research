namespace Centerix.Application.Platform.Staff.Commands;

using FluentValidation;

public class CreatePlatformRoleValidator : AbstractValidator<CreatePlatformRoleCommand>
{
    public CreatePlatformRoleValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .MaximumLength(200);
    }
}
