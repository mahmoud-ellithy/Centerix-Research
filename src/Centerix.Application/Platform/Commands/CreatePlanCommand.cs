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

public class CreatePlanHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreatePlanCommand, Result<Created>>
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

        if (!planResult.IsSuccess)
        {
            return planResult.Errors!;
        }

        dbContext.Plans.Add(planResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Plan.Create",
            entityType: nameof(Plan),
            entityId: planResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                planResult.Value.Code,
                planResult.Value.DisplayName,
                planResult.Value.MonthlyPrice,
                planResult.Value.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
