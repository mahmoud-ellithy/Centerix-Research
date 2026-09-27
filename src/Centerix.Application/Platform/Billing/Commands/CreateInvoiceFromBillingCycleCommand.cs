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
/// COMMERCIAL INTEGRITY (Task 21 final closure):
/// - FULL-TERM cycles are identified by PERIOD IDENTITY
///   (PeriodStart == StartsAtUtc && PeriodEnd == BaseEndsAtUtc), never by duration equality —
///   a different cycle can coincide with the same number of months.
/// - For a full-term cycle, Invoice amounts are the authoritative Contract values:
///   Subtotal = GrossAmount, DiscountAmount = DiscountAmount, TotalAmount = ContractedAmount.
/// - For a partial cycle, amounts derive exclusively from the immutable Subscription snapshot
///   scaled by the billable calendar months of the cycle period (BillingCycle.GetBillableMonthsFor):
///   billable time is bounded by the paid term [StartsAtUtc, BaseEndsAtUtc], a later (renewal)
///   term bills its own period, and free time (pre-start or bonus months only) bills nothing.
/// - Money is rounded once, at invoice derivation, to the invoice storage precision
///   (decimal(18,2), MidpointRounding.AwayFromZero) so the stored invoice reconciles exactly:
///   TotalAmount = Subtotal − DiscountAmount + TaxAmount.
/// - BonusMonths are free entitlement only: time from BaseEndsAtUtc onwards (EffectiveEndsAtUtc)
///   is never counted as billable and can never increase an invoice.
/// </summary>
public record CreateInvoiceFromBillingCycleCommand(Guid BillingCycleId) : IRequest<Result<Created>>;

public class CreateInvoiceFromBillingCycleHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IRequestHandler<CreateInvoiceFromBillingCycleCommand, Result<Created>>
{
    /// <summary>
    /// Invoice storage precision is decimal(18,2) (2 decimal places — the Centerix monetary
    /// policy: every stored amount on the commercial chain is decimal(n,2)); rounding happens
    /// here, once, before the invoice is constructed, using the same midpoint rule as the
    /// promotion engine (MidpointRounding.AwayFromZero) so subtotal/discount/total reconcile
    /// exactly on store.
    /// </summary>
    private static decimal RoundMoney(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

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

        // ── Full-term identity (CRITICAL) ────────────────────────────────────────────────
        // A cycle is full-term ONLY when its period IS the paid term of this subscription
        // ([StartsAtUtc, BaseEndsAtUtc]). Comparing month counts is not an identity test:
        // a later 12-month cycle would otherwise masquerade as the contract's full term.
        var isFullTerm = contract is not null && billingCycle.IsFullTermFor(subscription);

        decimal subtotal;
        decimal discountAmount;
        decimal taxAmount = 0m; // Tax calculation will be added in a later task
        decimal totalAmount;

        if (isFullTerm)
        {
            // Full-term: authoritative Contract values — the historical commercial result is
            // used verbatim and is NEVER recomputed from current catalog data.
            // Contract guarantees ContractedAmount = GrossAmount − DiscountAmount, so the
            // invoice arithmetic identity holds at storage precision.
            subtotal = RoundMoney(contract!.GrossAmount);
            discountAmount = RoundMoney(contract.DiscountAmount);
            totalAmount = RoundMoney(contract.ContractedAmount);
        }
        else
        {
            // Partial: immutable Subscription snapshot only (no Plan/Promotion/PricingTier reads),
            // scaled by the billable calendar months of THIS period. Billable time is bounded by
            // the paid term [StartsAtUtc, BaseEndsAtUtc]: time before the start and bonus time are
            // never billed, a later (renewal) term is billed on its own period, and a cycle that
            // holds only free time is reported explicitly instead of becoming a one-month invoice.
            var billableMonths = billingCycle.GetBillableMonthsFor(subscription);
            if (!billableMonths.IsSuccess)
                return billableMonths.Errors!;

            var months = billableMonths.Value;

            // Display gross: list price × billable months.
            subtotal = RoundMoney(subscription.SnapshotPrice * months);
            // The snapshot discount only, applied once for the same number of months.
            discountAmount = RoundMoney((subscription.SnapshotPrice - subscription.SnapshotMonthlyCharge) * months);
            // Derived from the already-rounded components so the stored invoice reconciles
            // exactly: TotalAmount = Subtotal − DiscountAmount + TaxAmount.
            totalAmount = subtotal - discountAmount + taxAmount;
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
