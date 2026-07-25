namespace Centerix.Application.Students.Students.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;

using Microsoft.EntityFrameworkCore;

using MediatR;

public record GetStudentsQuery : IRequest<Result<IEnumerable<StudentDto>>>;

public class GetStudentsHandler(IAppDbContext dbContext) : IRequestHandler<GetStudentsQuery, Result<IEnumerable<StudentDto>>>
{
    public async Task<Result<IEnumerable<StudentDto>>> Handle(
        GetStudentsQuery request,
        CancellationToken cancellationToken)
    {
        var students = await dbContext.Students
            .AsNoTracking()
            .Include(s => s.Branch)
            .Include(s => s.Stage)
            .Include(s => s.Year)
            .Select(s => new StudentDto
            {
                Id = s.Id,
                BranchId = s.BranchId,
                StageId = s.StageId,
                YearId = s.YearId,
                FullNameAr = s.FullNameAr,
                FullNameEn = s.FullNameEn,
                DateOfBirth = s.DateOfBirth,
                Gender = s.Gender.ToString(),
                Phone = s.Phone,
                QRCode = s.QRCode,
                DiscountType = s.DiscountType.ToString(),
                DiscountValue = s.DiscountValue,
                Status = s.Status.ToString(),
                EnrolledAt = s.EnrolledAt,
                BranchName = s.Branch.Name,
                StageName = s.Stage.DisplayName,
                YearName = s.Year.YearName,
            })
            .ToListAsync(cancellationToken);

        return students;
    }
}
