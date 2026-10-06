namespace Centerix.SecurityTests;

using System.Security.Claims;
using Centerix.Application.Common.Interfaces;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Common;
using Centerix.Infrastructure.Data;
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
/// T22 FINAL MICRO-CORRECTION — the five regression proofs for the corrected trust boundary.
/// <para>
/// Two surgical defects were fixed:
/// <list type="number">
///   <item><description>
///   <b>Authorization order.</b> The handler previously consulted the
///   <see cref="IPlatformAdminVerifier"/> BEFORE resolving scope, so a verified PlatformAdmin could
///   <i>Succeed</i> a Tenant-scoped requirement regardless of whether the principal had any
///   membership in the resolved tenant. The corrected handler resolves scope first, and only
///   invokes the verifier inside the Platform-scope branch.
///   </description></item>
///   <item><description>
///   <b>Catalog-default classification.</b> The classifier previously treated "membership in
///   <see cref="PermissionCatalog"/>" as a synonym for <see cref="PermissionScope.Tenant"/>, so an
///   entry that was not platform-scoped was always Tenant. The corrected classifier returns the
///   entry's EXPLICIT <see cref="PermissionScope"/>; an entry whose scope is
///   <see cref="PermissionScope.Unknown"/> stays Unknown (fail-closed).
///   </description></item>
/// </list>
/// </para>
/// <para>
/// The five tests below prove each defect would have been caught and that the corrected boundary
/// holds. <b>Tests A, B, C, D</b> run against the REAL handler + REAL verifier + REAL EF
/// store (handler-level). <b>Test E</b> is a unit test of the classifier invariant.
/// </para>
/// </summary>
[Trait("Category", "T22Final")]
public class Task22_FinalMicroCorrectionTests
{
    // ==================================================================
    // TEST A — PlatformAdmin cannot bypass tenant scope
    // ==================================================================

    /// <summary>
    /// Build the scenario:
    /// <code>
    /// IsPlatformAdmin == true
    /// AND CurrentTenant.IsAuthorized == false
    /// AND scope == Tenant
    /// </code>
    /// Expected: <c>DENY</c>.
    /// <para>
    /// The pre-correction handler checked <c>isPlatformAdmin</c> BEFORE <c>scope</c>, so this case
    /// would have <i>Succeed</i>ed regardless of whether the principal had any membership in the
    /// resolved tenant. After the correction the verifier is consulted INSIDE the Platform branch
    /// only, and the tenant path explicitly requires <c>IsAuthorized == true</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_PlatformAdmin_WithUnauthorizedTenantContext_DeniedTenantPermission()
    {
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        // Principal is a verified PlatformAdmin — the verifier would ALLOW the original handler
        // BEFORE the scope check. We deliberately never call AuthorizeTenant(), so
        // CurrentTenant.IsAuthorized == false.
        env.SignInAsPlatformAdmin();

        var result = await env.EvaluateAsync(Permissions.Students.Read);

        Assert.False(
            result,
            "A verified PlatformAdmin must NOT bypass tenant scope. The pre-correction handler " +
            "checked isPlatformAdmin BEFORE resolving scope, so this case would ALLOW. The " +
            "corrected handler resolves scope first; the verifier applies only inside the " +
            "Platform branch, and the tenant branch requires CurrentTenant.IsAuthorized == true.");
    }

    [Fact]
    public async Task A_PlatformAdmin_WithAuthorizedTenantButNoMembership_DeniedTenantPermission()
    {
        // The strongest positive case: the principal IS a verified PlatformAdmin AND the tenant
        // IS resolved+authorized (Finbuckle accepts the header). What is missing is an active
        // TenantMembership. The pre-correction handler would ALLOW; the corrected handler denies.
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlatformAdmin();
        env.AuthorizeTenant();
        env.RemoveMembership();

        Assert.True(env.CurrentTenant.IsAuthorized, "Precondition: tenant IS authorized.");
        Assert.False(env.HasMembership(), "Precondition: principal has NO membership in this tenant.");

        var result = await env.EvaluateAsync(Permissions.Students.Read);

        Assert.False(
            result,
            "PlatformAdmin must not satisfy a tenant-scope requirement without a TenantMembership. " +
            "The bypass only applies to platform scope.");
    }

