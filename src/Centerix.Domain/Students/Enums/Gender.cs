namespace Centerix.Domain.Students.Enums;

/// <summary>
/// Student's biological/legal gender (single character stored as string for i18n friendliness).
/// </summary>
public enum Gender : byte
{
    Unknown = 0,
    Male = 1,
    Female = 2,
}
