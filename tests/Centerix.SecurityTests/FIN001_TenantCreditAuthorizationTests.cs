namespace Centerix.SecurityTests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.API.Controllers;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// FIN-001 — a tenant credit (real balance) could be minted without sufficient authorization.
/// <para>
/// Before: <c>POST /api/tenantcredits</c> required only the tenant-scoped
/// <c>TenantCredits.Create</c> permission. There was no platform guard, no restriction on which
/// <c>CreditSourceType</c> could be claimed, no mandatory idempotency key and no upper bound on
/// the amount.
/// </para>
/// <para>
/// The invariant under test is a TWO-KEY model: the tenant membership/permission decides WHICH
/// TENANT the request may act in, and <c>IPlatformAdminGuard</c> (DB-verified platform authority)
/// decides whether the caller may mint balance at all. On top of that, only discretionary sources
/// may be claimed, they may not carry a fabricated <c>SourceId</c>, an <c>IdempotencyKey</c> is
/// mandatory, and the amount must fit the <c>decimal(10,2)</c> column.
/// </para>
/// </summary>
[Trait("Category", "FIN001")]
public class FIN001_TenantCreditAuthorizationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public FIN001_TenantCreditAuthorizationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ==================================================================
    // A. The two-key authorization matrix
    // ==================================================================

    [Fact]
    public void PermissionMatrix_TenantCreditsCreate_IsTenantAdminOnly()
    {
        // Tenant-context key: granted to TenantAdmin (so a platform operator holding a
        // TenantAdmin membership can reach the endpoint), withheld from TenantUser.
        Assert.Contains(Permissions.TenantCredits.Create, Permissions.GetTenantAdminPermissions());
        Assert.DoesNotContain(Permissions.TenantCredits.Create, Permissions.GetTenantUserPermissions());
    }

    /// <summary>
    /// Defense in depth: the two keys are declared on the ENDPOINT, so ASP.NET's own policy
    /// combination denies a caller who holds only the tenant key. The handler's
    /// <c>IPlatformAdminGuard</c> is a second, independent check — not the only one. Removing it
    /// must not turn <c>POST /api/tenantcredits</c> into a tenant-permission-only operation.
    /// </summary>
    [Fact]
    public void CreateTenantCredit_EndpointDeclaresBothAuthorizationKeys()
    {
        var attributes = typeof(TenantCreditsController)
            .GetMethod(nameof(TenantCreditsController.CreateTenantCredit))!
            .GetCustomAttributes(typeof(HasPermissionAttribute), inherit: false)
            .Cast<HasPermissionAttribute>()
            .Select(a => a.Permission)
            .ToList();

        Assert.Contains(Permissions.TenantCredits.Create, attributes);
        Assert.Contains(Permissions.PlatformCredits.Mint, attributes);

        // The two keys must resolve to OPPOSITE scopes; that opposition is what makes the
        // combination a genuine two-key operation.
        Assert.Equal(PermissionScope.Tenant, PermissionScopes.Resolve(Permissions.TenantCredits.Create));
        Assert.Equal(PermissionScope.Platform, PermissionScopes.Resolve(Permissions.PlatformCredits.Mint));

        // The platform key must never become tenant-authorizable: it is absent from every
        // tenant-role matrix, so no membership/role/RolePermission row can ever satisfy it.
        Assert.DoesNotContain(Permissions.PlatformCredits.Mint, Permissions.GetTenantAdminPermissions());
        Assert.DoesNotContain(Permissions.PlatformCredits.Mint, Permissions.GetTenantUserPermissions());
    }

    [Fact]
    public async Task Anonymous_MintCredit_Returns401()
    {
        var tenantId = await SeedAsync();

        var response = await _client.SendAsync(Post(null, MintPayload(), tenantId));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_MintCredit_Returns403()
    {
        var tenantId = await SeedAsync();
        var admin = await CreateMemberAsync(tenantId, "TenantAdmin", identityRole: "TenantAdmin");

        var response = await _client.SendAsync(Post(Token(admin, "TenantAdmin"), MintPayload(), tenantId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_MintCredit_Returns403()
    {
        var tenantId = await SeedAsync();
        var user = await CreateMemberAsync(tenantId, "TenantUser", identityRole: "TenantUser");

        var response = await _client.SendAsync(Post(Token(user, "TenantUser"), MintPayload(), tenantId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_WithoutMembership_MintCredit_Returns403()
    {
        var tenantId = await SeedAsync();
        var platformAdmin = await CreateUserAsync($"pa_{Guid.NewGuid():N}@fin001.test");
        await AddToRoleAsync(platformAdmin, "PlatformAdmin");

        // No TenantMembership at all: the tenant guard rejects before the handler is reached.
        var response = await _client.SendAsync(Post(Token(platformAdmin, "PlatformAdmin"), MintPayload(), tenantId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The positive control: verified platform authority + active tenant membership.</summary>
    [Fact]
    public async Task PlatformAdmin_WithMembership_MintCredit_Returns201_AndPersistsRow()
    {
        var tenantId = await SeedAsync();
        var platformAdmin = await CreatePlatformOperatorAsync(tenantId);

        var key = Guid.NewGuid().ToString("N");
        var response = await _client.SendAsync(
            Post(Token(platformAdmin, "PlatformAdmin"), MintPayload(idempotencyKey: key), tenantId));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected 201 but got {(int)response.StatusCode}. Body: {body}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // The verification scope has no authorized tenant, so the global tenant filter would hide
        // the row; the assertions below are about the PERSISTED row, not about tenant visibility.
        var credit = await db.TenantCredits.IgnoreQueryFilters()
            .SingleAsync(c => c.IdempotencyKey == key);
        Assert.Equal(tenantId, credit.TenantId);
        Assert.Equal(100m, credit.Amount);
        Assert.Equal(CreditSourceType.Manual, credit.SourceType);
        Assert.Null(credit.SourceId);
    }

    // ==================================================================
    // B. Source integrity
    // ==================================================================

    [Theory]
    [InlineData(CreditSourceType.Overpayment)]
    [InlineData(CreditSourceType.SubscriptionChange)]
    public async Task SystemGeneratedSource_IsRejected(CreditSourceType sourceType)
    {
        var tenantId = await SeedAsync();
        var platformAdmin = await CreatePlatformOperatorAsync(tenantId);

        var payload = MintPayload(
            sourceType: sourceType,
            sourceId: Guid.NewGuid(),
            idempotencyKey: Guid.NewGuid().ToString("N"));

        var response = await _client.SendAsync(
            Post(Token(platformAdmin, "PlatformAdmin"), payload, tenantId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DiscretionarySource_WithFabricatedSourceId_IsRejected()
    {
        var tenantId = await SeedAsync();
        var platformAdmin = await CreatePlatformOperatorAsync(tenantId);

        var payload = MintPayload(
            sourceType: CreditSourceType.Manual,
            sourceId: Guid.NewGuid(),
            idempotencyKey: Guid.NewGuid().ToString("N"));

        var response = await _client.SendAsync(
            Post(Token(platformAdmin, "PlatformAdmin"), payload, tenantId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ==================================================================
    // C. Idempotency + bounds
    // ==================================================================

    [Fact]
    public async Task MissingIdempotencyKey_IsRejected()
    {
        var tenantId = await SeedAsync();
        var platformAdmin = await CreatePlatformOperatorAsync(tenantId);

        var payload = MintPayload(idempotencyKey: null);

        var response = await _client.SendAsync(
            Post(Token(platformAdmin, "PlatformAdmin"), payload, tenantId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Amount_AboveDecimal10Maximum_IsRejected()
    {
        var tenantId = await SeedAsync();
        var platformAdmin = await CreatePlatformOperatorAsync(tenantId);

        var payload = MintPayload(
            amount: TenantCredit.MaxCreatableAmount + 0.01m,
            idempotencyKey: Guid.NewGuid().ToString("N"));

        var response = await _client.SendAsync(
            Post(Token(platformAdmin, "PlatformAdmin"), payload, tenantId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Replaying the SAME logical request (same idempotency key, same parameters) must not mint
    /// a second credit; only one row may exist afterwards.
    /// </summary>
    [Fact]
    public async Task ReplayWithSameIdempotencyKey_DoesNotCreateASecondCredit()
    {
        var tenantId = await SeedAsync();
        var platformAdmin = await CreatePlatformOperatorAsync(tenantId);
        var key = Guid.NewGuid().ToString("N");

        var token = Token(platformAdmin, "PlatformAdmin");
        var payload = MintPayload(idempotencyKey: key);

        var first = await _client.SendAsync(Post(token, payload, tenantId));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _client.SendAsync(Post(token, payload, tenantId));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.TenantCredits.IgnoreQueryFilters().CountAsync(c => c.IdempotencyKey == key));
    }

    /// <summary>The same key with DIFFERENT parameters is a conflict, not an idempotent retry.</summary>
    [Fact]
    public async Task SameIdempotencyKey_DifferentParameters_Returns409()
    {
        var tenantId = await SeedAsync();
        var platformAdmin = await CreatePlatformOperatorAsync(tenantId);
        var key = Guid.NewGuid().ToString("N");
        var token = Token(platformAdmin, "PlatformAdmin");

        var first = await _client.SendAsync(Post(token, MintPayload(idempotencyKey: key), tenantId));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var conflicting = await _client.SendAsync(Post(
            token, MintPayload(amount: 555m, idempotencyKey: key), tenantId));
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static object MintPayload(
        decimal amount = 100m,
        CreditSourceType sourceType = CreditSourceType.Manual,
        Guid? sourceId = null,
        string? idempotencyKey = "test-key")
        => new
        {
            amount,
            sourceType = (byte)sourceType,
            sourceId,
            currencyCode = "EGP",
            idempotencyKey
        };

    /// <summary>Eagerly-built assertion messages must not dereference a null error list.</summary>
    private static string DescribeErrors(List<Centerix.Domain.Common.Results.Error>? errors)
        => errors is null || errors.Count == 0
            ? "domain validation failed without a reported error"
            : string.Join("; ", errors.Select(e => e.Code));

    private async Task<string> SeedAsync()
    {
        var tenantId = $"fin001-{Guid.NewGuid():N}";
        await _factory.SeedPermissionsAsync();

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();
        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = tenantId,
                Name = tenantId,
                Email = $"{tenantId}@fin001.test",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var (name, display) in new[]
                 {
                     ("PlatformAdmin", "Platform Administrator"),
                     ("TenantAdmin", "Tenant Administrator"),
                     ("TenantUser", "Tenant User")
                 })
        {
            if (!await roleManager.RoleExistsAsync(name))
            {
                await roleManager.CreateAsync(new ApplicationRole(name)
                {
                    Code = name,
                    DisplayName = display,
                    IsSystem = true,
                    NormalizedName = name.ToUpperInvariant()
                });
            }
        }

        return tenantId;
    }

    private async Task<IdentityUser> CreateMemberAsync(
        string tenantId, string membershipRole, string identityRole)
    {
        var user = await CreateUserAsync($"{membershipRole}_{Guid.NewGuid():N}@fin001.test");
        await AddToRoleAsync(user, identityRole);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = TenantMembership.Create(
            user.Id, tenantId, membershipRole, TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess, DescribeErrors(membership.Errors));
        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<IdentityUser> CreatePlatformOperatorAsync(string tenantId)
        // SEC-001: a platform operator's TENANT membership carries a tenant role; the platform
        // authority comes from the Identity role + the DB-backed verifier.
        => await CreateMemberAsync(tenantId, "TenantAdmin", identityRole: "PlatformAdmin");

    private async Task<IdentityUser> CreateUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, "Str0ng!Pass1");
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task AddToRoleAsync(IdentityUser user, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var stored = await userManager.FindByIdAsync(user.Id);
        if (!await userManager.IsInRoleAsync(stored!, role))
        {
            await userManager.AddToRoleAsync(stored!, role);
        }
    }

    private string Token(IdentityUser user, params string[] roles) =>
        _factory.GenerateTestToken(user.Id, user.Email!, roles);

    private static HttpRequestMessage Post(string? token, object payload, string tenantId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tenantcredits");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("tenant", tenantId);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }
}
