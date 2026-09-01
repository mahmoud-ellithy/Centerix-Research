namespace Centerix.Domain.Teachers.Subjects;

using Centerix.Domain.Common.Results;

public static class SubjectErrors
{
    public static Error NameRequired =>
        Error.Validation("Subject.Name_Required", "Subject name is required");

    public static Error StageIdRequired =>
        Error.Validation("Subject.StageId_Required", "Academic stage is required");

    public static Error NotFound =>
        Error.NotFound("Subject.NotFound", "Subject was not found");

    public static Error DuplicateName =>
        Error.Conflict("Subject.DuplicateName", "A subject with this name already exists in this stage");
}