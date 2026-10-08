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
    IOptions<BootstrapAdminOptions> bootstrapAdminOptions,
    IOptions<PlatformAdminBootstrapOptions> platformAdminBootstrapOptions)
{
    private readonly ILogger<ApplicationDbContextInitialiser> _logger = logger;
    private readonly AppDbContext _context = context;
    private readonly UserManager<IdentityUser> _userManager = userManager;
    private readonly RoleManager<ApplicationRole> _roleManager = roleManager;
    private readonly IMultiTenantContextAccessor<CenterixTenantInfo> _tenantInfoContextAccessor = tenantInfoContextAccessor;
    private readonly IHostEnvironment _hostEnvironment = hostEnvironment;
    private readonly DatabaseInitializationOptions _dbInitOptions = dbInitOptions.Value;
    private readonly BootstrapAdminOptions _bootstrapAdminOptions = bootstrapAdminOptions.Value;
    private readonly PlatformAdminBootstrapOptions _platformAdminBootstrapOptions = platformAdminBootstrapOptions.Value;

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
        // Production first-PlatformAdmin bootstrap (Batch 2 correction): independent of
        // SeedDevelopmentData, gated on its own PlatformAdminBootstrap:Enabled switch.
        await EnsureFirstPlatformAdminAsync();
    }

    /// <summary>
    /// Required deterministic seed (NEW-2): single-row SubscriptionPolicy with the
    /// platform default grace period. Idempotent: an existing row is left untouched so an
    /// operator-tuned value is never overwritten by a restart. The database generates the
    /// key (IDENTITY) — the seed never assigns an explicit id, which SQL Server rejects.
    /// </summary>
    private async Task EnsureSubscriptionPolicySeedAsync()
    {
        if (await _context.SubscriptionPolicies.AnyAsync())
            return;

        var result = SubscriptionPolicy.Create(id: 0, gracePeriodDays: 7);
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
            // Bootstrap all-or-nothing contract: a failed role creation must fail startup
            // here — never continue to permission assignment or admin creation as if the
            // role existed.
            var createResult = await _roleManager.CreateAsync(role);
            if (!createResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to create role '{code}' during database bootstrap: " +
                    string.Join("; ", createResult.Errors.Select(e => e.Description)));
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
                // Same contract: the role's authoritative metadata could not be guaranteed,
                // so fail startup instead of continuing as if it were correct.
                var updateResult = await _roleManager.UpdateAsync(role);
                if (!updateResult.Succeeded)
                    throw new InvalidOperationException(
                        $"Failed to update role '{code}' during database bootstrap: " +
                        string.Join("; ", updateResult.Errors.Select(e => e.Description)));
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

            // NEW-1 correction: there is no generated/fallback password. A bootstrap
            // admin is created ONLY from an explicitly configured
            // BootstrapAdmin:TemporaryPassword (environment variable / secret store /
            // user-secrets in development). Missing configuration fails startup clearly
            // instead of inventing, logging, or returning a credential.
            if (string.IsNullOrWhiteSpace(_bootstrapAdminOptions.TemporaryPassword))
                throw new InvalidOperationException(
                    "BootstrapAdmin:TemporaryPassword must be configured (environment variable, " +
                    "secret store, or development user-secrets) to seed a bootstrap admin. " +
                    "Refusing to create a bootstrap admin without an explicitly configured temporary password. " +
                    "There is no generated or default password.");

            string temporaryPassword = _bootstrapAdminOptions.TemporaryPassword;

            // Run Identity password validators (never bypass by writing PasswordHash
            // directly): a weak explicitly-configured password fails startup loudly.
            var createResult = await _userManager.CreateAsync(adminUser, temporaryPassword);
            if (!createResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to create bootstrap admin {tenantInfo.Email}: " +
                    string.Join("; ", createResult.Errors.Select(e => e.Description)));

            await _userManager.AddClaimAsync(adminUser, new Claim("password.change_required", "true"));

            logger.LogWarning(
                "Created bootstrap admin {Email} from explicitly configured temporary password. " +
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

    /// <summary>
    /// Production first-PlatformAdmin bootstrap (Batch 2 correction). Operational contract:
    /// <list type="bullet">
    /// <item>Runs automatically at startup during the required seed — no SeedDevelopmentData,
    /// no hardcoded/default password, no separate command.</item>
    /// <item>Disabled by default; an enabled-but-incomplete configuration fails startup
    /// clearly (<see cref="PlatformAdminBootstrapOptions.Validate"/>).</item>
    /// <item>Idempotent: an existing PlatformAdmin with the configured email is left fully
    /// untouched (no password reset, no claim/role rewrite). A second startup is a no-op.</item>
    /// <item>All-or-nothing: every Identity result (create, claim, role) is enforced. A failed
    /// claim/role write compensates the just-created user and fails startup — a partially
    /// configured admin can never silently pass.</item>
    /// <item>Never elevates: when the configured email already belongs to a NON-admin user,
    /// startup fails instead of granting PlatformAdmin through this mechanism.</item>
    /// <item>Creates no tenant membership: platform authority flows from the Identity role
    /// plus <c>IPlatformAdminVerifier</c>, never from a membership row (SEC-001). Tenant
    /// access for the operator is granted later per tenant through the normal invitation
    /// flow.</item>
    /// </list>
    /// </summary>
    private async Task EnsureFirstPlatformAdminAsync()
    {
        var bootstrap = _platformAdminBootstrapOptions;

        if (!bootstrap.Enabled)
        {
            _logger.LogInformation(
                "Skipping first-PlatformAdmin bootstrap (PlatformAdminBootstrap:Enabled is false).");
            return;
        }

        // Fail-closed: missing email/password aborts startup before anything is created.
        bootstrap.Validate();
        var email = bootstrap.Email.Trim();

        if (await _userManager.FindByEmailAsync(email) is IdentityUser existingUser)
        {
            if (!await _userManager.IsInRoleAsync(existingUser, RoleConstants.PlatformAdmin))
                throw new InvalidOperationException(
                    $"PlatformAdmin bootstrap refused: a user with email '{email}' already exists " +
                    "but is not a PlatformAdmin. Granting the platform role through the bootstrap " +
                    "mechanism is not allowed. Either configure PlatformAdminBootstrap:Email with the " +
                    "intended first-admin address or assign the role through an existing PlatformAdmin.");

            _logger.LogInformation(
                "First-PlatformAdmin bootstrap already satisfied for {Email}; leaving the existing account untouched.",
                email);
            return;
        }

        // Self-sufficient: the platform role is ensured here (with its production permission
        // matrix) regardless of which tenant context the seed loop is currently running under.
        var platformAdminRole = await EnsureRoleAsync(RoleConstants.PlatformAdmin, "Platform Administrator", isSystem: true);
        await AssignPermissionsToRoleAsync(platformAdminRole, Permissions.GetPlatformAdminPermissions());

        var adminUser = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };

        // Run Identity password validators (never bypass by writing PasswordHash
        // directly): a weak explicitly-configured password fails startup loudly.
        var createResult = await _userManager.CreateAsync(adminUser, bootstrap.TemporaryPassword);
        if (!createResult.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create first PlatformAdmin {email}: " +
                string.Join("; ", createResult.Errors.Select(e => e.Description)));

        // Micro-correction: EVERY Identity step is enforced — no swallowed IdentityResult.
        // Either the admin is fully created, correctly configured and usable, or bootstrap
        // fails loudly. A failed claim/role write can never leave a partially configured
        // admin behind: the user above was created by THIS run only (an existing admin
        // short-circuits earlier and is never touched), so it is compensated (deleted) and
        // startup still fails.
        var bootstrapComplete = false;
        try
        {
            var claimResult = await _userManager.AddClaimAsync(adminUser, new Claim("password.change_required", "true"));
            if (!claimResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to stamp the forced password-change requirement for first PlatformAdmin {email}: " +
                    string.Join("; ", claimResult.Errors.Select(e => e.Description)));

            var roleResult = await _userManager.AddToRoleAsync(adminUser, RoleConstants.PlatformAdmin);
            if (!roleResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to assign the PlatformAdmin role to first PlatformAdmin {email}: " +
                    string.Join("; ", roleResult.Errors.Select(e => e.Description)));

            bootstrapComplete = true;
        }
        finally
        {
            if (!bootstrapComplete)
            {
                try
                {
                    var compensation = await _userManager.DeleteAsync(adminUser);
                    if (!compensation.Succeeded)
                        _logger.LogError(
                            "PlatformAdmin bootstrap compensation failed for {Email}: {Errors}. Startup will still fail.",
                            email,
                            string.Join("; ", compensation.Errors.Select(e => e.Description)));
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "PlatformAdmin bootstrap compensation threw for {Email}. Startup will still fail.",
                        email);
                }
            }
        }

        logger.LogWarning(
            "Created first PlatformAdmin {Email} via PlatformAdminBootstrap. " +
            "Force password change required on first login via POST /api/auth/change-password.",
            email);
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
