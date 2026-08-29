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
    bool IsActive,
    string? Description = null,
    string? CurrencyCode = null,
    int? DurationMonths = null,
    int? BonusMonths = null) : IRequest<Result<Updated>>;

public class UpdatePlanHandler(
    IAppDbContext dbContext,
    IPlatformAdminGuard platformAdminGuard,
    IAuditWriter auditWriter) : IRequestHandler<UpdatePlanCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(UpdatePlanCommand request, CancellationToken cancellationToken)
    {
        var guardResult = platformAdminGuard.EnsurePlatformAdmin();
        if (!guardResult.IsSuccess)
            return guardResult.Errors!;
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
            plan.CurrencyCode,
            plan.DurationMonths,
            plan.BonusMonths,
            plan.IsActive
        });

        var updateResult = plan.Update(
            request.Code,
            request.DisplayName,
            request.MonthlyPrice,
            request.MaxStudents,
            request.MaxUsers,
            request.MaxBranches,
            request.MaxTeachers,
            request.StorageGB,
            request.SMSQuota,
            request.IsActive,
            request.Description,
            request.CurrencyCode,
            request.DurationMonths,
            request.BonusMonths);

        if (!updateResult.IsSuccess)
            return updateResult.Errors!;

        // NOTE: existing subscriptions keep their purchased snapshot; this only affects future grants.
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
                plan.CurrencyCode,
                plan.DurationMonths,
                plan.BonusMonths,
                plan.IsActive
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
