namespace Centerix.Application.Teachers.Subjects.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Teachers.Subjects;

using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record CreateSubjectCommand(
    string Name,
    int StageId) : IRequest<Result<Created>>;

public class CreateSubjectValidator : AbstractValidator<CreateSubjectCommand>
{
    public CreateSubjectValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.StageId)
            .GreaterThan(0);
    }
}

public class CreateSubjectHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<CreateSubjectCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateSubjectCommand request,
        CancellationToken cancellationToken)
    {
        var stageExists = await dbContext.AcademicStages
            .AsNoTracking()
            .AnyAsync(x => x.Id == request.StageId, cancellationToken);
        if (!stageExists)
            return AcademicStageErrors.NotFound;

        var result = Subject.Create(0, request.Name, request.StageId);
        if (!result.IsSuccess)
            return result.Errors!;

        var duplicate = await dbContext.Subjects
            .IgnoreQueryFilters()
            .AnyAsync(s =>
                s.TenantId == currentTenant.TenantId &&
                s.StageId == request.StageId &&
                s.Name == result.Value.Name,
                cancellationToken);
        if (duplicate)
            return SubjectErrors.DuplicateName;

        dbContext.Subjects.Add(result.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Subject.Create",
            entityType: nameof(Subject),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.Name,
                result.Value.StageId
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}