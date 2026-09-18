namespace Centerix.Application.Platform.Billing.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

public record GetTenantCreditsQuery : IRequest<Result<IEnumerable<TenantCreditDto>>>;

public class GetTenantCreditsHandler(IAppDbContext dbContext)
    : IRequestHandler<GetTenantCreditsQuery, Result<IEnumerable<TenantCreditDto>>>
{
    public async Task<Result<IEnumerable<TenantCreditDto>>> Handle(
        GetTenantCreditsQuery request,
        CancellationToken cancellationToken)
    {
        var credits = await dbContext.TenantCredits
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new TenantCreditDto
            {
                Id = c.Id,
                Amount = c.Amount,
                RemainingAmount = c.RemainingAmount,
                SourceType = c.SourceType.ToString(),
                Status = c.Status.ToString(),
                CurrencyCode = c.CurrencyCode,
                SourceId = c.SourceId,
                CreatedAt = c.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return credits;
    }
}

public record GetCreditBalanceQuery(Guid CreditId) : IRequest<Result<CreditBalanceDto>>;

public class GetCreditBalanceHandler(IAppDbContext dbContext)
    : IRequestHandler<GetCreditBalanceQuery, Result<CreditBalanceDto>>
{
    public async Task<Result<CreditBalanceDto>> Handle(
        GetCreditBalanceQuery request,
        CancellationToken cancellationToken)
    {
        var credit = await dbContext.TenantCredits
            .FirstOrDefaultAsync(c => c.Id == request.CreditId, cancellationToken);

        if (credit is null)
        {
            return Domain.Platform.Billing.Credits.TenantCreditErrors.NotFound;
        }

        var totalApplied = await dbContext.CreditApplications
            .Where(ca => ca.CreditId == request.CreditId)
            .SumAsync(ca => ca.Amount, cancellationToken);

        return new CreditBalanceDto
        {
            CreditId = credit.Id,
            TotalCredit = credit.Amount,
            ConsumedCredit = totalApplied,
            AvailableCredit = credit.RemainingAmount,
            CurrencyCode = credit.CurrencyCode,
            Status = credit.Status.ToString()
        };
    }
}

public class CreditBalanceDto
{
    public Guid CreditId { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal ConsumedCredit { get; set; }
    public decimal AvailableCredit { get; set; }
    public string CurrencyCode { get; set; } = "EGP";
    public string Status { get; set; } = default!;
}
