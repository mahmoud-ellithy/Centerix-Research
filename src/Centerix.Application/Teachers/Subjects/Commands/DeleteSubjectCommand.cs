namespace Centerix.Application.Teachers.Subjects.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.Subjects;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record DeleteSubjectCommand(int Id) : IRequest<Result<Updated>>;

public class DeleteSubjectHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<DeleteSubjectCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        DeleteSubjectCommand request,
        CancellationToken cancellationToken)
    {
        var subject = await dbContext.Subjects
            .FirstOrDefaultAsync(s => s.Id == request.Id, cancellationToken);
        if (subject is null)
            return SubjectErrors.NotFound;

        var oldValue = AuditPayload.Serialize(new
        {
            subject.Name,
            subject.StageId
        });

        dbContext.Subjects.Remove(subject);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Subject.Delete",
            entityType: nameof(Subject),
            entityId: subject.Id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}