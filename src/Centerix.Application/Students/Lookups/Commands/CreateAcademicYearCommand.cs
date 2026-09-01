namespace Centerix.Application.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record CreateAcademicYearCommand(
    int StageId,
    string YearCode,
    string YearName) : IRequest<Result<Created>>;

public class CreateAcademicYearHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<CreateAcademicYearCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateAcademicYearCommand request,
        CancellationToken cancellationToken)
    {
        // The IHasTenantId query filter scopes this lookup to the current tenant,
        // so a missing row means either the stage doesn't exist or it belongs to
        // another tenant — both surface as 404 (never 500, never silently created).
        var stageExists = await dbContext.AcademicStages
            .AnyAsync(x => x.Id == request.StageId, cancellationToken);
        if (!stageExists)
        {
            return AcademicYearErrors.StageNotFound;
        }

        var result = AcademicYear.Create(
            0,
            request.StageId,
            request.YearCode,
            request.YearName);

        if (!result.IsSuccess)
        {
            return result.Errors!;
        }

        dbContext.AcademicYears.Add(result.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AcademicYear.Create",
            entityType: nameof(AcademicYear),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.StageId,
                result.Value.YearCode,
                result.Value.YearName
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
