namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Plans;

using MediatR;

public record UpdatePlanCommand(
    int Id,
    string Code,
    string DisplayName,
    decimal MonthlyPrice,
    int MaxStudents,
    int MaxUsers,
    int MaxBranches,
    int MaxTeachers,
    int StorageGB,
    int SMSQuota,
    bool IsActive) : IRequest<Result<Updated>>;

public class UpdatePlanHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdatePlanCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(UpdatePlanCommand request, CancellationToken cancellationToken)
    {
        var plan = await dbContext.Plans.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (plan is null)
        {
            return Error.NotFound("Plan.NotFound", $"Plan with id '{request.Id}' was not found.");
        }

        var oldValue = AuditPayload.Serialize(new
        {
            plan.Code,
            plan.DisplayName,
            plan.MonthlyPrice,
            plan.IsActive
        });

        plan.Update(
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

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Plan.Update",
            entityType: nameof(Plan),
            entityId: plan.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                plan.Code,
                plan.DisplayName,
                plan.MonthlyPrice,
                plan.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