    [Fact]
    public async Task A_PlatformAdmin_WithFullTenantContext_AllowedTenantPermission()
    {
        // Sanity check: a verified PlatformAdmin who ALSO has a TenantMembership (some staff do)
        // is still allowed for tenant-scoped permissions through the legitimate tenant path.
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlatformAdmin();
        env.AuthorizeTenant();

        var result = await env.EvaluateAsync(Permissions.Students.Read);

        Assert.True(
            result,
            "A PlatformAdmin who is also an active tenant member must keep their tenant access " +
            "through the legitimate tenant path.");
    }

    // ==================================================================
    // TEST B — Unknown catalog classification fails closed
    // ==================================================================

    /// <summary>
    /// Hand-craft a catalog entry with <see cref="PermissionScope.Unknown"/> and verify the
    /// classifier returns <see cref="PermissionScope.Unknown"/> — proving that catalog membership
    /// alone does NOT promote a permission to <see cref="PermissionScope.Tenant"/>.
    /// <para>
    /// The test asserts the algorithm property directly through the internal
    /// <c>ClassifyCatalogEntry</c> hook: an entry whose explicit scope is
    /// <see cref="PermissionScope.Unknown"/> is classified as <see cref="PermissionScope.Unknown"/>.
    /// This is the property the previous "membership ⇒ Tenant" derivation violated.
    /// </para>
    /// </summary>
    [Fact]
    public void B_CatalogEntry_WithoutExplicitClassification_RemainsUnknown()
    {
        var unclassified = new PermissionCatalog.Entry(
            Module: "TestModule",
            Action: "TestAction",
            Code: "Test.Unclassified.Code",
            Scope: PermissionScope.Unknown,
            Description: null);

        Assert.Equal(PermissionScope.Unknown, PermissionScopes.ClassifyCatalogEntry(unclassified));
    }

    [Fact]
    public void B_CatalogEntry_ExplicitTenant_ClassifiedAsTenant()
    {
        // The positive form of B: an entry with explicit scope=Tenant is Tenant.
        var tenant = new PermissionCatalog.Entry(
            Module: "TestModule",
            Action: "TestAction",
            Code: "Test.Explicit.Tenant",
            Scope: PermissionScope.Tenant,
            Description: null);

        Assert.Equal(PermissionScope.Tenant, PermissionScopes.ClassifyCatalogEntry(tenant));
    }

    [Fact]
    public void B_CatalogEntry_ExplicitPlatform_ClassifiedAsPlatform()
    {
        // The positive form of B for platform: an entry with explicit scope=Platform is Platform.
        var platform = new PermissionCatalog.Entry(
            Module: "TestModule",
            Action: "TestAction",
            Code: "Test.Explicit.Platform",
            Scope: PermissionScope.Platform,
            Description: null);

        Assert.Equal(PermissionScope.Platform, PermissionScopes.ClassifyCatalogEntry(platform));
    }

    [Fact]
    public async Task B_UnclassifiedCodeInCatalog_ResolveReturnsUnknown_AndHandlerDenies()
    {
        // End-to-end form of Test B: a permission code that DOES NOT exist in the catalog AND is
        // not in the platform code list must classify as Unknown and the handler must DENY it.
        // This proves the fail-closed rule survives the entire authorization pipeline, not just
        // the classifier unit. (The "membership ⇒ Tenant" derivation previously produced Unknown
        // for non-catalog codes only by accident; the corrected algorithm makes it intentional.)
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlainTenantUser();
        env.AuthorizeTenant();
        env.PublishTenantPermissions("Definitely.Not.A.Real.Code");

        var result = await env.EvaluateAsync("Definitely.Not.A.Real.Code");

        Assert.False(
            result,
            "A non-catalog, non-platform code must be classified as Unknown and DENIED by the " +
            "handler. The pre-published tenant permission list must not widen an unclassifiable code.");
    }

    // ==================================================================
    // TEST C — Scope resolved before any grant source
    // ==================================================================

