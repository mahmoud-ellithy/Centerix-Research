namespace Centerix.Application.Platform.Tenants.Commands;

using FluentValidation;

public class CreateTenantValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantValidator()
    {
        RuleFor(x => x.Slug)
            .NotEmpty()
            .MaximumLength(60);

        RuleFor(x => x.Subdomain)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Country)
            .NotEmpty()
            .Length(2);

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(3);

        RuleFor(x => x.Timezone)
            .NotEmpty();

        RuleFor(x => x.OwnerFirstName)
            .NotEmpty();

        RuleFor(x => x.OwnerLastName)
            .NotEmpty();

        RuleFor(x => x.OwnerEmail)
            .NotEmpty();
    }
}
