namespace Centerix.SecurityTests;

using System.Security.Claims;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Authorization;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Common;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>
/// T22 CORRECTION — the core regression proof for the platform/tenant permission scope boundary.
/// <para>
/// These tests drive the REAL <see cref="PermissionAuthorizationHandler"/> (no mocked handler, no
/// mocked <see cref="IPlatformAdminVerifier"/>) through the real ASP.NET authorization service, and
/// they deliberately CORRUPT the permission database by injecting PLATFORM permissions into a
/// TENANT role. The invariant under test is Invariant A: a platform permission can never be
/// authorized through any tenant-derived source.
/// </para>
/// <para>
/// <see cref="Handler_PlatformPermission_NotPlatformAdmin_Denied_EvenWithTenantRoleGrantAndAuthorizedTenant"/>
/// is the critical case: it reproduces the exact defect that existed before the correction. In that
/// state the handler consulted the tenant fallback for an arbitrary permission, so a TenantAdmin
/// whose role had been granted <c>Plans.Read</c> was ALLOWED. The test now asserts DENY.
/// </para>
/// </summary>
[Trait("Category", "T22Scope")]
public class Task22_PermissionScopeBoundaryTests
{
    // ==================================================================
    // INVARIANT A — platform permission is never a tenant permission
    // ==================================================================