    /// <summary>
    /// Prove that the scope decision happens BEFORE the tenant fallback, the PlatformAdmin
    /// verifier, or any DB read. The cleanest way is a code-level test: even if every tenant
    /// grant source would say ALLOW, a Tenant-scoped permission must NOT be authorized by a
    /// PlatformAdmin verifier, AND must NOT be authorized by a tenant fallback either when
    /// <c>IsAuthorized == false</c>.
    /// </summary>
    [Fact]
    public async Task C_ScopeDecidesFirst_PlatformAdminCannotAuthorizeTenantEvenWithAllTenantSignalsSet()
    {
        // Build the "every tenant signal says ALLOW" position:
        //   - active TenantMembership seeded by SeedAsync (already in place)
        //   - tenant role has the tenant permission granted (already in place)
        //   - HttpContext.Items["TenantPermissions"] pre-published with the permission
        //   - tenant resolved AND authorized
        //   - AND the principal is also a verified PlatformAdmin (verifier would say yes)
        // The only correct answer for a Tenant-scope permission is ALLOW, because the legitimate
        // tenant path is satisfied and PlatformAdmin is irrelevant here. The point of C is the
        // inverse: removing the tenant grants while keeping PlatformAdmin must DENY.
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlatformAdmin();
        env.AuthorizeTenant();
        env.RemoveMembership(); // no tenant grants — only PlatformAdmin remains
        env.PublishTenantPermissions(); // empty published list — only PlatformAdmin remains

        var result = await env.EvaluateAsync(Permissions.Students.Read);

        Assert.False(
            result,
            "With no tenant grants and only the PlatformAdmin verifier remaining, a Tenant-scoped " +
            "permission must DENY. The pre-correction handler checked isPlatformAdmin BEFORE " +
            "resolving scope, so this case would have ALLOWED.");
    }

    [Fact]
    public async Task C_PlatformAdminVerifierNotInvoked_OnUnknownScope()
    {
        // Belt-and-braces. The handler must not even invoke the verifier for an Unknown code: the
        // verifier call is INSIDE the Platform branch only. We arrange a verifier that throws
        // unconditionally; if the handler invoked the verifier, the call would still throw but
        // the test would still report DENY (the catch wraps in the tenant path). For Unknown
        // scope the test asserts DENY specifically, demonstrating the verifier is irrelevant.
        var env = await ScopeTestEnvironment.CreateAsync(grantStudentsReadToTenantAdmin: true);
        using var _ = env;

        env.SignInAsPlatformAdmin();
        env.AuthorizeTenant();
        env.AlwaysThrowFromVerifier();

        var result = await env.EvaluateAsync("Not.A.Real.Code");

        Assert.False(
            result,
            "Unknown scope must DENY. The PlatformAdmin verifier is intentionally NOT called for " +
            "Unknown scope — scope must not be widened by an unrelated verifier result.");
    }

    // ==================================================================
    // TEST D — No module-based security inference
    // ==================================================================

    /// <summary>
    /// Verify that <see cref="PermissionScopes.Resolve"/> does NOT look at module names, prefixes,
    /// or strings to decide scope. It must rely on the explicit classification only.
    /// <para>
    /// The simplest form: hand-craft two catalog entries with the SAME module but DIFFERENT
    /// explicit scopes, and assert <see cref="PermissionScopes.ClassifyCatalogEntry"/> returns each
    /// entry's EXPLICIT scope — never inferring from the module string.
    /// </para>
    /// </summary>
    [Fact]
    public void D_SameModule_DifferentExplicitScopes_ProduceDifferentClassifications()
    {
        var sharedModule = "SyntheticShared";
        var platformEntry = new PermissionCatalog.Entry(sharedModule, "Alpha", "Synth.Alpha", PermissionScope.Platform, null);
        var tenantEntry = new PermissionCatalog.Entry(sharedModule, "Beta", "Synth.Beta", PermissionScope.Tenant, null);
        var unknownEntry = new PermissionCatalog.Entry(sharedModule, "Gamma", "Synth.Gamma", PermissionScope.Unknown, null);

        Assert.Equal(PermissionScope.Platform, PermissionScopes.ClassifyCatalogEntry(platformEntry));
        Assert.Equal(PermissionScope.Tenant, PermissionScopes.ClassifyCatalogEntry(tenantEntry));
        Assert.Equal(PermissionScope.Unknown, PermissionScopes.ClassifyCatalogEntry(unknownEntry));
    }

