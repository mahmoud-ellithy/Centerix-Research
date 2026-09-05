namespace Centerix.Application.Platform.Billing.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetPaymentByIdQuery(Guid Id) : IRequest<Result<PaymentDto>>;

public class GetPaymentByIdHandler(IAppDbContext dbContext)
    : IRequestHandler<GetPaymentByIdQuery, Result<PaymentDto>>
{
    public async Task<Result<PaymentDto>> Handle(
        GetPaymentByIdQuery request,
        CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments
            .Where(p => p.Id == request.Id)
            .ProjectToType<PaymentDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (payment is null)
        {
            return Error.NotFound("Payment.NotFound", $"Payment with id '{request.Id}' was not found.");
        }

        return payment;
    }
}

public record GetPaymentsQuery() : IRequest<Result<List<PaymentDto>>>;

public class GetPaymentsHandler(IAppDbContext dbContext)
    : IRequestHandler<GetPaymentsQuery, Result<List<PaymentDto>>>
{
    public async Task<Result<List<PaymentDto>>> Handle(
        GetPaymentsQuery request,
        CancellationToken cancellationToken)
    {
        var payments = await dbContext.Payments
            .ProjectToType<PaymentDto>()
            .ToListAsync(cancellationToken);

        return payments;
    }
}

public record GetPaymentAllocationsQuery(Guid PaymentId) : IRequest<Result<List<PaymentAllocationDto>>>;

public class GetPaymentAllocationsHandler(IAppDbContext dbContext)
    : IRequestHandler<GetPaymentAllocationsQuery, Result<List<PaymentAllocationDto>>>
{
    public async Task<Result<List<PaymentAllocationDto>>> Handle(
        GetPaymentAllocationsQuery request,
        CancellationToken cancellationToken)
    {
        var allocations = await dbContext.PaymentAllocations
            .Where(a => a.PaymentId == request.PaymentId)
            .ProjectToType<PaymentAllocationDto>()
            .ToListAsync(cancellationToken);

        return allocations;
    }
}

public record GetReceiptByPaymentIdQuery(Guid PaymentId) : IRequest<Result<ReceiptDto>>;

public class GetReceiptByPaymentIdHandler(IAppDbContext dbContext)
    : IRequestHandler<GetReceiptByPaymentIdQuery, Result<ReceiptDto>>
{
    public async Task<Result<ReceiptDto>> Handle(
        GetReceiptByPaymentIdQuery request,
        CancellationToken cancellationToken)
    {
        var receipt = await dbContext.PaymentReceipts
            .Where(r => r.PaymentId == request.PaymentId)
            .ProjectToType<ReceiptDto>()
            .FirstOrDefaultAsync(cancellationToken);

        if (receipt is null)
        {
            return Error.NotFound("Receipt.NotFound", $"Receipt for payment '{request.PaymentId}' was not found.");
        }

        return receipt;
    }
}

public record GetCustomerLedgerQuery() : IRequest<Result<List<CustomerLedgerEntryDto>>>;

public class GetCustomerLedgerHandler(IAppDbContext dbContext)
    : IRequestHandler<GetCustomerLedgerQuery, Result<List<CustomerLedgerEntryDto>>>
{
    public async Task<Result<List<CustomerLedgerEntryDto>>> Handle(
        GetCustomerLedgerQuery request,
        CancellationToken cancellationToken)
    {
        var entries = await dbContext.CustomerLedgerEntries
            .OrderBy(e => e.RecordedAtUtc)
            .ProjectToType<CustomerLedgerEntryDto>()
            .ToListAsync(cancellationToken);

        return entries;
    }
}
