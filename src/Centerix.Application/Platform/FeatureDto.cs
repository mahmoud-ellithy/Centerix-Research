namespace Centerix.Application.Platform;

public class FeatureDto
{
    public int Id { get; set; }
    public string Code { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string Module { get; set; } = default!;
}
