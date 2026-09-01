namespace Centerix.Application.Teachers.SalaryPayments.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.SalaryPayments;

using Microsoft.EntityFrameworkCore;

using MediatR;

public record GetSalaryPaymentsQuery(Guid? TeacherId = null) : IRequest<Result<IEnumerable<SalaryPaymentDto>>>;

public class GetSalaryPaymentsHandler(IAppDbContext dbContext) : IRequestHandler<GetSalaryPaymentsQuery, Result<IEnumerable<SalaryPaymentDto>>>
{
    public async Task<Result<IEnumerable<SalaryPaymentDto>>> Handle(
        GetSalaryPaymentsQuery request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SalaryPayments.AsNoTracking();

        if (request.TeacherId.HasValue)
            query = query.Where(p => p.TeacherId == request.TeacherId.Value);

        var items = await query
            .OrderByDescending(p => p.PeriodYear)
            .ThenByDescending(p => p.PeriodMonth)
            .Select(p => new SalaryPaymentDto
            {
                Id = p.Id,
                TeacherId = p.TeacherId,
                PeriodMonth = p.PeriodMonth,
                PeriodYear = p.PeriodYear,
                GrossAmount = p.GrossAmount,
                NetAmount = p.NetAmount,
                Status = p.Status.ToString(),
                PaidAt = p.PaidAt,
                TeacherName = dbContext.Teachers
                    .Where(t => t.Id == p.TeacherId)
                    .Select(t => t.FullName)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return items;
    }
}

public record GetSalaryPaymentByIdQuery(Guid Id) : IRequest<Result<SalaryPaymentDto>>;

public class GetSalaryPaymentByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetSalaryPaymentByIdQuery, Result<SalaryPaymentDto>>
{
    public async Task<Result<SalaryPaymentDto>> Handle(
        GetSalaryPaymentByIdQuery request,
        CancellationToken cancellationToken)
    {
        var payment = await dbContext.SalaryPayments
            .AsNoTracking()
            .Where(p => p.Id == request.Id)
            .Select(p => new SalaryPaymentDto
            {
                Id = p.Id,
                TeacherId = p.TeacherId,
                PeriodMonth = p.PeriodMonth,
                PeriodYear = p.PeriodYear,
                GrossAmount = p.GrossAmount,
                NetAmount = p.NetAmount,
                Status = p.Status.ToString(),
                PaidAt = p.PaidAt,
                TeacherName = dbContext.Teachers
                    .Where(t => t.Id == p.TeacherId)
                    .Select(t => t.FullName)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (payment is null)
            return SalaryPaymentErrors.NotFound;

        return payment;
    }
}