namespace Centerix.Domain.Teachers.TeacherSalaryConfigs;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Teachers.Enums;
using Centerix.Domain.Teachers.Teachers;

public class TeacherSalaryConfig : AuditableEntity<int>
{
    public Guid TeacherId { get; private set; }

    /// <summary>
    /// Optional reference to a not-yet-implemented Groups aggregate.
    /// Stored as plain Guid with NO FK constraint for now. The Groups entity
    /// (M-03 Schedule) should introduce the FK constraint.
    /// </summary>
    public Guid? GroupId { get; private set; }

    public SalaryType SalaryType { get; private set; }
    public decimal Value { get; private set; }
    public DateOnly EffectiveFrom { get; private set; }

    public Teacher Teacher { get; private set; } = default!;

    private TeacherSalaryConfig() { }

    private TeacherSalaryConfig(
        int id,
        Guid teacherId,
        Guid? groupId,
        SalaryType salaryType,
        decimal value,
        DateOnly effectiveFrom)
        : base(id)
    {
        TeacherId = teacherId;
        GroupId = groupId;
        SalaryType = salaryType;
        Value = value;
        EffectiveFrom = effectiveFrom;
    }

    public static Result<TeacherSalaryConfig> Create(
        int id,
        Guid teacherId,
        Guid? groupId,
        SalaryType salaryType,
        decimal value,
        DateOnly effectiveFrom)
    {
        if (teacherId == Guid.Empty)
            return TeacherSalaryConfigErrors.TeacherIdRequired;

        if (!Enum.IsDefined(salaryType))
            return TeacherSalaryConfigErrors.InvalidSalaryType;

        if (value <= 0 || value > 999999.99m)
            return TeacherSalaryConfigErrors.ValueOutOfRange;

        if (salaryType == SalaryType.Percentage && value > 100m)
            return TeacherSalaryConfigErrors.PercentageOutOfRange;

        return new TeacherSalaryConfig(id, teacherId, groupId, salaryType, value, effectiveFrom);
    }

    public Result<Updated> Update(
        Guid? groupId,
        SalaryType salaryType,
        decimal value,
        DateOnly effectiveFrom)
    {
        if (!Enum.IsDefined(salaryType))
            return TeacherSalaryConfigErrors.InvalidSalaryType;

        if (value <= 0 || value > 999999.99m)
            return TeacherSalaryConfigErrors.ValueOutOfRange;

        if (salaryType == SalaryType.Percentage && value > 100m)
            return TeacherSalaryConfigErrors.PercentageOutOfRange;

        GroupId = groupId;
        SalaryType = salaryType;
        Value = value;
        EffectiveFrom = effectiveFrom;

        return Result.Updated;
    }
}