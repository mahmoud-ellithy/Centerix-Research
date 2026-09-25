namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Subscriptions;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record CreateInvoiceCommand(
    string? InvoiceNumber,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    Guid ContractId,
    Guid? SubscriptionId = null,
    decimal? Subtotal = null,
    decimal? DiscountAmount = null,
    decimal? TaxAmount = null,
    decimal? TotalAmount = null) : IRequest<Result<Created>>;

public class CreateInvoiceHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<CreateInvoiceCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateInvoiceCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Load Contract with tenant verification (INV-02: commercial traceability)
        var contract = await dbContext.Contracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ContractId, cancellationToken);

        if (contract is null)
            return InvoiceErrors.ContractNotFound;

        // 2. Verify tenant ownership (INV-02: tenant isolation)
        if (contract.TenantId != currentTenant.TenantId)
            return InvoiceErrors.ContractNotOwnedByTenant;

        // 3. Verify Subscription/Contract relationship if SubscriptionId provided (INV-02)
        Guid? validatedSubscriptionId = null;
        if (request.SubscriptionId.HasValue)
        {
            var subscription = await dbContext.TenantPlans
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == request.SubscriptionId.Value, cancellationToken);

            if (subscription is null)
                return TenantPlanErrors.PlanNotFound;

            // Verify subscription belongs to this contract
            if (subscription.ContractId != request.ContractId)
                return InvoiceErrors.SubscriptionContractMismatch;

            // Verify subscription belongs to this tenant
            if (subscription.TenantId != currentTenant.TenantId)
                return InvoiceErrors.ContractNotOwnedByTenant;

            validatedSubscriptionId = request.SubscriptionId;
        }

        // 4. Generate invoice number
        var invoiceNumber = string.IsNullOrWhiteSpace(request.InvoiceNumber)
            ? $"INV-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
            : request.InvoiceNumber;

        // 5. Check for duplicate invoice number within tenant scope (INV-03)
        var duplicateExists = await dbContext.Invoices
            .AnyAsync(i => i.TenantId == currentTenant.TenantId && i.InvoiceNumber == invoiceNumber, cancellationToken);
        if (duplicateExists)
            return InvoiceErrors.DuplicateInvoiceNumber;

        // 6. Derive authoritative amounts from Contract (INV-01: server-authoritative)
        // Commercial invariant: ContractedAmount = GrossAmount - DiscountAmount
        // Invoice.TotalAmount MUST equal Contract.ContractedAmount (the final agreed amount)
        var subtotal = contract.GrossAmount;
        var discountAmount = contract.DiscountAmount;
        var taxAmount = 0m; // Tax calculation will be added in a later task
        var totalAmount = contract.ContractedAmount; // = GrossAmount - DiscountAmount

        // 7. Validate client-supplied amounts match server-derived (if provided)
        // This prevents tampering while allowing optional submission for validation
        if (request.Subtotal.HasValue && request.Subtotal.Value != subtotal)
            return InvoiceErrors.ClientAmountMismatch;

        if (request.DiscountAmount.HasValue && request.DiscountAmount.Value != discountAmount)
            return InvoiceErrors.ClientAmountMismatch;

        if (request.TaxAmount.HasValue && request.TaxAmount.Value != taxAmount)
            return InvoiceErrors.ClientAmountMismatch;

        if (request.TotalAmount.HasValue && request.TotalAmount.Value != totalAmount)
            return InvoiceErrors.ClientAmountMismatch;

        // 8. Create invoice with server-derived amounts
        var invoiceResult = Invoice.Create(
            Guid.NewGuid(),
            invoiceNumber,
            request.PeriodStart,
            request.PeriodEnd,
            subtotal,
            discountAmount,
            taxAmount,
            totalAmount,
            request.ContractId,
            validatedSubscriptionId,
            billingCycleId: null);

        if (!invoiceResult.IsSuccess)
            return invoiceResult.Errors!;

        dbContext.Invoices.Add(invoiceResult.Value);

        // Stamp tenant ID before save (InMemory provider doesn't run interceptors)
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);

        await dbContext.SaveChangesAsync(cancellationToken);

        // 9. Audit log
        await auditWriter.WriteAsync(
            action: "Invoice.Create",
            entityType: nameof(Invoice),
            entityId: invoiceResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                invoiceResult.Value.InvoiceNumber,
                invoiceResult.Value.PeriodStart,
                invoiceResult.Value.PeriodEnd,
                invoiceResult.Value.Subtotal,
                invoiceResult.Value.DiscountAmount,
                invoiceResult.Value.TaxAmount,
                invoiceResult.Value.TotalAmount,
                invoiceResult.Value.Status,
                ContractId = request.ContractId,
                SubscriptionId = validatedSubscriptionId
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
