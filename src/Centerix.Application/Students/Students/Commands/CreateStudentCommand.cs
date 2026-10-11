namespace Centerix.Application.Students.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;

using MediatR;

using Microsoft.EntityFrameworkCore;

public record CreateStudentCommand(
    Guid BranchId,
    int StageId,
    int YearId,
    string FullNameAr,
    string? FullNameEn,
    DateOnly? DateOfBirth,
    Gender? Gender,
    string? Phone,
    string QRCode,
    DiscountType? DiscountType,
    decimal? DiscountValue,
    StudentStatus Status,
    DateOnly EnrolledAt) : IRequest<Result<Created>>;

/// <summary>
/// Reference wiring of the reusable Phase 2 enforcement pipeline: the FEATURE gate lives on the
/// endpoint ([RequireFeature]) while the LIMIT gate runs here — permission alone is not enough
/// when the tenant's subscription quota is exhausted.
/// </summary>
public class CreateStudentHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    ILimitService limitService,
    IAuditWriter auditWriter) : IRequestHandler<CreateStudentCommand, Result<Created>>
{
    public async Task<Result<Created>> Handle(
        CreateStudentCommand request,
        CancellationToken cancellationToken)
    {
        // REV-011: reserve + validations + save run in ONE transaction on relational providers
        // (commit only on success; every non-success return/exception rolls back, so a crash
        // between reserve and save can no longer leak a slot). On InMemory the reservation is
        // a read-only check and the transaction is a no-op — nothing to undo there.
        await using var transaction = dbContext.IsRelational
            ? await dbContext.BeginTransactionAsync(cancellationToken)
            : null;

        // Tenant-scoped referential integrity: branch / stage / year must exist within the
        // resolved tenant (FailClosed — query filter ensures cross-tenant FK lookups return
        // null and surface as clean 404 rather than a relational FK violation 500).
        // REV-011: validated BEFORE reserving, so a 404 never burns a quota slot even
        // without the transaction safety net.
        var branchExists = await dbContext.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == request.BranchId, cancellationToken);
        if (!branchExists)
            return BranchErrors.NotFound;

        var stageExists = await dbContext.AcademicStages
            .AsNoTracking()
            .AnyAsync(s => s.Id == request.StageId, cancellationToken);
        if (!stageExists)
            return AcademicStageErrors.NotFound;

        var yearExists = await dbContext.AcademicYears
            .AsNoTracking()
            .AnyAsync(y => y.Id == request.YearId, cancellationToken);
        if (!yearExists)
            return AcademicYearErrors.NotFound;

        // Commercial gate: plan/override limit on active subscription (atomic slot reservation
        // inside the ambient transaction above).
        var limitResult = await limitService.ReserveAsync(
            currentTenant.TenantId!, LimitTypeCodes.Students, cancellationToken);
        if (!limitResult.IsSuccess)
            return limitResult.Errors!;

        var studentResult = Student.Create(
            Guid.NewGuid(),
            request.BranchId,
            request.StageId,
            request.YearId,
            request.FullNameAr,
            request.FullNameEn,
            request.DateOfBirth,
            request.Gender,
            request.Phone,
            request.QRCode,
            request.DiscountType,
            request.DiscountValue,
            request.Status,
            request.EnrolledAt);

        if (!studentResult.IsSuccess)
            return studentResult.Errors!;

        dbContext.Students.Add(studentResult.Value);
        dbContext.StampAddedTenantIds(currentTenant.TenantId!);

        // No manual ReleaseAsync: any SaveChanges failure (or concurrent unique-violation)
        // rolls back the reservation with the transaction on relational providers; on
        // InMemory the reservation was a read-only check. The exception still propagates
        // so infrastructure maps it (500, or a mapped 409 for unique violations).
        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Student.Create",
            entityType: nameof(Student),
            entityId: studentResult.Value.Id.ToString(),
            newValue: AuditPayload.Serialize(new
            {
                studentResult.Value.FullNameAr,
                studentResult.Value.BranchId,
                studentResult.Value.StageId,
                studentResult.Value.YearId,
                studentResult.Value.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Created;
    }
}
