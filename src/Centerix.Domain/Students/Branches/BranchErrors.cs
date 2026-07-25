namespace Centerix.Domain.Students.Branches;

using Centerix.Domain.Common.Results;

public static class BranchErrors
{
    public static Error NameRequired =>
        Error.Validation("Branch.Name_Required", "Branch name is required");

    public static Error InvalidPhone =>
        Error.Validation("Branch.InvalidPhone", "Branch phone must be 7-15 digits and may start with '+'");

    public static Error NotFound =>
        Error.NotFound("Branch.NotFound", "Branch was not found");

    public static Error AlreadyDeleted =>
        Error.Conflict("Branch.AlreadyDeleted", "Branch is already deleted");
}
