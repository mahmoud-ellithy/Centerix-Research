namespace Centerix.Domain.Students.Lookups;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class AcademicYear : AuditableEntity<int>
{
    public int StageId { get; private set; }
    public string YearCode { get; private set; } = default!;
    public string YearName { get; private set; } = default!;

    public AcademicStage Stage { get; private set; } = default!;

    private AcademicYear() { }

    private AcademicYear(int id, int stageId, string yearCode, string yearName)
        : base(id)
    {
        StageId = stageId;
        YearCode = yearCode;
        YearName = yearName;
    }

    public static Result<AcademicYear> Create(int id, int stageId, string yearCode, string yearName)
    {
        if (stageId <= 0)
            return AcademicYearErrors.StageIdRequired;

        if (string.IsNullOrWhiteSpace(yearCode))
            return AcademicYearErrors.YearCodeRequired;

        if (string.IsNullOrWhiteSpace(yearName))
            return AcademicYearErrors.YearNameRequired;

        return new AcademicYear(id, stageId, yearCode.Trim().ToUpperInvariant(), yearName.Trim());
    }

    public Result<Updated> Update(int stageId, string yearCode, string yearName)
    {
        if (stageId <= 0)
            return AcademicYearErrors.StageIdRequired;

        if (string.IsNullOrWhiteSpace(yearCode))
            return AcademicYearErrors.YearCodeRequired;

        if (string.IsNullOrWhiteSpace(yearName))
            return AcademicYearErrors.YearNameRequired;

        StageId = stageId;
        YearCode = yearCode.Trim().ToUpperInvariant();
        YearName = yearName.Trim();

        return Result.Updated;
    }
}
