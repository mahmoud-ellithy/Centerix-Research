namespace Centerix.Application.Platform.Staff;

public class PlatformUserDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public bool Is2FAEnabled { get; set; }
    public bool IsActive { get; set; }
}
