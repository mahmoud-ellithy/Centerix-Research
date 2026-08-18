using Centerix.Application.Common.Interfaces;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;

namespace Centerix.Infrastructure.Common;

/// <summary>
/// Default <see cref="ICurrentTenant"/>. Backed by Finbuckle's <see cref="IMultiTenantContextAccessor"/>
/// for the RESOLVED tenant, and holds the AUTHORIZED tenant once <see cref="AuthorizeTenant"/> is called
/// by the request pipeline (typically <c>TenantGuardMiddleware</c>).
/// </summary>
public class CurrentTenant(IMultiTenantContextAccessor<CenterixTenantInfo> multiTenantContextAccessor) : ICurrentTenant
{
    private readonly IMultiTenantContextAccessor<CenterixTenantInfo> _multiTenantContextAccessor = multiTenantContextAccessor;

    private string? _authorizedTenantId;
    private bool _isAuthorized;

    // AUTHORIZED / VERIFIED tenant context — the single source of truth for tenant-aware data access.
    // Empty until the request has been authorized (fail-closed), so any IHasTenantId query returns nothing
    // until the guard establishes the verified context.
    public string TenantId => _isAuthorized ? _authorizedTenantId! : string.Empty;

    // RESOLVED (client-selected) tenant — selection input only; never trusted for authorization.
    public string ResolvedTenantId => _multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.Id ?? string.Empty;

    public bool IsAuthorized => _isAuthorized;

    public bool IsResolved => _multiTenantContextAccessor.MultiTenantContext?.TenantInfo != null;

    public bool IsActive => _multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.IsActive ?? false;

    public DateTime ValidUpTo => _multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.ValidUpTo ?? DateTime.MinValue;

    /// <summary>
    /// Establishes the verified tenant context by locking the currently resolved tenant as authorized
    /// for this request. Must only be called after membership/authorization has been confirmed.
    /// </summary>
    public void AuthorizeTenant()
    {
        _authorizedTenantId = ResolvedTenantId;
        _isAuthorized = true;
    }
}