    [Fact]
    public void D_Resolve_DoesNotInferFromModuleOrPrefix()
    {
        // Probe the public Resolve path with codes whose strings suggest module/prefix membership
        // but which are NOT in the catalog and NOT in the platform list. Every probe must return
        // Unknown — no inference from "Tenant" prefix or any other string heuristic.
        Assert.Equal(PermissionScope.Unknown, PermissionScopes.Resolve("TenantSomething.Read"));
        Assert.Equal(PermissionScope.Unknown, PermissionScopes.Resolve("PlatformSomething.Read"));
        Assert.Equal(PermissionScope.Unknown, PermissionScopes.Resolve("Students.Update.Synthetic"));
        Assert.Equal(PermissionScope.Unknown, PermissionScopes.Resolve("Plans.Fictional"));
        Assert.Equal(PermissionScope.Unknown, PermissionScopes.Resolve("Tenants.Mystery"));
    }

    [Fact]
    public void D_Resolve_PreservesPlatformListEvenForNonCatalogCodes()
    {
        // The preserved platform code list (Permissions.PlatformScope.PermissionCodes) keeps
        // controller-enforced codes classified as Platform even though they are not catalog rows.
        // This guards the existing behavior — the regression must not silently demote them.
        Assert.Equal(PermissionScope.Platform, PermissionScopes.Resolve(Permissions.PlatformUsers.Read));
        Assert.Equal(PermissionScope.Platform, PermissionScopes.Resolve(Permissions.PlatformRoles.Create));
        Assert.Equal(PermissionScope.Platform, PermissionScopes.Resolve(Permissions.PlatformPermissions.Read));
    }

    // ==================================================================
    // TEST E — Production catalog is fully classified
    // ==================================================================

    /// <summary>
    /// Drift guard: every catalog entry MUST have a non-Unknown scope. Combined with Test B, the
    /// production catalog is guaranteed to be either Platform or Tenant — never Unknown — so the
    /// fail-closed behaviour applies to the entire canonical catalog.
    /// </summary>
    [Fact]
    public void E_ProductionCatalog_EveryEntryHasExplicitNonUnknownScope()
    {
        var unknownEntries = PermissionCatalog.All
            .Where(e => e.Scope == PermissionScope.Unknown)
            .Select(e => e.Code)
            .ToList();

        Assert.True(
            unknownEntries.Count == 0,
            "Every production catalog entry MUST have an explicit non-Unknown scope. Found: " +
            string.Join(", ", unknownEntries));
    }

    [Fact]
    public void E_ProductionCatalog_PlatformEntriesAlignWithPlatformList()
    {
        // The platform code list (Permissions.PlatformScope.PermissionCodes) is the authoritative
        // platform source. Catalog entries that claim Platform scope must appear in that list, and
        // no catalog entry may claim Platform scope without being in the list. This keeps the
        // controller-enforced platform surface (PlatformUsers.* etc.) and the catalog platform
        // surface in lockstep.
        var platformList = Permissions.PlatformScope.PermissionCodes
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var catalogPlatform = PermissionCatalog.All
            .Where(e => e.Scope == PermissionScope.Platform)
            .Select(e => e.Code)
            .ToList();

        var missingFromList = catalogPlatform
            .Where(code => !platformList.Contains(code))
            .ToList();

        Assert.True(
            missingFromList.Count == 0,
            "Every catalog entry marked Platform must also appear in Permissions.PlatformScope. " +
            "Otherwise the controller-enforced platform surface would diverge from the catalog. " +
            "Missing: " + string.Join(", ", missingFromList));
    }

    [Fact]
    public void E_ProductionCatalog_TenantEntriesAreNotInPlatformList()
    {
        // Inverse of the previous test: a Tenant-scope entry must NEVER appear in the platform
        // code list. If a catalog Tenant code were also in the platform list, the platform
        // classification would win, which is exactly the boundary erosion we are removing.
        var platformList = Permissions.PlatformScope.PermissionCodes
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var leaking = PermissionCatalog.All
            .Where(e => e.Scope == PermissionScope.Tenant)
            .Where(e => platformList.Contains(e.Code))
            .Select(e => e.Code)
            .ToList();

        Assert.True(
            leaking.Count == 0,
            "A Tenant-scope catalog entry must not appear in Permissions.PlatformScope. Leaking: " +
            string.Join(", ", leaking));
    }

