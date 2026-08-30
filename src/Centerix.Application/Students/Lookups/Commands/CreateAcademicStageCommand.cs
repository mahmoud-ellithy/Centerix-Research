namespace Centerix.Application.Students.Lookups.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Lookups;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record CreateAcademicStageCommand(
    string Code,
    string DisplayName,
    byte SortOrder) : IRequest<Result<Created>>;

public class CreateAcademicStageHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<CreateAcademicStageCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateAcademicStageCommand request,
        CancellationToken cancellationToken)
    {
        var stageResult = AcademicStage.Create(
            0,
            request.Code,
            request.DisplayName,
            request.SortOrder);

        if (!stageResult.IsSuccess)
        {
            return stageResult.Errors!;
        }

        // Tenant-scoped uniqueness on (TenantId, Code). The relational schema also has a
        // filtered unique index (UX_AcademicStages_TenantId_Code) that protects against
        // races, but the EF InMemory provider used by the HTTP tests does not enforce unique
        // indexes, so the application layer must own the check to return a clean 409 Conflict
        // for the duplicate case instead of an obscure DB exception.
        var normalizedCode = stageResult.Value.Code;
        var duplicate = await dbContext.AcademicStages
            .IgnoreQueryFilters()
            .AnyAsync(s => s.Code == normalizedCode && s.TenantId == currentTenant.TenantId,
                cancellationToken);
        if (duplicate)
        {
            return AcademicStageErrors.DuplicateCode;
        }

        dbContext.AcademicStages.Add(stageResult.Value);
        // The TenantInterceptor stamps TenantId on the relational path; the explicit
        // StampAddedTenantIds call mirrors that for the EF InMemory test provider, which
        // does not invoke the interceptor.
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "AcademicStage.Create",
            entityType: nameof(AcademicStage),
            entityId: stageResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                stageResult.Value.Code,
                stageResult.Value.DisplayName,
                stageResult.Value.SortOrder
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
