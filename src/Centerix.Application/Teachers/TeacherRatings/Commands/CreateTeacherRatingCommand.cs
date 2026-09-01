namespace Centerix.Application.Teachers.TeacherRatings.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Students;
using Centerix.Domain.Teachers.TeacherRatings;
using Centerix.Domain.Teachers.Teachers;

using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record CreateTeacherRatingCommand(
    Guid TeacherId,
    Guid StudentId,
    Guid? GroupId,
    byte Stars,
    string? Comment,
    byte PeriodMonth,
    short PeriodYear) : IRequest<Result<Created>>;

public class CreateTeacherRatingValidator : AbstractValidator<CreateTeacherRatingCommand>
{
    public CreateTeacherRatingValidator()
    {
        RuleFor(x => x.TeacherId).NotEmpty();
        RuleFor(x => x.StudentId).NotEmpty();
        RuleFor(x => x.Stars).InclusiveBetween((byte)1, (byte)5);
        RuleFor(x => x.Comment).MaximumLength(500).When(x => !string.IsNullOrEmpty(x.Comment));
        RuleFor(x => x.PeriodMonth).InclusiveBetween((byte)1, (byte)12);
        RuleFor(x => x.PeriodYear).InclusiveBetween((short)2000, (short)2100);
    }
}

public class CreateTeacherRatingHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<CreateTeacherRatingCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateTeacherRatingCommand request,
        CancellationToken cancellationToken)
    {
        var teacherExists = await dbContext.Teachers
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TeacherId, cancellationToken);
        if (!teacherExists)
            return TeacherErrors.NotFound;

        var studentExists = await dbContext.Students
            .AsNoTracking()
            .AnyAsync(s => s.Id == request.StudentId, cancellationToken);
        if (!studentExists)
            return StudentErrors.NotFound;

        var result = TeacherRating.Create(
            Guid.NewGuid(),
            request.TeacherId,
            request.StudentId,
            request.GroupId,
            request.Stars,
            request.Comment,
            request.PeriodMonth,
            request.PeriodYear);

        if (!result.IsSuccess)
            return result.Errors!;

        dbContext.TeacherRatings.Add(result.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TeacherRating.Create",
            entityType: nameof(TeacherRating),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.TeacherId,
                result.Value.StudentId,
                result.Value.Stars,
                result.Value.PeriodMonth,
                result.Value.PeriodYear
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}