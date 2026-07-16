namespace Centerix.Application.Common.Interfaces;

public interface ICurrentTenant
{
    string TenantId { get; }
    bool IsResolved { get; }
    bool IsActive { get; }
    DateTime ValidUpTo { get; }
}