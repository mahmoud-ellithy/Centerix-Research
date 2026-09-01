namespace Centerix.Application.Teachers.Teachers.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Teachers.Teachers;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record DeleteTeacherCommand(Guid Id) : IRequest<Result<Updated>>;

public class DeleteTeacherHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    ILimitService limitService,
    IAuditWriter auditWriter) : IRequestHandler<DeleteTeacherCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        DeleteTeacherCommand request,
        CancellationToken cancellationToken)
    {
        var teacher = await dbContext.Teachers
            .FirstOrDefaultAsync(t => t.Id == request.Id, cancellationToken);
        if (teacher is null)
            return TeacherErrors.NotFound;

        var oldValue = AuditPayload.Serialize(new
        {
            teacher.FullName,
            teacher.UserId,
            teacher.BranchId,
            teacher.Status
        });

        var result = teacher.SoftDelete();
        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        // Soft-deleting a teacher returns the slot to the active counter so the
        // tenant can immediately replace them without an explicit release call.
        await limitService.ReleaseAsync(currentTenant.TenantId!, LimitTypeCodes.Teachers, cancellationToken);

        await auditWriter.WriteAsync(
            action: "Teacher.Delete",
            entityType: nameof(Teacher),
            entityId: teacher.Id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}