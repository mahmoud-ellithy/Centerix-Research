namespace Centerix.Application.Platform.Staff;

public class PlatformPermissionDto
{
    public int Id { get; set; }
    public string Module { get; set; } = default!;
    public string Action { get; set; } = default!;
    public string Code { get; set; } = default!;
}
