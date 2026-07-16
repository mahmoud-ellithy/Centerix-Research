namespace Centerix.Domain.Common;

public interface IHasTenantId
{
    string? TenantId { get; }
}