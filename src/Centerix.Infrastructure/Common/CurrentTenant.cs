using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;

namespace Centerix.Infrastructure.Common;

public class CurrentTenant(IMultiTenantContextAccessor<CenterixTenantInfo> multiTenantContextAccessor) : ICurrentTenant
{
    private readonly IMultiTenantContextAccessor<CenterixTenantInfo> _multiTenantContextAccessor = multiTenantContextAccessor;

    public string TenantId => _multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.Id ?? string.Empty;

    public bool IsResolved => _multiTenantContextAccessor.MultiTenantContext?.TenantInfo != null;

    public bool IsActive => _multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.IsActive ?? false;

    public DateTime ValidUpTo => _multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.ValidUpTo ?? DateTime.MinValue;
}