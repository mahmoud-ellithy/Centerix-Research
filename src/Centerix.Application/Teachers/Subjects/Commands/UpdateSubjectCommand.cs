namespace Centerix.Application.Teachers.Subjects.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Teachers.Subjects;

using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record UpdateSubjectCommand(
    int Id,
    string Name,
    int StageId) : IRequest<Result<Updated>>;

public class UpdateSubjectValidator : AbstractValidator<UpdateSubjectCommand>
{
    public UpdateSubjectValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.StageId).GreaterThan(0);
    }
}

public class UpdateSubjectHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<UpdateSubjectCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        UpdateSubjectCommand request,
        CancellationToken cancellationToken)
    {
        var subject = await dbContext.Subjects
            .FirstOrDefaultAsync(s => s.Id == request.Id, cancellationToken);
        if (subject is null)
            return SubjectErrors.NotFound;

        var stageExists = await dbContext.AcademicStages
            .AsNoTracking()
            .AnyAsync(x => x.Id == request.StageId, cancellationToken);
        if (!stageExists)
            return AcademicStageErrors.NotFound;

        var oldValue = AuditPayload.Serialize(new
        {
            subject.Name,
            subject.StageId
        });

        var result = subject.Update(request.Name, request.StageId);
        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Subject.Update",
            entityType: nameof(Subject),
            entityId: subject.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                subject.Name,
                subject.StageId
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}