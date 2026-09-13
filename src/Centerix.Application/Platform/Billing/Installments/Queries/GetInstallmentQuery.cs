namespace Centerix.Application.Platform.Billing.Installments.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetInstallmentQuery(Guid Id) : IRequest<Result<InstallmentDto>>;

public class GetInstallmentHandler(IAppDbContext dbContext)
    : IRequestHandler<GetInstallmentQuery, Result<InstallmentDto>>
{
    public async Task<Result<InstallmentDto>> Handle(
        GetInstallmentQuery request,
        CancellationToken cancellationToken)
    {
        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return InstallmentErrors.TenantIdRequired;

        var installment = await dbContext.Installments
            .Where(i => i.Id == request.Id && i.TenantId == tenantId)
            .Select(i => new InstallmentDto
            {
                Id = i.Id,
                ContractId = i.ContractId,
                SubscriptionId = i.SubscriptionId,
                InvoiceId = i.InvoiceId,
                SequenceNumber = i.SequenceNumber,
                DueDateUtc = i.DueDateUtc,
                CoveredPeriodStartUtc = i.CoveredPeriodStartUtc,
                CoveredPeriodEndUtc = i.CoveredPeriodEndUtc,
                Amount = i.Amount,
                CurrencyCode = i.CurrencyCode,
                SettledAmount = i.SettledAmount,
                RemainingAmount = i.RemainingAmount,
                Status = i.Status,
                CreatedAt = i.CreatedAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (installment is null)
            return InstallmentErrors.NotFound;

        return installment;
    }
}
