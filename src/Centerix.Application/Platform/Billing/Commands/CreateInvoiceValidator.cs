namespace Centerix.Application.Platform.Billing.Commands;

using FluentValidation;

public class CreateInvoiceValidator : AbstractValidator<CreateInvoiceCommand>
{
    public CreateInvoiceValidator()
    {
        RuleFor(x => x.InvoiceNumber)
            .MaximumLength(50)
            .When(x => !string.IsNullOrWhiteSpace(x.InvoiceNumber));

        RuleFor(x => x.PeriodStart)
            .NotEmpty();

        RuleFor(x => x.PeriodEnd)
            .NotEmpty();

        RuleFor(x => x.Subtotal)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.DiscountAmount)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.TaxAmount)
            .GreaterThanOrEqualTo(0);

        RuleFor(x => x.TotalAmount)
            .GreaterThanOrEqualTo(0);
    }
}
