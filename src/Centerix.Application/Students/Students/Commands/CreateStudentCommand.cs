namespace Centerix.Application.Students.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Students;

using MediatR;

public record CreateStudentCommand(
    Guid BranchId,
    int StageId,
    int YearId,
    string FullNameAr,
    string? FullNameEn,
    DateOnly? DateOfBirth,
    Gender? Gender,
    string? Phone,
    string QRCode,
    DiscountType? DiscountType,
    decimal? DiscountValue,
    StudentStatus Status,
    DateOnly EnrolledAt) : IRequest<Result<Created>>;

public class CreateStudentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<CreateStudentCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateStudentCommand request,
        CancellationToken cancellationToken)
    {
        var studentResult = Student.Create(
            Guid.NewGuid(),
            request.BranchId,
            request.StageId,
            request.YearId,
            request.FullNameAr,
            request.FullNameEn,
            request.DateOfBirth,
            request.Gender,
            request.Phone,
            request.QRCode,
            request.DiscountType,
            request.DiscountValue,
            request.Status,
            request.EnrolledAt);

        if (!studentResult.IsSuccess)
        {
            return studentResult.Errors!;
        }

        dbContext.Students.Add(studentResult.Value);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Student.Create",
            entityType: nameof(Student),
            entityId: studentResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                studentResult.Value.FullNameAr,
                studentResult.Value.BranchId,
                studentResult.Value.StageId,
                studentResult.Value.YearId,
                studentResult.Value.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
