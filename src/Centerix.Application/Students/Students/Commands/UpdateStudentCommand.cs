namespace Centerix.Application.Students.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Students;

using MediatR;

public record UpdateStudentCommand(
    Guid Id,
    Guid BranchId,
    int StageId,
    int YearId,
    string FullNameAr,
    string? FullNameEn,
    DateOnly? DateOfBirth,
    Gender? Gender,
    string? Phone,
    DiscountType? DiscountType,
    decimal? DiscountValue,
    StudentStatus Status) : IRequest<Result<Updated>>;

public class UpdateStudentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdateStudentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        UpdateStudentCommand request,
        CancellationToken cancellationToken)
    {
        var student = await dbContext.Students.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (student is null)
        {
            return StudentErrors.NotFound;
        }

        var oldValue = AuditPayload.Serialize(new
        {
            student.FullNameAr,
            student.BranchId,
            student.StageId,
            student.YearId,
            student.Status
        });

        var updateResult = student.Update(
            request.BranchId,
            request.StageId,
            request.YearId,
            request.FullNameAr,
            request.FullNameEn,
            request.DateOfBirth,
            request.Gender,
            request.Phone,
            request.DiscountType,
            request.DiscountValue,
            request.Status);

        if (!updateResult.IsSuccess)
        {
            return updateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Student.Update",
            entityType: nameof(Student),
            entityId: student.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                student.FullNameAr,
                student.BranchId,
                student.StageId,
                student.YearId,
                student.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
