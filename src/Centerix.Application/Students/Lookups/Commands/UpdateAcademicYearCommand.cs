namespace Centerix.Application.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;

using MediatR;

public record UpdateAcademicYearCommand(
    int Id,
    int StageId,
    string YearCode,
    string YearName) : IRequest<Result<Updated>>;

public class UpdateAcademicYearHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdateAcademicYearCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        UpdateAcademicYearCommand request,
        CancellationToken cancellationToken)
    {
        var academicYear = await dbContext.AcademicYears.FindAsync(
            [request.Id], cancellationToken: cancellationToken);

        if (academicYear is null)
        {
            return AcademicYearErrors.NotFound;
        }

        var oldValue = AuditPayload.Serialize(new
        {
            academicYear.StageId,
            academicYear.YearCode,
            academicYear.YearName
        });

        academicYear.Update(
            request.StageId,
            request.YearCode,
            request.YearName);

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AcademicYear.Update",
            entityType: nameof(AcademicYear),
            entityId: academicYear.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                academicYear.StageId,
                academicYear.YearCode,
                academicYear.YearName
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
