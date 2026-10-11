namespace Centerix.Application.Students.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;

using MediatR;

using Microsoft.Data.SqlClient;
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

        // REV-009: QR codes are unique per tenant (UX_Students_Tenant_QRCode). Explicit
        // tenant-scoped duplicate check INCLUDING soft-deleted rows (their QR stays
        // reserved), so a same-tenant duplicate answers 409 instead of a 500 from the
        // unique index. IgnoreQueryFilters + explicit TenantId predicate mirrors the
        // reconcile pattern; cross-tenant codes never collide here.
        var trimmedQRCode = request.QRCode.Trim();
        var qrDuplicate = await dbContext.Students
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(
                s => s.TenantId == currentTenant.TenantId && s.QRCode == trimmedQRCode,
                cancellationToken);
        if (qrDuplicate)
            return StudentErrors.DuplicateQRCode;

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

        // No manual ReleaseAsync: any SaveChanges failure rolls back the reservation with
        // the transaction on relational providers; on InMemory the reservation was a
        // read-only check. A concurrent duplicate insert surfaces as a unique-violation
        // DbUpdateException and is mapped to the same 409 below (never a 500).
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateQRCodeException(ex))
        {
            return StudentErrors.DuplicateQRCode;
        }

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

    /// <summary>
    /// REV-009: maps a concurrent-insert unique violation on UX_Students_Tenant_QRCode to the
    /// same 409 as the explicit pre-check (same pattern as the billing duplicate-number
    /// guards). SQL Server error 2601 = duplicate key in unique index, 2627 = UNIQUE KEY
    /// constraint violation; the index name pins the mapping to the QR constraint only.
    /// </summary>
    private static bool IsDuplicateQRCodeException(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            if (sqlEx.Number == 2601 || sqlEx.Number == 2627)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                return msg.Contains("UX_Students_Tenant_QRCode", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }
}
