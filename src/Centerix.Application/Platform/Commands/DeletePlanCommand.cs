namespace Centerix.Application.Platform.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Plans;

using MediatR;

public record DeletePlanCommand(int Id) : IRequest<Result<Deleted>>;

public class DeletePlanHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<DeletePlanCommand, Result<Deleted>>
{
    public async Task<Result<Deleted>> Handle(DeletePlanCommand request, CancellationToken cancellationToken)
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

        dbContext.Plans.Remove(plan);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Plan.Delete",
            entityType: nameof(Plan),
            entityId: request.Id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Deleted;
    }
}
