namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Subscriptions;

using MediatR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Creates an invoice from a BillingCycle, deriving amounts from the Subscription/Contract snapshot.
/// Client cannot submit arbitrary financial values; they are computed from the commercial snapshot.
/// </summary>
public record CreateInvoiceFromBillingCycleCommand(Guid BillingCycleId) : IRequest<Result<Created>>;

public class CreateInvoiceFromBillingCycleHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IRequestHandler<CreateInvoiceFromBillingCycleCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateInvoiceFromBillingCycleCommand request,
        CancellationToken cancellationToken)
    {
        var billingCycle = await dbContext.BillingCycles
            .Include(bc => bc.Subscription)
            .ThenInclude(s => s!.Contract)
            .FirstOrDefaultAsync(bc => bc.Id == request.BillingCycleId, cancellationToken);

        if (billingCycle is null)
            return Error.NotFound("BillingCycle.NotFound", $"BillingCycle '{request.BillingCycleId}' was not found.");

        if (billingCycle.Subscription is null)
            return Error.Conflict("Invoice.SubscriptionMissing", "BillingCycle is not linked to a Subscription.");

        var subscription = billingCycle.Subscription;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Calculate billing cycle duration in calendar months
        var cycleDurationMonths = ((billingCycle.PeriodEnd.Year - billingCycle.PeriodStart.Year) * 12) +
                                   (billingCycle.PeriodEnd.Month - billingCycle.PeriodStart.Month);

        if (cycleDurationMonths <= 0)
            cycleDurationMonths = 1; // minimum 1 month for partial cycles

        // Derive amounts from subscription snapshot (immutable commercial terms)
        var subtotal = subscription.SnapshotPrice * cycleDurationMonths;
        var discountAmount = 0m; // Contract-level discounts are already reflected in SnapshotPrice
        var taxAmount = 0m; // Tax calculation will be added in a later task
        var totalAmount = subtotal - discountAmount + taxAmount;

        var invoiceNumber = $"INV-{now:yyyyMMdd-HHmmss}";

        var invoiceResult = Invoice.Create(
            Guid.NewGuid(),
            invoiceNumber,
            DateOnly.FromDateTime(billingCycle.PeriodStart),
            DateOnly.FromDateTime(billingCycle.PeriodEnd),
            subtotal,
            discountAmount,
            taxAmount,
            totalAmount,
            subscription.ContractId,
            subscription.Id,
            billingCycle.Id);

        if (!invoiceResult.IsSuccess)
            return invoiceResult.Errors!;

        dbContext.Invoices.Add(invoiceResult.Value);

        // Stamp tenant ID before save (InMemory provider doesn't run interceptors)
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);

        // Mark billing cycle as invoiced
        var markInvoiced = billingCycle.MarkInvoiced();
        if (!markInvoiced.IsSuccess)
            return markInvoiced.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Created;
    }
}
