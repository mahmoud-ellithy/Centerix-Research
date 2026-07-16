using System.Security.Claims;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Centerix.Infrastructure.Data;

public class ApplicationDbContextInitialiser(
    ILogger<ApplicationDbContextInitialiser> logger,
    AppDbContext context,
    UserManager<IdentityUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IMultiTenantContextAccessor<CenterixTenantInfo> tenantInfoContextAccessor)
{
    private readonly ILogger<ApplicationDbContextInitialiser> _logger = logger;
    private readonly AppDbContext _context = context;
    private readonly UserManager<IdentityUser> _userManager = userManager;
    private readonly RoleManager<IdentityRole> _roleManager = roleManager;
    private readonly IMultiTenantContextAccessor<CenterixTenantInfo> _tenantInfoContextAccessor = tenantInfoContextAccessor;

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initialising the database.");
            throw;
        }
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TrySeedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while seeding the database.");
            throw;
        }
    }

    private async Task TrySeedAsync()
    {
        // Default Roles > Assign Permissions/claims
        await InitializeDefaultRolesAsync();
        // Admin user (from the current tenant) > Assign Role
        await InitializeAdminUserAsync();
    }

    private async Task InitializeDefaultRolesAsync()
    {
        var isRootTenant = _tenantInfoContextAccessor.MultiTenantContext.TenantInfo?.Id == TenancyConstants.Root.Id;

        // PlatformAdmin role — full access to everything (root tenant only)
        if (isRootTenant)
        {
            var platformAdminRole = await EnsureRoleAsync(RoleConstants.PlatformAdmin);
            await AssignPermissionsToRoleAsync(platformAdminRole, Permissions.GetPlatformAdminPermissions());
        }

        // TenantAdmin role — full CRUD on tenant resources
        var tenantAdminRole = await EnsureRoleAsync(RoleConstants.TenantAdmin);
        await AssignPermissionsToRoleAsync(tenantAdminRole, Permissions.GetTenantAdminPermissions());

        // TenantUser role — read-only access to tenant resources
        var tenantUserRole = await EnsureRoleAsync(RoleConstants.TenantUser);
        await AssignPermissionsToRoleAsync(tenantUserRole, Permissions.GetTenantUserPermissions());
    }

    private async Task<IdentityRole> EnsureRoleAsync(string roleName)
    {
        if (await _roleManager.Roles.SingleOrDefaultAsync(r => r.Name == roleName) is not IdentityRole role)
        {
            role = new IdentityRole(roleName);
            await _roleManager.CreateAsync(role);
        }

        return role;
    }

    private async Task InitializeAdminUserAsync()
    {
        var tenantInfo = _tenantInfoContextAccessor.MultiTenantContext.TenantInfo;

        if (tenantInfo is null || string.IsNullOrEmpty(tenantInfo.Email))
        {
            return;
        }

        var adminRole = tenantInfo.Id == TenancyConstants.Root.Id
            ? RoleConstants.PlatformAdmin
            : RoleConstants.TenantAdmin;

        if (await _userManager.Users.SingleOrDefaultAsync(u => u.Email == tenantInfo.Email) is not IdentityUser adminUser)
        {
            adminUser = new IdentityUser
            {
                Email = tenantInfo.Email,
                UserName = tenantInfo.Email,
                EmailConfirmed = true,
                PhoneNumberConfirmed = true,
                NormalizedEmail = tenantInfo.Email.ToUpperInvariant(),
                NormalizedUserName = tenantInfo.Email.ToUpperInvariant()
            };

            var passwordHasher = new PasswordHasher<IdentityUser>();
            adminUser.PasswordHash = passwordHasher.HashPassword(adminUser, TenancyConstants.DefaultPassword);

            await _userManager.CreateAsync(adminUser);
        }

        if (!await _userManager.IsInRoleAsync(adminUser, adminRole))
        {
            await _userManager.AddToRoleAsync(adminUser, adminRole);
        }
    }

    private async Task AssignPermissionsToRoleAsync(IdentityRole role, string[] permissions)
    {
        var currentClaims = await _roleManager.GetClaimsAsync(role);

        foreach (var permission in permissions)
        {
            if (!currentClaims.Any(c => c.Type == Permissions.ClaimType && c.Value == permission))
            {
                await _roleManager.AddClaimAsync(role, new Claim(Permissions.ClaimType, permission));
            }
        }
    }
}
