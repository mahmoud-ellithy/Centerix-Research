namespace Centerix.Application.Platform.Promotions.Commands;

using FluentValidation;

public class CalculateAndPersistOfferValidator : AbstractValidator<CalculateAndPersistOfferCommand>
{
    public CalculateAndPersistOfferValidator()
    {
        RuleFor(x => x.PlanId)
            .GreaterThan(0)
            .WithMessage("Plan ID must be greater than 0");

        RuleFor(x => x.DurationMonths)
            .GreaterThan(0)
            .WithMessage("Duration must be at least 1 month");

        When(x => x.Benefits is not null, () =>
        {
            RuleForEach(x => x.Benefits)
                .ChildRules(benefit =>
                {
                    benefit.RuleFor(b => b.Name)
                        .NotEmpty()
                        .MaximumLength(200)
                        .WithMessage("Benefit name is required");

                    benefit.RuleFor(b => b.ContractualValue)
                        .GreaterThanOrEqualTo(0)
                        .WithMessage("Benefit value cannot be negative");
                });
        });
    }
}

public class AcceptOfferValidator : AbstractValidator<AcceptOfferCommand>
{
    public AcceptOfferValidator()
    {
        RuleFor(x => x.OfferId)
            .NotEmpty()
            .WithMessage("Offer ID is required");
    }
}

public class CreateContractFromOfferValidator : AbstractValidator<CreateContractFromOfferCommand>
{
    public CreateContractFromOfferValidator()
    {
        RuleFor(x => x.OfferId)
            .NotEmpty()
            .WithMessage("Offer ID is required");

        RuleFor(x => x.ContractNumber)
            .NotEmpty()
            .MaximumLength(50)
            .WithMessage("Contract number is required and must not exceed 50 characters");
    }
}
