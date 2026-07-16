namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Plans;

using MediatR;

public record CreatePlanCommand(
    string Code,
    string DisplayName,
    decimal MonthlyPrice,
    int MaxStudents,
    int MaxUsers,
    int MaxBranches,
    int MaxTeachers,
    int StorageGB,
    int SMSQuota,
    bool IsActive) : IRequest<Result<Created>>;

public class CreatePlanHandler(IAppDbContext _dbContext) : IRequestHandler<CreatePlanCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreatePlanCommand request,
        CancellationToken cancellationToken)
    {
        var planResult = Plan.Create(
            0,
            request.Code,
            request.DisplayName,
            request.MonthlyPrice,
            request.MaxStudents,
            request.MaxUsers,
            request.MaxBranches,
            request.MaxTeachers,
            request.StorageGB,
            request.SMSQuota,
            request.IsActive);

        if (planResult.IsSuccess)
        {
            _dbContext.Plans.Add(planResult.Value);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Created;
        }

        return planResult.Errors!;
    }
}