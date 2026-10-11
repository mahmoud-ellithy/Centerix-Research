namespace Centerix.Application.Teachers.Teachers.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.Teachers;

using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record CreateTeacherCommand(
    string UserId,
    Guid BranchId,
    string FullName,
    string Phone,
    string? Qualification,
    byte? YearsExp,
    TeacherStatus Status,
    DateOnly JoinedAt) : IRequest<Result<Created>>;

public class CreateTeacherValidator : AbstractValidator<CreateTeacherCommand>
{
    public CreateTeacherValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty()
            .MaximumLength(450);

        RuleFor(x => x.FullName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Phone)
            .NotEmpty()
            .MaximumLength(30);

        RuleFor(x => x.Qualification)
            .MaximumLength(200)
            .When(x => !string.IsNullOrEmpty(x.Qualification));

        RuleFor(x => x.YearsExp)
            .InclusiveBetween((byte)0, (byte)60)
            .When(x => x.YearsExp.HasValue);
    }
}

public class CreateTeacherHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    ILimitService limitService,
    IAuditWriter auditWriter) : IRequestHandler<CreateTeacherCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateTeacherCommand request,
        CancellationToken cancellationToken)
    {
        // REV-011: same transaction wrapper as CreateStudentHandler (reserve + validations +
        // save commit atomically on relational providers) without changing success/error
        // semantics: validation failures are checked before reserving, and the transaction —
        // not manual ReleaseAsync — undoes the reservation on every failure path.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(cancellationToken)
            : null;

        // Security: the referenced Identity user must hold an active membership in this tenant.
        var userIsMember = await dbContext.TenantMemberships
            .AsNoTracking()
            .AnyAsync(m =>
                m.UserId == request.UserId
                && m.TenantId == currentTenant.TenantId
                && m.Status == TenantMembershipStatus.Active,
                cancellationToken);
        if (!userIsMember)
            return TeacherErrors.UserNotInTenant;

        // Tenant-scoped referential integrity: branch must exist within the resolved tenant.
        var branchExists = await dbContext.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == request.BranchId, cancellationToken);
        if (!branchExists)
            return BranchErrors.NotFound;

        // Commercial gate: plan/override limit on active subscription (atomic slot reservation
        // inside the ambient transaction above).
        var limitResult = await limitService.ReserveAsync(
            currentTenant.TenantId!, LimitTypeCodes.Teachers, cancellationToken);
        if (!limitResult.IsSuccess)
            return limitResult.Errors!;

        var result = Teacher.Create(
            Guid.NewGuid(),
            request.UserId,
            request.BranchId,
            request.FullName,
            request.Phone,
            request.Qualification,
            request.YearsExp,
            request.Status,
            request.JoinedAt);

        if (!result.IsSuccess)
            return result.Errors!;

        dbContext.Teachers.Add(result.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Teacher.Create",
            entityType: nameof(Teacher),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.FullName,
                result.Value.UserId,
                result.Value.BranchId,
                result.Value.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}