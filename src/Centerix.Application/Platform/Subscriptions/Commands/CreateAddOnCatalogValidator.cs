namespace Centerix.Application.Platform.Subscriptions.Commands;

using FluentValidation;

public class CreateAddOnCatalogValidator : AbstractValidator<CreateAddOnCatalogCommand>
{
    public CreateAddOnCatalogValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.UnitType)
            .NotEmpty()
            .MaximumLength(50);

        RuleFor(x => x.UnitQuantity)
            .GreaterThan(0);

        RuleFor(x => x.BillingType)
            .InclusiveBetween((byte)0, (byte)2);
    }
}
