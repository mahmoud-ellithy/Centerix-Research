namespace Centerix.Application.Tenants;

public class CreateTenantRequest
{
    public string Identifier { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? ConnectionString { get; set; }
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime? ValidUpTo { get; set; }
    public bool IsActive { get; set; } = true;
}