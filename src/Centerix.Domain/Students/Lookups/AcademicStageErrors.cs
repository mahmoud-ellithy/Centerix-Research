namespace Centerix.Domain.Students.Lookups;

using Centerix.Domain.Common.Results;

public static class AcademicStageErrors
{
    public static Error CodeRequired =>
        Error.Validation("AcademicStage.Code_Required", "Stage code is required");

    public static Error DisplayNameRequired =>
        Error.Validation("AcademicStage.DisplayName_Required", "Stage display name is required");

    public static Error NotFound =>
        Error.NotFound("AcademicStage.NotFound", "Academic stage was not found");

    public static Error DuplicateCode =>
        Error.Conflict("AcademicStage.DuplicateCode", "An academic stage with this code already exists");
}

public static class AcademicYearErrors
{
    public static Error StageIdRequired =>
        Error.Validation("AcademicYear.StageId_Required", "Academic stage is required");

    public static Error YearCodeRequired =>
        Error.Validation("AcademicYear.YearCode_Required", "Year code is required");

    public static Error YearNameRequired =>
        Error.Validation("AcademicYear.YearName_Required", "Year name is required");

    public static Error NotFound =>
        Error.NotFound("AcademicYear.NotFound", "Academic year was not found");

    public static Error DuplicateYearCode =>
        Error.Conflict("AcademicYear.DuplicateCode", "An academic year with this code already exists in this stage");
}
