namespace Centerix.Application.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;

using MediatR;

public record CreateAcademicYearCommand(
    int StageId,
    string YearCode,
    string YearName) : IRequest<Result<Created>>;

public class CreateAcademicYearHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateAcademicYearCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateAcademicYearCommand request,
        CancellationToken cancellationToken)
    {
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
