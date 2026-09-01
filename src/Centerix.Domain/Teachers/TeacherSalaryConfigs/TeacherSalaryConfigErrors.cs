namespace Centerix.Domain.Teachers.TeacherSalaryConfigs;

using Centerix.Domain.Common.Results;

public static class TeacherSalaryConfigErrors
{
    public static Error TeacherIdRequired =>
        Error.Validation("TeacherSalaryConfig.TeacherId_Required", "Teacher is required");

    public static Error InvalidSalaryType =>
        Error.Validation("TeacherSalaryConfig.InvalidSalaryType", "Invalid salary type");

    public static Error ValueRequired =>
        Error.Validation("TeacherSalaryConfig.Value_Required", "Salary value is required");

    public static Error ValueOutOfRange =>
        Error.Validation("TeacherSalaryConfig.Value_OutOfRange", "Salary value must be greater than zero and within 999999.99");

    public static Error PercentageOutOfRange =>
        Error.Validation("TeacherSalaryConfig.Percentage_OutOfRange", "Percentage salary must be between 0 and 100");

    public static Error EffectiveFromRequired =>
        Error.Validation("TeacherSalaryConfig.EffectiveFrom_Required", "Effective from date is required");

    public static Error NotFound =>
        Error.NotFound("TeacherSalaryConfig.NotFound", "Teacher salary configuration was not found");
}