using Microsoft.AspNetCore.Authorization;

namespace Centerix.Infrastructure.Auth;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class HasPermissionAttribute(string permission) : AuthorizeAttribute(permission)
{
    public string Permission => Policy!;
}
