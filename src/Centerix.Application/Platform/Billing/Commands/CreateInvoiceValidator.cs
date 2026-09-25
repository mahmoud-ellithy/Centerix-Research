namespace Centerix.Application.Platform.Billing.Commands;

using FluentValidation;

public class CreateInvoiceValidator : AbstractValidator<CreateInvoiceCommand>
{
    public CreateInvoiceValidator()
    {
        // INV-02: ContractId is required for commercial traceability
        RuleFor(x => x.ContractId)
            .NotEmpty()
            .WithMessage("ContractId is required for commercial traceability");

        RuleFor(x => x.InvoiceNumber)
            .MaximumLength(50)
            .When(x => !string.IsNullOrWhiteSpace(x.InvoiceNumber));

        RuleFor(x => x.PeriodStart)
            .NotEmpty();

        RuleFor(x => x.PeriodEnd)
            .NotEmpty();

        // Amount validation: optional for server-validation, but must be >= 0 if provided
        RuleFor(x => x.Subtotal)
            .GreaterThanOrEqualTo(0)
            .When(x => x.Subtotal.HasValue);

        RuleFor(x => x.DiscountAmount)
            .GreaterThanOrEqualTo(0)
            .When(x => x.DiscountAmount.HasValue);

        RuleFor(x => x.TaxAmount)
            .GreaterThanOrEqualTo(0)
            .When(x => x.TaxAmount.HasValue);

        RuleFor(x => x.TotalAmount)
            .GreaterThanOrEqualTo(0)
            .When(x => x.TotalAmount.HasValue);

        // Optional SubscriptionId validation: if provided, must be a valid GUID
        RuleFor(x => x.SubscriptionId)
            .NotEmpty()
            .When(x => x.SubscriptionId.HasValue)
            .WithMessage("SubscriptionId must be a valid GUID if provided");
    }
}
