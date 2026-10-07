using System.Security.Claims;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Centerix.Infrastructure.Data;

public class ApplicationDbContextInitialiser(
    ILogger<ApplicationDbContextInitialiser> logger,
    AppDbContext context,
    UserManager<IdentityUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IMultiTenantContextAccessor<CenterixTenantInfo> tenantInfoContextAccessor,
    IHostEnvironment hostEnvironment,
    IOptions<DatabaseInitializationOptions> dbInitOptions,
    IOptions<BootstrapAdminOptions> bootstrapAdminOptions)
{
    private readonly ILogger<ApplicationDbContextInitialiser> _logger = logger;
    private readonly AppDbContext _context = context;
    private readonly UserManager<IdentityUser> _userManager = userManager;
    private readonly RoleManager<ApplicationRole> _roleManager = roleManager;
    private readonly IMultiTenantContextAccessor<CenterixTenantInfo> _tenantInfoContextAccessor = tenantInfoContextAccessor;
    private readonly IHostEnvironment _hostEnvironment = hostEnvironment;
    private readonly DatabaseInitializationOptions _dbInitOptions = dbInitOptions.Value;
    private readonly BootstrapAdminOptions _bootstrapAdminOptions = bootstrapAdminOptions.Value;

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
        // REQUIRED seed (NEW-2): deterministic + idempotent, runs in every environment
        // when DatabaseInitialization:Seed is enabled. Never includes development-only
        // bootstrap credentials — those are gated separately on SeedDevelopmentData.
        // Permission catalog (global) > Roles > RolePermission assignments per tenant.
        await SeedPermissionCatalogAsync();
        // Default Roles > Assign Permissions via RolePermission rows
        await InitializeDefaultRolesAsync();
        // Required platform singleton: reconciliation fail-fasts without it.
        await EnsureSubscriptionPolicySeedAsync();
        // C2: Ensure Platform.Tenants entry exists for the root tenant
        await EnsureRootTenantEntityAsync();
        // Development-only bootstrap admin (NEW-1/NEW-2): skipped in production unless
        // DatabaseInitialization:SeedDevelopmentData is explicitly enabled, in which
        // case the temporary password MUST come from BootstrapAdmin configuration —
        // a static/default password is never created.
        await InitializeAdminUserAsync();
    }

    /// <summary>
    /// Required deterministic seed (NEW-2): single-row SubscriptionPolicy with the
    /// platform default grace period. Idempotent: existing row is left untouched so an
    /// operator-tuned value is never overwritten by a restart.
    /// </summary>
    private async Task EnsureSubscriptionPolicySeedAsync()
    {
        if (await _context.SubscriptionPolicies.AnyAsync())
            return;

        var result = SubscriptionPolicy.Create(id: 1, gracePeriodDays: 7);
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Failed to seed required SubscriptionPolicy: {string.Join(", ", result.Errors!.Select(e => e.Code))}");

        await _context.SubscriptionPolicies.AddAsync(result.Value);
        await _context.SaveChangesAsync();
    }

    private async Task SeedPermissionCatalogAsync()
    {
        var existingCodes = await _context.Permissions
            .Select(p => p.Code)
            .ToListAsync();

        var existingSet = new HashSet<string>(existingCodes, StringComparer.Ordinal);

        foreach (var entry in PermissionCatalog.All)
        {
            if (existingSet.Contains(entry.Code))
            {
                continue;
            }

            var permission = Permission.Create(
                id: 0,
                module: entry.Module,
                action: entry.Action,
                code: entry.Code,
                description: entry.Description);

            if (permission.IsSuccess)
            {
                await _context.Permissions.AddAsync(permission.Value);
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task InitializeDefaultRolesAsync()
    {
        var isRootTenant = _tenantInfoContextAccessor.MultiTenantContext.TenantInfo?.Id == TenancyConstants.Root.Id;

        // PlatformAdmin role — full access to everything (root tenant only)
        if (isRootTenant)
        {
            var platformAdminRole = await EnsureRoleAsync(RoleConstants.PlatformAdmin, "Platform Administrator", isSystem: true);
            await AssignPermissionsToRoleAsync(platformAdminRole, Permissions.GetPlatformAdminPermissions());
        }

        // TenantAdmin role — full CRUD on tenant resources
        var tenantAdminRole = await EnsureRoleAsync(RoleConstants.TenantAdmin, "Tenant Administrator", isSystem: true);
        await AssignPermissionsToRoleAsync(tenantAdminRole, Permissions.GetTenantAdminPermissions());

        // TenantUser role — read-only access to tenant resources
        var tenantUserRole = await EnsureRoleAsync(RoleConstants.TenantUser, "Tenant User", isSystem: true);
        await AssignPermissionsToRoleAsync(tenantUserRole, Permissions.GetTenantUserPermissions());
    }

    private async Task<ApplicationRole> EnsureRoleAsync(string code, string displayName, bool isSystem)
    {
        if (await _roleManager.Roles.SingleOrDefaultAsync(r => r.Name == code) is not ApplicationRole role)
        {
            role = new ApplicationRole(code)
            {
                Code = code,
                DisplayName = displayName,
                IsSystem = isSystem,
                NormalizedName = code.ToUpperInvariant()
            };
            await _roleManager.CreateAsync(role);
        }
        else
        {
            // Backfill metadata for roles created before Code/DisplayName/IsSystem existed.
            var changed = false;
            if (role.Code != code)
            {
                role.Code = code;
                changed = true;
            }

            if (role.DisplayName != displayName)
            {
                role.DisplayName = displayName;
                changed = true;
            }

            if (role.IsSystem != isSystem)
            {
                role.IsSystem = isSystem;
                changed = true;
            }

            if (changed)
            {
                await _roleManager.UpdateAsync(role);
            }
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

        // NEW-2: development-only/bootstrap data must never reach production unless an
        // operator explicitly opts in via DatabaseInitialization:SeedDevelopmentData.
        if (_hostEnvironment.IsProduction() && !_dbInitOptions.SeedDevelopmentData)
        {
            _logger.LogInformation(
                "Skipping bootstrap admin seed for {Email} in Production (SeedDevelopmentData is disabled).",
                tenantInfo.Email);
            return;
        }

        var isProduction = _hostEnvironment.IsProduction();

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

            // NEW-1: no static/default password anywhere. Production with development
            // seed explicitly enabled MUST supply BootstrapAdmin:TemporaryPassword via
            // configuration/secret store; startup fails clearly otherwise. Everywhere
            // else a fresh cryptographically random password is generated per user.
            string temporaryPassword;
            if (isProduction)
            {
                if (string.IsNullOrWhiteSpace(_bootstrapAdminOptions.TemporaryPassword))
                    throw new InvalidOperationException(
                        "BootstrapAdmin:TemporaryPassword must be configured (environment variable or secret store) " +
                        "when DatabaseInitialization:SeedDevelopmentData is enabled in Production. " +
                        "Refusing to create a bootstrap admin without an explicitly configured temporary password.");

                temporaryPassword = _bootstrapAdminOptions.TemporaryPassword;
            }
            else if (!string.IsNullOrWhiteSpace(_bootstrapAdminOptions.TemporaryPassword))
            {
                temporaryPassword = _bootstrapAdminOptions.TemporaryPassword;
            }
            else
            {
                temporaryPassword = TenancyConstants.GenerateTemporaryPassword();
            }

            // Run Identity password validators (never bypass by writing PasswordHash
            // directly): a weak explicitly-configured password fails startup loudly.
            var createResult = await _userManager.CreateAsync(adminUser, temporaryPassword);
            if (!createResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to create bootstrap admin {tenantInfo.Email}: " +
                    string.Join("; ", createResult.Errors.Select(e => e.Description)));

            await _userManager.AddClaimAsync(adminUser, new Claim("password.change_required", "true"));

            if (isProduction)
                logger.LogWarning(
                    "Created bootstrap admin {Email} from explicitly configured temporary password. " +
                    "Force password change required on first login via POST /api/auth/change-password.",
                    tenantInfo.Email);
            else
                logger.LogInformation(
                    "Created bootstrap admin {Email} with a generated temporary password. " +
                    "Force password change required on first login via POST /api/auth/change-password.",
                    tenantInfo.Email);
        }

        if (!await _userManager.IsInRoleAsync(adminUser, adminRole))
        {
            await _userManager.AddToRoleAsync(adminUser, adminRole);
        }

        // C1 fix: record the tenant's admin user as an ACTIVE member of this tenant so the
        // TenantGuardMiddleware membership check authorizes legitimate owners. Idempotent:
        // skips when a membership already exists (e.g. migration backfill or re-seeding).
        //
        // SEC-001: the Identity role above and the TENANT MEMBERSHIP role are different axes.
        // The root tenant's administrator still holds the Identity PlatformAdmin role (that is
        // what satisfies platform-scoped endpoints via IPlatformAdminVerifier), but a
        // TenantMembership row is a tenant-scoped assertion and must carry a tenant role -
        // otherwise the tenant permission resolver would publish the platform role's entire
        // permission set as a tenant-derived grant.
        if (!await _context.TenantMemberships.AnyAsync(
                m => m.UserId == adminUser.Id && m.TenantId == tenantInfo.Id))
        {
            var membership = TenantMembership.Create(
                adminUser.Id, tenantInfo.Id, RoleConstants.TenantAdmin, TenantMembershipStatus.Active);
            if (membership.IsSuccess)
            {
                await _context.TenantMemberships.AddAsync(membership.Value);
                await _context.SaveChangesAsync();
            }
            else
            {
                _logger.LogWarning(
                    "Could not seed tenant membership for {Email} in {TenantId}: {Errors}",
                    tenantInfo.Email, tenantInfo.Id,
                    string.Join(", ", membership.Errors!.Select(e => e.Code)));
            }
        }
    }

    private async Task AssignPermissionsToRoleAsync(ApplicationRole role, string[] permissions)
    {
        var permissionIds = await _context.Permissions
            .Where(p => permissions.Contains(p.Code))
            .Select(p => p.Id)
            .ToListAsync();

        var existingAssignments = await _context.RolePermissions
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.PermissionId)
            .ToListAsync();

        var existingSet = new HashSet<int>(existingAssignments);

        foreach (var permissionId in permissionIds)
        {
            if (existingSet.Contains(permissionId))
            {
                continue;
            }

            var result = RolePermission.Create(role.Id, permissionId);
            if (result.IsSuccess)
            {
                await _context.RolePermissions.AddAsync(result.Value);
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task EnsureRootTenantEntityAsync()
    {
        var tenantInfo = _tenantInfoContextAccessor.MultiTenantContext.TenantInfo;
        if (tenantInfo is null || tenantInfo.Id != TenancyConstants.Root.Id)
        {
            return;
        }

        if (await _context.Tenants.AnyAsync(t => t.Id == TenancyConstants.Root.GuidId))
        {
            return;
        }

        var tenantResult = Domain.Platform.Tenants.Tenant.Create(
            TenancyConstants.Root.GuidId,
            slug: "root",
            subdomain: "root",
            displayName: "Root",
            country: "EG",
            currency: "EGP",
            timezone: "Africa/Cairo",
            ownerFirstName: TenancyConstants.FirstName,
            ownerLastName: TenancyConstants.LastName,
            ownerEmail: TenancyConstants.Root.Email,
            isolationMode: IsolationMode.Shared);

        if (tenantResult.IsSuccess)
        {
            _context.Tenants.Add(tenantResult.Value);
            await _context.SaveChangesAsync();
        }
    }
}
