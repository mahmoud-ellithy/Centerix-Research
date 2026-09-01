namespace Centerix.Application.Teachers.Teachers.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.Teachers;

using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record UpdateTeacherCommand(
    Guid Id,
    string UserId,
    Guid BranchId,
    string FullName,
    string Phone,
    string? Qualification,
    byte? YearsExp,
    TeacherStatus Status) : IRequest<Result<Updated>>;

public class UpdateTeacherValidator : AbstractValidator<UpdateTeacherCommand>
{
    public UpdateTeacherValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty().MaximumLength(450);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Qualification).MaximumLength(200).When(x => !string.IsNullOrEmpty(x.Qualification));
        RuleFor(x => x.YearsExp).InclusiveBetween((byte)0, (byte)60).When(x => x.YearsExp.HasValue);
    }
}

public class UpdateTeacherHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdateTeacherCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        UpdateTeacherCommand request,
        CancellationToken cancellationToken)
    {
        var teacher = await dbContext.Teachers
            .FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
        if (teacher is null)
            return TeacherErrors.NotFound;

        var branchExists = await dbContext.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == request.BranchId, cancellationToken);
        if (!branchExists)
            return BranchErrors.NotFound;

        var oldValue = AuditPayload.Serialize(new
        {
            teacher.FullName,
            teacher.UserId,
            teacher.BranchId,
            teacher.Phone,
            teacher.Status
        });

        var result = teacher.Update(
            request.BranchId,
            request.FullName,
            request.Phone,
            request.Qualification,
            request.YearsExp,
            request.Status);

        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Teacher.Update",
            entityType: nameof(Teacher),
            entityId: teacher.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                teacher.FullName,
                teacher.UserId,
                teacher.BranchId,
                teacher.Phone,
                teacher.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}