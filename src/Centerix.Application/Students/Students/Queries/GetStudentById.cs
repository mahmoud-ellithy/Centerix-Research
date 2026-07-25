namespace Centerix.Application.Students.Students.Queries;

using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Students.Students;

using Microsoft.EntityFrameworkCore;

using MediatR;

public record GetStudentByIdQuery(Guid Id) : IRequest<Result<StudentDto>>;

public class GetStudentByIdHandler(IAppDbContext dbContext) : IRequestHandler<GetStudentByIdQuery, Result<StudentDto>>
{
    public async Task<Result<StudentDto>> Handle(
        GetStudentByIdQuery request,
        CancellationToken cancellationToken)
    {
        var student = await dbContext.Students
            .AsNoTracking()
            .Include(s => s.Branch)
            .Include(s => s.Stage)
            .Include(s => s.Year)
            .Where(s => s.Id == request.Id)
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
            .FirstOrDefaultAsync(cancellationToken);

        if (student is null)
        {
            return StudentErrors.NotFound;
        }

        return student;
    }
}
