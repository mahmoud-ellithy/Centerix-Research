namespace Centerix.Application.Students.Students.Commands;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Branches;
using Centerix.Domain.Students.Enums;
using Centerix.Domain.Students.Lookups;
using Centerix.Domain.Students.Students;

using MediatR;
using Microsoft.EntityFrameworkCore;

public record UpdateStudentCommand(
    Guid Id,
    Guid BranchId,
    int StageId,
    int YearId,
    string FullNameAr,
    string? FullNameEn,
    DateOnly? DateOfBirth,
    Gender? Gender,
    string? Phone,
    DiscountType? DiscountType,
    decimal? DiscountValue,
    StudentStatus Status) : IRequest<Result<Updated>>;

public class UpdateStudentHandler(
    IAppDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditWriter auditWriter) : IRequestHandler<UpdateStudentCommand, Result<Updated>>
{
    public async Task<Result<Updated>> Handle(
        UpdateStudentCommand request,
        CancellationToken cancellationToken)
    {
        var student = await dbContext.Students.FindAsync([request.Id], cancellationToken: cancellationToken);
        if (student is null)
        {
            return StudentErrors.NotFound;
        }

        // Explicit tenant ownership assertion — the global query filter on IHasTenantId
        // returns only rows matching the current tenant, but we assert explicitly here so
        // that any future change to the filter (e.g. IgnoreQueryFilters) cannot silently
        // expose cross-tenant mutation.
        if (student.TenantId != currentTenant.TenantId)
        {
            return StudentErrors.NotFound;
        }

        // Tenant-scoped referential integrity: branch / stage / year must exist within the
        // resolved tenant before we allow the assignment. This mirrors the pattern used in
        // CreateStudentHandler and prevents a tenant user from reassigning a student to a
        // cross-tenant branch, stage, or year.
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

        var oldValue = AuditPayload.Serialize(new
        {
            student.FullNameAr,
            student.BranchId,
            student.StageId,
            student.YearId,
            student.Status
        });

        var updateResult = student.Update(
            request.BranchId,
            request.StageId,
            request.YearId,
            request.FullNameAr,
            request.FullNameEn,
            request.DateOfBirth,
            request.Gender,
            request.Phone,
            request.DiscountType,
            request.DiscountValue,
            request.Status);

        if (!updateResult.IsSuccess)
        {
            return updateResult.Errors!;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            action: "Student.Update",
            entityType: nameof(Student),
            entityId: student.Id.ToString(),
            oldValue: oldValue,
            newValue: AuditPayload.Serialize(new
            {
                student.FullNameAr,
                student.BranchId,
                student.StageId,
                student.YearId,
                student.Status
            }),
            cancellationToken: cancellationToken);

        return Result.Updated;
    }
}
