namespace Centerix.Application.Students.Lookups.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;

using MediatR;

public record UpdateAcademicStageCommand(
    int Id,
    string Code,
    string DisplayName,
    byte SortOrder) : IRequest<Result<Updated>>;

public class UpdateAcademicStageHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdateAcademicStageCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(UpdateAcademicStageCommand request, CancellationToken cancellationToken)
    {
        var stage = await dbContext.AcademicStages.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (stage is null)
        {
            return AcademicStageErrors.NotFound;
        }

        var oldValue = AuditPayload.Serialize(new
        {
            stage.Code,
            stage.DisplayName,
            stage.SortOrder
        });

        var updateResult = stage.Update(
            request.Code,
            request.DisplayName,
            request.SortOrder);

        if (!updateResult.IsSuccess)
        {
            return updateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AcademicStage.Update",
            entityType: nameof(AcademicStage),
            entityId: stage.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                stage.Code,
                stage.DisplayName,
                stage.SortOrder
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
