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
///
/// COMMERCIAL INTEGRITY (Task 21.2.1):
/// - TotalAmount is authoritative: for full-term cycles (BillingCycle covers entire paid term),
///   Invoice.TotalAmount MUST equal Contract.ContractedAmount (no rounding drift).
/// - For full-term cycles, all invoice amounts use Contract values (GrossAmount, DiscountAmount, ContractedAmount).
/// - For partial cycles, amounts are calculated from Subscription snapshot values.
/// - BonusMonths are excluded from the billing period; they are free entitlement only.
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
        var contract = subscription.Contract;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Calculate billing cycle duration in calendar months
        // Note: BillingCycle.PeriodEnd is based on BaseEndsAtUtc (paid term only, excluding bonus months)
        var cycleDurationMonths = ((billingCycle.PeriodEnd.Year - billingCycle.PeriodStart.Year) * 12) +
                                   (billingCycle.PeriodEnd.Month - billingCycle.PeriodStart.Month);

        if (cycleDurationMonths <= 0)
            cycleDurationMonths = 1; // minimum 1 month for partial cycles

        // Determine invoice amounts based on whether this is a full-term billing cycle.
        // For full-term cycles (covers entire paid term), use Contract as the authoritative source.
        // For partial cycles, calculate from Subscription snapshot values.
        decimal subtotal;
        decimal discountAmount;
        decimal taxAmount = 0m; // Tax calculation will be added in a later task
        decimal totalAmount;

        if (contract != null && cycleDurationMonths == subscription.DurationMonths)
        {
            // Full-term billing cycle: use Contract values as the authoritative source.
            // This ensures Invoice.TotalAmount == Contract.ContractedAmount exactly, with no rounding drift.
            subtotal = contract.GrossAmount;
            discountAmount = contract.DiscountAmount;
            totalAmount = contract.ContractedAmount;
        }
        else
        {
            // Partial-term billing cycle: calculate from Subscription snapshot.
            // For display: gross monthly price × duration
            subtotal = subscription.SnapshotPrice * cycleDurationMonths;
            // Discount: difference between list price and monthly charge, scaled by duration
            discountAmount = (subscription.SnapshotPrice - subscription.SnapshotMonthlyCharge) * cycleDurationMonths;
            // Total charge: monthly charge × duration
            // With decimal(18,6) precision, drift is minimized (e.g., 833.333333 × 12 = 9,999.999996).
            totalAmount = subscription.SnapshotMonthlyCharge * cycleDurationMonths;
        }

        var invoiceNumber = $"INV-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

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

        // Mark billing cycle as invoiced BEFORE tracking the invoice: BillingCycle.MarkInvoiced()
        // only accepts Draft, so an already-invoiced cycle must fail without leaving an
        // orphan Invoice attached to the change tracker (it would be persisted by the next
        // SaveChangesAsync on the same unit of work). This also guarantees at most one
        // invoice per BillingCycle (UX_Invoices_InvoiceNumber + Draft-only transition).
        var markInvoiced = billingCycle.MarkInvoiced();
        if (!markInvoiced.IsSuccess)
            return markInvoiced.Errors!;

        dbContext.Invoices.Add(invoiceResult.Value);

        // Stamp tenant ID before save (InMemory provider doesn't run interceptors)
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Created;
    }
}
