namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Payments;

using MediatR;

public record AllocatePaymentCommand(Guid PaymentId, Guid InvoiceId, decimal AllocatedAmount) : IRequest<Result<Updated>>;

public class AllocatePaymentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<AllocatePaymentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        AllocatePaymentCommand request,
        CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments.FindAsync([request.PaymentId], cancellationToken: cancellationToken);
        if (payment is null)
        {
            return PaymentErrors.NotFound;
        }

        // Payment must be completed before allocation
        if (!payment.IsCompleted)
        {
            return PaymentErrors.CannotAllocatePendingPayment;
        }

        var invoice = await dbContext.Invoices.FindAsync([request.InvoiceId], cancellationToken: cancellationToken);
        if (invoice is null)
        {
            return PaymentErrors.InvoiceNotFound;
        }

        // Tenant isolation check
        if (payment.TenantId != invoice.TenantId)
        {
            return PaymentErrors.CrossTenantAllocation;
        }

        // Check allocation doesn't exceed payment amount
        var currentAllocated = payment.GetAllocatedAmount();
        if (currentAllocated + request.AllocatedAmount > payment.Amount)
        {
            return PaymentErrors.AllocationExceedsPayment;
        }

        // Check allocation doesn't exceed invoice remaining amount
        var invoiceRemaining = invoice.GetRemainingAmount();
        if (request.AllocatedAmount > invoiceRemaining)
        {
            return PaymentErrors.AllocationExceedsInvoiceRemaining;
        }

        var allocationResult = PaymentAllocation.Create(
            Guid.NewGuid(),
            request.PaymentId,
            request.InvoiceId,
            request.AllocatedAmount,
            DateTime.UtcNow);

        if (!allocationResult.IsSuccess)
        {
            return allocationResult.Errors!;
        }

        var allocation = allocationResult.Value;

        dbContext.PaymentAllocations.Add(allocation);

        // Update invoice payment status
        var updateResult = invoice.UpdatePaymentStatus();
        if (!updateResult.IsSuccess)
        {
            return updateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Payment.Allocate",
            entityType: nameof(PaymentAllocation),
            entityId: allocation.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                allocation.PaymentId,
                allocation.InvoiceId,
                allocation.AllocatedAmount
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