    [Fact]
    public async Task Handler_PlatformPermission_NotPlatformAdmin_Denied_EvenWithTenantRoleGrantAndAuthorizedTenant()
    {
        // The strongest possible tenant-side position:
        //   - active TenantMembership in the tenant
        //   - a DELIBERATELY INJECTED platform RolePermission (Plans.Read) on that tenant role
        //   - the permission is ALSO pre-published in HttpContext.Items["TenantPermissions"]
        //   - the tenant context is fully AUTHORIZED (IsAuthorized == true, TenantId populated)
        //   - a tenant request header is present
        // Every tenant-derived grant source is armed. The only correct answer is DENY.
        var env = await ScopeTestEnvironment.CreateAsync(grantPlansReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlainTenantUser();
        env.PublishTenantPermissions(Permissions.Plans.Read, Permissions.Students.Read);
        env.AuthorizeTenant();

        var result = await env.EvaluateAsync(Permissions.Plans.Read);

        Assert.False(result, "A PLATFORM permission must NEVER be authorized via a tenant role grant, "
            + "even with an authorized tenant context, an active membership and a pre-published "
            + "tenant permission list.");
    }

    [Fact]
    public async Task Handler_PlatformPermission_NotPlatformAdmin_Denied_OnEveryPlatformCode()
    {
        // Scope enforcement must be GENERIC: it protects every current and future platform
        // permission, not just the one the previous test happened to use.
        var env = await ScopeTestEnvironment.CreateAsync(grantPlansReadToTenantAdmin: false);
        using var _ = env;

        env.SignInAsPlainTenantUser();
        env.AuthorizeTenant();

        foreach (var code in Permissions.PlatformScope.PermissionCodes)
        {
            var result = await env.EvaluateAsync(code);
            Assert.False(result, $"Platform-scoped permission '{code}' was authorized without a "
                + "PlatformAdmin decision.");
        }
    }

    [Fact]
    public async Task Handler_PlatformPermission_ValidPlatformAdmin_Allowed()
    {
        // The fix must not break the legitimate platform path (P1).
        var env = await ScopeTestEnvironment.CreateAsync(grantPlansReadToTenantAdmin: false);
        using var _ = env;

        env.SignInAsPlatformAdmin();

        var result = await env.EvaluateAsync(Permissions.Plans.Read);

        Assert.True(result, "A verified PlatformAdmin must still be authorized for platform permissions.");
    }

    // ==================================================================
    // INVARIANT D — fail closed on unknown scope / unknown permission
    // ==================================================================

    [Theory]
    [InlineData("Totally.Made.Up.Permission")]
    [InlineData("")]
    [InlineData("plans.read ")]
    public async Task Handler_UnknownPermission_Denied_ForNonAdmin(string permission)
    {
        // An unclassifiable permission must not be authorized from any TENANT-derived source.
        // (A verified PlatformAdmin retains the platform-wide bypass by design — see
        // Handler_PlatformPermission_ValidPlatformAdmin_Allowed — so this asserts the
        // fail-closed rule for everyone else.)
        var env = await ScopeTestEnvironment.CreateAsync(grantPlansReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlainTenantUser();
        env.AuthorizeTenant();
        env.PublishTenantPermissions(permission);

        var result = await env.EvaluateAsync(permission);

        Assert.False(result, $"Unknown permission '{permission}' must be denied (fail-closed).");
    }

    [Fact]
    public void Scope_Classifier_IsTotal_NoUnknownScopeForAnyCatalogPermission()
    {
        // Section 20: every permission must have a determinate scope. An Unknown scope for a
        // catalog permission would be an ambiguity that must block the release.
        var classification = PermissionScopes.GetCatalogClassification();

        Assert.NotEmpty(classification);
        Assert.DoesNotContain(classification, c => c.Scope == PermissionScope.Unknown);
    }

    [Fact]
    public void Scope_Classifier_CatalogIsDisjoint_AndNoPermissionIsBothScopes()
    {
        // There is no "Both" scope by construction: a permission is either platform or tenant.
        var platform = Permissions.PlatformScope.PermissionCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = PermissionCatalog.All.Select(e => e.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var both = platform.Intersect(catalog)
            .Where(code => PermissionScopes.Resolve(code) != PermissionScope.Platform)
            .ToList();

        Assert.Empty(both);
    }

    [Fact]
    public void Scope_Classifier_PlatformCodeResolvesToPlatform_EvenWhenAbsentFromCatalog()
    {
        // PlatformUsers.*/PlatformRoles.*/PlatformPermissions.Read are enforced by controllers but
        // are not yet rows in PermissionCatalog. They must still classify as Platform (never as
        // Unknown, and certainly never as Tenant), so the boundary holds for them too.
        foreach (var code in new[]
        {
            Permissions.PlatformUsers.Read,
            Permissions.PlatformRoles.Create,
            Permissions.PlatformRoles.Delete,
            Permissions.PlatformPermissions.Read,
        })
        {
            Assert.Equal(PermissionScope.Platform, PermissionScopes.Resolve(code));
            Assert.True(PermissionScopes.IsPlatformScoped(code));
        }
    }

    [Fact]
    public void Scope_Classifier_CaseInsensitive_AndTenantCodesResolveToTenant()
    {
        Assert.Equal(PermissionScope.Platform, PermissionScopes.Resolve("PLANS.read"));
        Assert.Equal(PermissionScope.Platform, PermissionScopes.Resolve("plans.READ"));
        Assert.Equal(PermissionScope.Tenant, PermissionScopes.Resolve(Permissions.Students.Read));
        Assert.Equal(PermissionScope.Tenant, PermissionScopes.Resolve(Permissions.Invoices.Read));
    }

    // ==================================================================
    // INVARIANT C — tenant permissions require an AUTHORIZED tenant context
    // ==================================================================

    [Fact]
    public async Task Handler_TenantPermission_AuthorizedTenant_WithRoleGrant_Allowed()
    {
        // T1: the legitimate tenant path must keep working (no regression).
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlainTenantUser();
        env.AuthorizeTenant();

        var result = await env.EvaluateAsync(Permissions.Students.Read);

        Assert.True(result, "A tenant permission with an authorized tenant context and a role grant must be allowed.");
    }

    [Fact]
    public async Task Handler_TenantPermission_ResolvedButNotAuthorized_Denied()
    {
        // The heart of Invariant C: IsResolved must never be treated as authorization.
        // Here Finbuckle resolved a tenant, but AuthorizeTenant() was never called.
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlainTenantUser();
        env.ResolveTenantWithoutAuthorizing();

        Assert.True(env.CurrentTenant.IsResolved, "Precondition: the tenant IS resolved.");
        Assert.False(env.CurrentTenant.IsAuthorized, "Precondition: the tenant is NOT authorized.");

        var result = await env.EvaluateAsync(Permissions.Students.Read);

        Assert.False(result, "A merely RESOLVED tenant must not authorize a tenant permission.");
    }

    [Fact]
    public async Task Handler_TenantPermission_NoMembership_Denied()
    {
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlainTenantUser();
        env.AuthorizeTenant();
        env.RemoveMembership();

        var result = await env.EvaluateAsync(Permissions.Students.Read);

        Assert.False(result, "A tenant permission without an active membership must be denied.");
    }

    [Fact]
    public async Task Handler_TenantPermission_InactiveMembership_Denied()
    {
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlainTenantUser();
        env.AuthorizeTenant();
        env.SuspendMembership();

        var result = await env.EvaluateAsync(Permissions.Students.Read);

        Assert.False(result, "A tenant permission with an INACTIVE membership must be denied.");
    }

    // ==================================================================
    // INVARIANT B — PlatformAdmin is authoritative, not the JWT claim
    // ==================================================================

    [Fact]
    public async Task Handler_ForgedPlatformAdminClaim_WithoutDbRole_Denied()
    {
        // P3: the JWT claims PlatformAdmin but the Identity store never granted it.
        var env = await ScopeTestEnvironment.CreateAsync(grantPlansReadToTenantAdmin: false);
        using var _ = env;

        env.SignIn(RoleConstants.PlatformAdmin, platformAdminInDatabase: false);

        var result = await env.EvaluateAsync(Permissions.Plans.Read);

        Assert.False(result, "A forged PlatformAdmin claim must not authorize a platform permission.");
    }

    [Fact]
    public async Task Handler_PlatformAdminVerifierThrows_FailsClosed()
    {
        // Invariant D: a verifier exception must never be converted to Allow.
        var env = await ScopeTestEnvironment.CreateAsync(grantPlansReadToTenantAdmin: false);
        using var _ = env;

        env.SignInAsPlatformAdmin();
        env.ThrowFromVerifier();

        var result = await env.EvaluateAsync(Permissions.Plans.Read);

        Assert.False(result, "A PlatformAdmin verification failure must fail closed.");
    }

    // ==================================================================
    // Test environment: REAL handler + REAL verifier + REAL EF store
    // ==================================================================

    private sealed class ScopeTestEnvironment : IDisposable
    {
        private const string TenantId = "t22scope-tenant";

        private readonly ServiceProvider _root;

        // One long-lived request scope, mirroring a single HTTP request: ICurrentTenant is scoped,
        // so AuthorizeTenant() and the authorization handler must observe the SAME instance.
        private IServiceScope _requestScope;

        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly InMemoryUserManager _userManager;
        private readonly FakeMultiTenantAccessor _tenantAccessorState;

        private ScopeTestEnvironment(
            ServiceProvider root,
            IHttpContextAccessor httpContextAccessor,
            InMemoryUserManager userManager,
            FakeMultiTenantAccessor tenantAccessor)
        {
            _root = root;
            _requestScope = root.CreateScope();
            _httpContextAccessor = httpContextAccessor;
            _userManager = userManager;
            _tenantAccessorState = tenantAccessor;
        }

        private IServiceProvider RequestServices => _requestScope.ServiceProvider;

        public ICurrentTenant CurrentTenant => RequestServices.GetRequiredService<ICurrentTenant>();

        public static async Task<ScopeTestEnvironment> CreateAsync(
            bool grantPlansReadToTenantAdmin = false,
            bool grantStudentsReadToTenantAdmin = true)
        {
            var userManager = new InMemoryUserManager();

            // The InMemory database name MUST be computed once, outside the options lambda: the
            // lambda runs once per DI scope, so a Guid generated inside it would hand every scope a
            // different (empty) database and silently break seeded-data visibility.
            var databaseName = $"T22Scope_{Guid.NewGuid():N}";

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Substitute.For<IMediator>());
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(databaseName));

            // The handler resolves IAppDbContext (not the concrete context) for its DB fallback.
            services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

            // Real Identity registration, exactly as production wires it, so the handler's
            // RoleManager<ApplicationRole> resolution is genuine rather than a stub.
            services
                .AddIdentityCore<IdentityUser>()
                .AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<AppDbContext>();
            var tenantAccessor = new FakeMultiTenantAccessor();
            services.AddSingleton<IMultiTenantContextAccessor<CenterixTenantInfo>>(tenantAccessor);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // The REAL production CurrentTenant, scoped. The handler and AppDbContext must observe
            // the same instance so AuthorizeTenant() is visible to the authorization decision.
            services.AddScoped<ICurrentTenant, CurrentTenant>();

            // The REAL PlatformAdminVerifier runs against a controllable identity store, so these
            // tests exercise production verification logic rather than a mocked Allow/deny answer.
            services.AddSingleton<IPlatformAdminVerifier>(
                sp => new PlatformAdminVerifier(userManager, NullLogger<PlatformAdminVerifier>.Instance));

            services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
            services.AddSingleton<IOptions<AuthorizationOptions>>(Options.Create(new AuthorizationOptions()));
            services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
            services.AddAuthorization();

            var provider = services.BuildServiceProvider(validateScopes: true);
            var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();

            var env = new ScopeTestEnvironment(provider, httpContextAccessor, userManager, tenantAccessor);
            await env.SeedAsync(grantPlansReadToTenantAdmin, grantStudentsReadToTenantAdmin);
            return env;
        }

        private async Task SeedAsync(bool grantPlansReadToTenantAdmin, bool grantStudentsReadToTenantAdmin)
        {
            // Seed inside the REQUEST scope with the tenant already resolved+authorized, because
            // AppDbContext applies a global tenant query filter on ICurrentTenant.TenantId. Seeding
            // from an unauthorized scope would make the membership invisible to the handler.
            _tenantAccessorState.Resolve(TenantId);
            CurrentTenant.AuthorizeTenant();

            var db = RequestServices.GetRequiredService<AppDbContext>();

            foreach (var entry in PermissionCatalog.All)
            {
                if (!db.Permissions.Any(p => p.Code == entry.Code))
                {
                    db.Permissions.Add(Permission.Create(0, entry.Module, entry.Action, entry.Code, entry.Description).Value);
                }
            }

            // The tenant role under test. Note it is a TENANT role, not a platform role.
            var tenantRole = new ApplicationRole("TenantAdmin")
            {
                Code = "TenantAdmin",
                DisplayName = "Tenant Administrator",
                IsSystem = true,
                NormalizedName = "TENANTADMIN"
            };
            db.Roles.Add(tenantRole);
            await db.SaveChangesAsync();

            if (grantPlansReadToTenantAdmin)
            {
                var plansRead = db.Permissions.Single(p => p.Code == Permissions.Plans.Read);
                db.RolePermissions.Add(RolePermission.Create(tenantRole.Id, plansRead.Id).Value);
            }

            if (grantStudentsReadToTenantAdmin)
            {
                var studentsRead = db.Permissions.Single(p => p.Code == Permissions.Students.Read);
                db.RolePermissions.Add(RolePermission.Create(tenantRole.Id, studentsRead.Id).Value);
            }

            db.TenantMemberships.Add(
                Centerix.Domain.Platform.Tenants.TenantMembership.Create(
                    TestUserId, TenantId, "TenantAdmin",
                    Centerix.Domain.Platform.Tenants.Enums.TenantMembershipStatus.Active).Value);

            await db.SaveChangesAsync();

            _userManager.Add(TestUserId, email: $"{TestUserId}@t22scope.test");

            // Reset the request scope so each test starts from a NEUTRAL tenant state
            // (resolved == false, authorized == false) and must explicitly establish the context
            // it needs. The InMemory store is keyed by database name, so the seeded rows persist
            // across the scope swap.
            _requestScope.Dispose();
            _requestScope = _root.CreateScope();
            _tenantAccessorState.Reset();
        }

        /// <summary>Runs the REAL authorization pipeline for the given permission policy.</summary>
        public async Task<bool> EvaluateAsync(string permission)
        {
            var principal = _httpContextAccessor.HttpContext?.User
                ?? throw new InvalidOperationException("Call SignIn* first.");

            var authService = RequestServices.GetRequiredService<IAuthorizationService>();
            var result = await authService.AuthorizeAsync(principal, null, permission);
            return result.Succeeded;
        }

        public void SignIn(string? role, bool platformAdminInDatabase = true)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, TestUserId),
                new(ClaimTypes.Name, $"{TestUserId}@t22scope.test"),
            };

