namespace Centerix.Domain.Students.Lookups;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;

public class AcademicStage : AuditableEntity<int>
{
    public string Code { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public byte SortOrder { get; private set; }

    private readonly List<AcademicYear> _academicYears = [];
    public IReadOnlyList<AcademicYear> AcademicYears => _academicYears.AsReadOnly();

    private AcademicStage() { }

    private AcademicStage(int id, string code, string displayName, byte sortOrder)
        : base(id)
    {
        Code = code;
        DisplayName = displayName;
        SortOrder = sortOrder;
    }

    public static Result<AcademicStage> Create(int id, string code, string displayName, byte sortOrder)
    {
        if (string.IsNullOrWhiteSpace(code))
            return AcademicStageErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return AcademicStageErrors.DisplayNameRequired;

        return new AcademicStage(id, code.Trim().ToUpperInvariant(), displayName.Trim(), sortOrder);
    }

    public Result<Updated> Update(string code, string displayName, byte sortOrder)
    {
        if (string.IsNullOrWhiteSpace(code))
            return AcademicStageErrors.CodeRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return AcademicStageErrors.DisplayNameRequired;

        Code = code.Trim().ToUpperInvariant();
        DisplayName = displayName.Trim();
        SortOrder = sortOrder;

        return Result.Updated;
    }
}
