namespace Centerix.Application.Platform.Promotions.Commands;

using FluentValidation;

public class CreatePromotionValidator : AbstractValidator<CreatePromotionCommand>
{
    public CreatePromotionValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.PlanId)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.DurationMonths)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.StartsAtUtc)
            .NotEmpty();

        RuleFor(x => x.EndsAtUtc)
            .NotEmpty();

        RuleFor(x => x.StartsAtUtc)
            .LessThan(x => x.EndsAtUtc)
            .WithMessage("Start date must be before end date");

        When(x => x.Type == Domain.Platform.Promotions.Enums.PromotionType.PercentageDiscount, () =>
        {
            RuleFor(x => x.Percentage)
                .NotNull()
                .InclusiveBetween(0.01m, 100m);
        });

        When(x => x.Type == Domain.Platform.Promotions.Enums.PromotionType.FixedAmountDiscount, () =>
        {
            RuleFor(x => x.FixedAmount)
                .NotNull()
                .GreaterThan(0);
        });

        When(x => x.Type == Domain.Platform.Promotions.Enums.PromotionType.PromotionalPrice, () =>
        {
            RuleFor(x => x.PromotionalPrice)
                .NotNull()
                .GreaterThanOrEqualTo(0);
        });

        When(x => x.Type == Domain.Platform.Promotions.Enums.PromotionType.PayForXMonths, () =>
        {
            RuleFor(x => x.ChargedMonths)
                .NotNull()
                .GreaterThan(0);
        });
    }
}

public class UpdatePromotionValidator : AbstractValidator<UpdatePromotionCommand>
{
    public UpdatePromotionValidator()
    {
        RuleFor(x => x.Id)
            .GreaterThan(0);

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.PlanId)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.DurationMonths)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.StartsAtUtc)
            .NotEmpty();

        RuleFor(x => x.EndsAtUtc)
            .NotEmpty();

        RuleFor(x => x.StartsAtUtc)
            .LessThan(x => x.EndsAtUtc)
            .WithMessage("Start date must be before end date");

        When(x => x.Type == Domain.Platform.Promotions.Enums.PromotionType.PercentageDiscount, () =>
        {
            RuleFor(x => x.Percentage)
                .NotNull()
                .InclusiveBetween(0.01m, 100m);
        });

        When(x => x.Type == Domain.Platform.Promotions.Enums.PromotionType.FixedAmountDiscount, () =>
        {
            RuleFor(x => x.FixedAmount)
                .NotNull()
                .GreaterThan(0);
        });

        When(x => x.Type == Domain.Platform.Promotions.Enums.PromotionType.PromotionalPrice, () =>
        {
            RuleFor(x => x.PromotionalPrice)
                .NotNull()
                .GreaterThanOrEqualTo(0);
        });

        When(x => x.Type == Domain.Platform.Promotions.Enums.PromotionType.PayForXMonths, () =>
        {
            RuleFor(x => x.ChargedMonths)
                .NotNull()
                .GreaterThan(0);
        });
    }
}
