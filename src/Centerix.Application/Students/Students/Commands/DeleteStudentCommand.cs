namespace Centerix.Application.Students.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Students;

using MediatR;

public record DeleteStudentCommand(Guid Id) : IRequest<Result<Updated>>;

public class DeleteStudentHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<DeleteStudentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        DeleteStudentCommand request,
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
            student.Status
        });

        var deleteResult = student.SoftDelete();
        if (!deleteResult.IsSuccess)
        {
            return deleteResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Student.Delete",
            entityType: nameof(Student),
            entityId: student.Id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
