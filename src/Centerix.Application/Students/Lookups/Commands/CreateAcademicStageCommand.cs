namespace Centerix.Application.Students.Lookups.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;

using MediatR;

public record CreateAcademicStageCommand(
    string Code,
    string DisplayName,
    byte SortOrder) : IRequest<Result<Created>>;

public class CreateAcademicStageHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateAcademicStageCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateAcademicStageCommand request,
        CancellationToken cancellationToken)
    {
        var stageResult = AcademicStage.Create(
            0,
            request.Code,
            request.DisplayName,
            request.SortOrder);

        if (!stageResult.IsSuccess)
        {
            return stageResult.Errors!;
        }

        dbContext.AcademicStages.Add(stageResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AcademicStage.Create",
            entityType: nameof(AcademicStage),
            entityId: stageResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                stageResult.Value.Code,
                stageResult.Value.DisplayName,
                stageResult.Value.SortOrder
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
