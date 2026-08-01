namespace Centerix.Application.Platform.Staff.Commands;

using FluentValidation;

public class CreatePlatformUserValidator : AbstractValidator<CreatePlatformUserCommand>
{
    public CreatePlatformUserValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(200);

        RuleFor(x => x.FullName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8);
    }
}
