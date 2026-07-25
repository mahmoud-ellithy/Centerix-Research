namespace Centerix.Domain.Students.Enums;

/// <summary>
/// Lifecycle status of a student enrollment.
/// </summary>
public enum StudentStatus : byte
{
    Active = 0,
    Inactive = 1,
    Graduated = 2,
    Withdrawn = 3,
    Suspended = 4,
    Transferred = 5,
}
