namespace Centerix.Application.Platform.Billing.Installments.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Installments;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetInstallmentScheduleQuery(Guid ContractId) : IRequest<Result<List<InstallmentDto>>>;

public class GetInstallmentScheduleHandler(IAppDbContext dbContext)
    : IRequestHandler<GetInstallmentScheduleQuery, Result<List<InstallmentDto>>>
{
    public async Task<Result<List<InstallmentDto>>> Handle(
        GetInstallmentScheduleQuery request,
        CancellationToken cancellationToken)
    {
        var tenantId = dbContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            return InstallmentErrors.TenantIdRequired;

        var installments = await dbContext.Installments
            .Where(i => i.ContractId == request.ContractId && i.TenantId == tenantId)
            .OrderBy(i => i.SequenceNumber)
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
            .ToListAsync(cancellationToken);

        return installments;
    }
}
