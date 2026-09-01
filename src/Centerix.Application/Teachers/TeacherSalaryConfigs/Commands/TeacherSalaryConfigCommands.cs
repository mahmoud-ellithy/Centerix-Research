namespace Centerix.Application.Teachers.TeacherSalaryConfigs.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.TeacherSalaryConfigs;
using Centerix.Domain.Teachers.Teachers;

using FluentValidation;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record CreateTeacherSalaryConfigCommand(
    Guid TeacherId,
    Guid? GroupId,
    SalaryType SalaryType,
    decimal Value,
    DateOnly EffectiveFrom) : IRequest<Result<Created>>;

public class CreateTeacherSalaryConfigValidator : AbstractValidator<CreateTeacherSalaryConfigCommand>
{
    public CreateTeacherSalaryConfigValidator()
    {
        RuleFor(x => x.TeacherId).NotEmpty();
        RuleFor(x => x.Value)
            .GreaterThan(0)
            .LessThanOrEqualTo(999999.99m);
        RuleFor(x => x).Custom((cmd, ctx) =>
        {
            if (cmd.SalaryType == SalaryType.Percentage && cmd.Value > 100m)
            {
                ctx.AddFailure(nameof(cmd.Value), "Percentage salary value must be <= 100.");
            }
        });
    }
}

public class CreateTeacherSalaryConfigHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<CreateTeacherSalaryConfigCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateTeacherSalaryConfigCommand request,
        CancellationToken cancellationToken)
    {
        var teacherExists = await dbContext.Teachers
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TeacherId, cancellationToken);
        if (!teacherExists)
            return TeacherErrors.NotFound;

        var result = TeacherSalaryConfig.Create(
            0,
            request.TeacherId,
            request.GroupId,
            request.SalaryType,
            request.Value,
            request.EffectiveFrom);

        if (!result.IsSuccess)
            return result.Errors!;

        dbContext.TeacherSalaryConfigs.Add(result.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TeacherSalaryConfig.Create",
            entityType: nameof(TeacherSalaryConfig),
            entityId: result.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                result.Value.TeacherId,
                result.Value.GroupId,
                result.Value.SalaryType,
                result.Value.Value,
                result.Value.EffectiveFrom
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}

public record UpdateTeacherSalaryConfigCommand(
    int Id,
    Guid? GroupId,
    SalaryType SalaryType,
    decimal Value,
    DateOnly EffectiveFrom) : IRequest<Result<Updated>>;

public class UpdateTeacherSalaryConfigValidator : AbstractValidator<UpdateTeacherSalaryConfigCommand>
{
    public UpdateTeacherSalaryConfigValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.Value)
            .GreaterThan(0)
            .LessThanOrEqualTo(999999.99m);
        RuleFor(x => x).Custom((cmd, ctx) =>
        {
            if (cmd.SalaryType == SalaryType.Percentage && cmd.Value > 100m)
            {
                ctx.AddFailure(nameof(cmd.Value), "Percentage salary value must be <= 100.");
            }
        });
    }
}

public class UpdateTeacherSalaryConfigHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<UpdateTeacherSalaryConfigCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        UpdateTeacherSalaryConfigCommand request,
        CancellationToken cancellationToken)
    {
        var config = await dbContext.TeacherSalaryConfigs
            .FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken);
        if (config is null)
            return TeacherSalaryConfigErrors.NotFound;

        var oldValue = AuditPayload.Serialize(new
        {
            config.TeacherId,
            config.GroupId,
            config.SalaryType,
            config.Value,
            config.EffectiveFrom
        });

        var result = config.Update(
            request.GroupId,
            request.SalaryType,
            request.Value,
            request.EffectiveFrom);

        if (!result.IsSuccess)
            return result.Errors!;

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TeacherSalaryConfig.Update",
            entityType: nameof(TeacherSalaryConfig),
            entityId: config.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                config.TeacherId,
                config.GroupId,
                config.SalaryType,
                config.Value,
                config.EffectiveFrom
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}

public record DeleteTeacherSalaryConfigCommand(int Id) : IRequest<Result<Updated>>;

public class DeleteTeacherSalaryConfigHandler(
    IAppDbContext dbContext,
    IAuditWriter auditWriter) : IRequestHandler<DeleteTeacherSalaryConfigCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        DeleteTeacherSalaryConfigCommand request,
        CancellationToken cancellationToken)
    {
        var config = await dbContext.TeacherSalaryConfigs
            .FirstOrDefaultAsync(c => c.Id == request.Id, cancellationToken);
        if (config is null)
            return TeacherSalaryConfigErrors.NotFound;

        var oldValue = AuditPayload.Serialize(new
        {
            config.TeacherId,
            config.GroupId,
            config.SalaryType,
            config.Value
        });

        dbContext.TeacherSalaryConfigs.Remove(config);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "TeacherSalaryConfig.Delete",
            entityType: nameof(TeacherSalaryConfig),
            entityId: config.Id.ToString(),
            oldValue: oldValue,
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}