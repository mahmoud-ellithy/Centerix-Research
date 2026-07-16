using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Centerix.Infrastructure.Auth;

public class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var policy = await _fallback.GetPolicyAsync(policyName);
        if (policy != null)
            return policy;

        return new AuthorizationPolicyBuilder()
            .RequireClaim(Permissions.ClaimType, policyName)
            .Build();
    }

    public Task<AuthorizationPolicy?> GetDefaultPolicyAsync()
        => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        => Task.FromResult<AuthorizationPolicy?>(null);
}
