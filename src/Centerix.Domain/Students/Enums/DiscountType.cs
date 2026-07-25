namespace Centerix.Domain.Students.Enums;

/// <summary>
/// How the student's discount value is interpreted.
/// </summary>
public enum DiscountType : byte
{
    None = 0,
    Percentage = 1,
    Fixed = 2,
    Sibling = 3,
    Staff = 4,
    Scholarship = 5,
}