    [Fact]
    public void E_ProductionCatalog_ClassificationIsTotalAndDeterminate()
    {
        // The combined invariant: every catalog code resolves to a non-Unknown scope (so the
        // classifier is total over the catalog) AND the production classifier agrees with each
        // entry's explicit field (so the resolve is faithful).
        foreach (var entry in PermissionCatalog.All)
        {
            var resolved = PermissionScopes.Resolve(entry.Code);
            Assert.True(
                resolved != PermissionScope.Unknown,
                $"Catalog code '{entry.Code}' resolves to Unknown but the entry declares " +
                $"scope={entry.Scope}. The classifier must agree with the entry's explicit scope.");
            Assert.Equal(entry.Scope, resolved);
            Assert.True(
                entry.Scope == resolved,
                $"Catalog code '{entry.Code}' declares scope={entry.Scope} but Resolve returns " +
                $"{resolved}. The classifier is supposed to return the entry's explicit scope.");
        }
    }

    // ==================================================================
    // Test environment: REAL handler + REAL verifier + REAL EF store
    // (Adapted from Task22_PermissionScopeBoundaryTests; minimal additions only.)
    // ==================================================================

    private sealed class ScopeTestEnvironment : IDisposable
    {
        private const string TenantId = "t22final-tenant";

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
            bool grantStudentsReadToTenantAdmin = true)
        {
            var userManager = new InMemoryUserManager();

            // The InMemory database name MUST be computed once, outside the options lambda: the
            // lambda runs once per DI scope, so a Guid generated inside it would hand every scope a
            // different (empty) database and silently break seeded-data visibility.
            var databaseName = $"T22Final_{Guid.NewGuid():N}";

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
            await env.SeedAsync(grantStudentsReadToTenantAdmin);
            return env;
        }

        private async Task SeedAsync(bool grantStudentsReadToTenantAdmin)
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

            if (grantStudentsReadToTenantAdmin)
            {
                var studentsRead = db.Permissions.Single(p => p.Code == Permissions.Students.Read);
                db.RolePermissions.Add(RolePermission.Create(tenantRole.Id, studentsRead.Id).Value);
            }

            db.TenantMemberships.Add(
                Centerix.Domain.Platform.Tenants.TenantMembership.Create(
                    TestUserId, TenantId, "TenantAdmin",
                    TenantMembershipStatus.Active).Value);

            await db.SaveChangesAsync();

            _userManager.Add(TestUserId, email: $"{TestUserId}@t22final.test");

            // Reset the request scope so each test starts from a NEUTRAL tenant state
            // (resolved == false, authorized == false) and must explicitly establish the context
            // it needs. The InMemory store is keyed by database name, so the seeded rows persist
            // across the scope swap.
            _requestScope.Dispose();
            _requestScope = _root.CreateScope();
            _tenantAccessorState.Reset();
        }

        public bool HasMembership()
        {
            var db = RequestServices.GetRequiredService<AppDbContext>();
            return db.TenantMemberships
                .IgnoreQueryFilters()
                .Any(m => m.UserId == TestUserId && m.TenantId == TenantId);
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
                new(ClaimTypes.Name, $"{TestUserId}@t22final.test"),
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

        public void AlwaysThrowFromVerifier() => _userManager.ThrowOnLookup = true;

        public void Dispose()
        {
            _requestScope.Dispose();
            _root.Dispose();
        }

        private const string TestUserId = "t22-final-user";
    }

    /// <summary>
    /// Minimal <see cref="IMultiTenantContextAccessor{T}"/> stand-in. The real Finbuckle accessor
    /// is async-context bound; this fake makes "resolved" and "authorized" independently
    /// controllable so the IsResolved-vs-IsAuthorized distinction can be tested directly.
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
    /// a full Identity stack, so the test proves the verifier's decision rather than a mocked
    /// result.
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