            if (role is not null)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            if (role == RoleConstants.PlatformAdmin && platformAdminInDatabase)
            {
                _userManager.GrantPlatformAdmin(TestUserId);
            }

            // The handler reads scoped services from HttpContext.RequestServices (as production
            // does after UseAuthorization), so the test context must expose the request scope.
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                RequestServices = RequestServices
            };
            _httpContextAccessor.HttpContext = httpContext;
        }

        public void SignInAsPlatformAdmin() => SignIn(RoleConstants.PlatformAdmin);

        public void SignInAsPlainTenantUser() => SignIn("TenantAdmin");

        /// <summary>Simulates Finbuckle having resolved a tenant from the request, without authorizing it.</summary>
        public void ResolveTenantWithoutAuthorizing()
        {
            var httpContext = _httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("Call SignIn* first.");
            httpContext.Request.Headers["tenant"] = TenantId;
            _tenantAccessorState.Resolve(TenantId);
        }

        /// <summary>Simulates TenantGuardMiddleware completing membership verification.</summary>
        public void AuthorizeTenant()
        {
            var httpContext = _httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("Call SignIn* first.");
            httpContext.Request.Headers["tenant"] = TenantId;
            _tenantAccessorState.Resolve(TenantId);
            CurrentTenant.AuthorizeTenant();
        }

        public void PublishTenantPermissions(params string[] permissions)
        {
            var httpContext = _httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("Call SignIn* first.");
            httpContext.Items["TenantPermissions"] = permissions;
        }

        // NOTE: these run in the REQUEST scope on purpose. AppDbContext applies a global tenant
        // query filter keyed on ICurrentTenant.TenantId, which is only populated once
        // AuthorizeTenant() has run; a fresh scope would be unauthorized and filter the row away.
        public void RemoveMembership()
        {
            var db = RequestServices.GetRequiredService<AppDbContext>();
            var membership = db.TenantMemberships.Single(m => m.UserId == TestUserId);
            db.TenantMemberships.Remove(membership);
            db.SaveChanges();
        }

        public void SuspendMembership()
        {
            var db = RequestServices.GetRequiredService<AppDbContext>();
            var membership = db.TenantMemberships.Single(m => m.UserId == TestUserId);
            membership.Suspend();
            db.SaveChanges();
        }

        public void ThrowFromVerifier() => _userManager.ThrowOnLookup = true;

        public void Dispose()
        {
            _requestScope.Dispose();
            _root.Dispose();
        }

        private const string TestUserId = "t22-scope-user";
    }

    /// <summary>
    /// Minimal <see cref="IMultiTenantContextAccessor{T}"/> stand-in. The real Finbuckle accessor
    /// is async-context bound; this fake makes "resolved" and "authorized" independently controllable
    /// so the IsResolved-vs-IsAuthorized distinction can be tested directly.
    /// </summary>
    private sealed class FakeMultiTenantAccessor : IMultiTenantContextAccessor<CenterixTenantInfo>
    {
        private CenterixTenantInfo? _info;

        public void Resolve(string tenantId) => _info = new CenterixTenantInfo
        {
            Id = tenantId,
            Identifier = tenantId,
            Name = tenantId,
            IsActive = true,
            ValidUpTo = DateTime.MaxValue
        };

        public void Reset() => _info = null;

        // The non-generic IMultiTenantContextAccessor inherited by the generic interface exposes
        // MultiTenantContext as IMultiTenantContext. Both members are satisfied by the same state.
        public IMultiTenantContext<CenterixTenantInfo>? MultiTenantContext =>
            _info is null
                ? null
                : new MultiTenantContext<CenterixTenantInfo> { TenantInfo = _info };

        IMultiTenantContext? IMultiTenantContextAccessor.MultiTenantContext => MultiTenantContext;
    }

    /// <summary>
    /// <see cref="UserManager{TUser}"/> substitute that backs the REAL
    /// <see cref="PlatformAdminVerifier"/> logic (FindById → lockout → IsInRole) without requiring
    /// a full Identity stack, so the test proves the verifier's decision rather than a mocked result.
    /// </summary>
    private sealed class InMemoryUserManager : UserManager<IdentityUser>
    {
        private readonly Dictionary<string, IdentityUser> _users = new();
        private readonly HashSet<string> _platformAdmins = new(StringComparer.Ordinal);

        public bool ThrowOnLookup { get; set; }

        public InMemoryUserManager()
            : base(Substitute.For<IUserStore<IdentityUser>>(), null, null, null, null, null,
                   null, null, null)
        {
        }

        public void Add(string userId, string email)
        {
            _users[userId] = new IdentityUser { Id = userId, Email = email, UserName = email };
        }

        public void GrantPlatformAdmin(string userId) => _platformAdmins.Add(userId);

        public override Task<IdentityUser?> FindByIdAsync(string userId)
        {
            if (ThrowOnLookup)
            {
                throw new InvalidOperationException("Simulated identity-store failure.");
            }

            return Task.FromResult(_users.TryGetValue(userId, out var u) ? u : null);
        }

        public override Task<bool> IsLockedOutAsync(IdentityUser user)
            => Task.FromResult(false);

        public override Task<bool> IsInRoleAsync(IdentityUser user, string roleName)
            => Task.FromResult(roleName == RoleConstants.PlatformAdmin && _platformAdmins.Contains(user.Id));
    }
}
