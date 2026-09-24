namespace Centerix.Application.Platform.Billing.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record CreatePaymentCommand(
    string PaymentNumber,
    decimal Amount,
    string CurrencyCode,
    PaymentMethod Method,
    string? IdempotencyKey = null,
    string? ExternalReference = null,
    string? Notes = null) : IRequest<Result<Guid>>;

public class CreatePaymentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreatePaymentCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreatePaymentCommand request,
        CancellationToken cancellationToken)
    {
        // Task 19 — IDEMPOTENCY CHECK (key-based, BEFORE insert).
        // A client may retry CreatePayment with the same IdempotencyKey (network blip, retry
        // policy, etc.). Without a key-based pre-check, a second request would create a
        // duplicate Payment row, which in turn double-counts against the tenant's financial
        // history. The UX_Payments_PaymentNumber constraint protects the auto-generated
        // PaymentNumber, but a client-supplied IdempotencyKey is the authoritative logical
        // retry token.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await dbContext.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    p => p.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken);

            if (existing is not null)
            {
                // Same key + matching payload → idempotent retry. Return the existing id.
                if (existing.PaymentNumber == request.PaymentNumber
                    && existing.Amount == request.Amount
                    && existing.CurrencyCode == request.CurrencyCode
                    && existing.Method == request.Method)
                {
                    return existing.Id;
                }

                // Same key + different payload → deterministic conflict.
                return Error.Conflict(
                    "Payment.IdempotencyKeyConflict",
                    "A payment with the same IdempotencyKey but different parameters already exists.");
            }
        }

        var paymentResult = Payment.Create(
            Guid.NewGuid(),
            request.PaymentNumber,
            request.Amount,
            request.CurrencyCode,
            request.Method,
            request.ExternalReference,
            request.Notes,
            request.IdempotencyKey);

        if (!paymentResult.IsSuccess)
        {
            return paymentResult.Errors!;
        }

        var payment = paymentResult.Value;

        dbContext.Payments.Add(payment);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The UX_Payments_TenantId_IdempotencyKey filtered unique index (when present)
            // guards against a TOCTOU race where a concurrent request inserts the same
            // IdempotencyKey between our pre-check and our insert. Re-read the persisted
            // record to distinguish idempotent retry from conflict.
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                if (dbContext is DbContext dbc)
                {
                    dbc.ChangeTracker.Clear();
                }

                var persisted = await dbContext.Payments
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        p => p.IdempotencyKey == request.IdempotencyKey,
                        cancellationToken);

                if (persisted is not null)
                {
                    if (persisted.PaymentNumber == request.PaymentNumber
                        && persisted.Amount == request.Amount
                        && persisted.CurrencyCode == request.CurrencyCode
                        && persisted.Method == request.Method)
                    {
                        return persisted.Id;
                    }

                    return Error.Conflict(
                        "Payment.IdempotencyKeyConflict",
                        "A payment with the same IdempotencyKey but different parameters already exists.");
                }
            }

            throw;
        }

        await auditWriter.WriteAsync(
            action: "Payment.Create",
            entityType: nameof(Payment),
            entityId: payment.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                payment.PaymentNumber,
                payment.Amount,
                payment.CurrencyCode,
                Method = payment.Method.ToString(),
                Status = payment.Status.ToString(),
                IdempotencyKey = payment.IdempotencyKey
            }),
            cancellationToken: cancellationToken);

        return payment.Id;
    }
}
