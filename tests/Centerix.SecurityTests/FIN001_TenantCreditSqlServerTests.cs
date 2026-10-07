using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

namespace Centerix.SecurityTests;

/// <summary>
/// F4 (relational half) — the FIN-001 mint path under TRUE concurrency on real SQL Server.
/// <para>
/// The idempotency contract of <c>POST /api/tenantcredits</c> rests on a unique index over
/// (TenantId, IdempotencyKey): two concurrent mints with the same key must BOTH answer 201 —
/// the loser's insert hits 2627, the handler re-reads the winner's row, compares the payload
/// and reports idempotent success — and exactly ONE row may exist afterwards. The InMemory
/// provider cannot enforce the unique index, so this race is only provable here.
/// </para>
/// <para>
/// The two-key authorization itself (flagged tenant key + platform key) is proven in
/// <see cref="FIN001_TenantCreditAuthorizationTests"/> and
/// <see cref="FIN001_TwoKeyCatalogEnforcementTests"/>; this file needs the full SQL stack only
/// for the storage race.
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
public class FIN001_TenantCreditSqlServerTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly SqlServerIntegrationFactory _env;

    public FIN001_TenantCreditSqlServerTests(SqlServerIntegrationFactory env) => _env = env;

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task ParallelMints_WithTheSameIdempotencyKey_BothSucceed_WithExactlyOneRow()
    {
        const string tenantId = "fin001-sql-race";
        await EnsureTenantAsync(tenantId);
        var token = await SeedPlatformOperatorAsync(tenantId);

        var key = Guid.NewGuid().ToString("N");
        var payload = MintPayload(idempotencyKey: key);

        var responses = await Task.WhenAll(
            _env.Client.SendAsync(MintRequest(token, payload, tenantId)),
            _env.Client.SendAsync(MintRequest(token, payload, tenantId)));

        for (var i = 0; i < responses.Length; i++)
        {
            var body = await responses[i].Content.ReadAsStringAsync();
            Assert.True(
                responses[i].StatusCode == HttpStatusCode.Created,
                $"Both parallel mints are the SAME logical request and must be 201 (idempotent). " +
                $"Request {i} got {(int)responses[i].StatusCode}. Body: {body}");
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.TenantCredits.IgnoreQueryFilters()
            .Where(c => c.IdempotencyKey == key)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(tenantId, row.TenantId);
    }

    /// <summary>
    /// Sequential replay against the same storage contract (the race above can interleave either
    /// way; this one deterministically exercises the pre-insert duplicate check on SQL).
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task SequentialReplay_WithTheSameKey_Returns201Twice_SingleRow()
    {
        const string tenantId = "fin001-sql-replay";
        await EnsureTenantAsync(tenantId);
        var token = await SeedPlatformOperatorAsync(tenantId);

        var key = Guid.NewGuid().ToString("N");
        var payload = MintPayload(idempotencyKey: key);

        var first = await _env.Client.SendAsync(MintRequest(token, payload, tenantId));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _env.Client.SendAsync(MintRequest(token, payload, tenantId));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            1,
            await db.TenantCredits.IgnoreQueryFilters().CountAsync(c => c.IdempotencyKey == key));
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static object MintPayload(string idempotencyKey) => new
    {
        amount = 100m,
        sourceType = (byte)CreditSourceType.Manual,
        sourceId = (Guid?)null,
        currencyCode = "EGP",
        idempotencyKey
    };

    private HttpRequestMessage MintRequest(string token, object payload, string tenantId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tenantcredits");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("tenant", tenantId);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    private Task EnsureTenantAsync(string id)
        => EnsureTenantAsync(
            _env.Factory.Services.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>(),
            id);

    private static async Task EnsureTenantAsync(IMultiTenantStore<CenterixTenantInfo> store, string id)
    {
        if (await store.TryGetAsync(id) is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = id,
                Identifier = id,
                Name = id,
                Email = $"{id}@registry.test",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// A real PlatformAdmin (Identity role → DB-verified by
    /// <see cref="Centerix.Infrastructure.Auth.PlatformAdminVerifier"/> against AspNetUserRoles)
    /// holding an ACTIVE TenantAdmin membership in the tenant — the only principal that may mint.
    /// The role row is written directly through the DbContext: Identity's AddToRoleAsync attaches
    /// a second IdentityUser instance into the scoped context, which collides with the instance
    /// already tracked there.
    /// </summary>
    private async Task<string> SeedPlatformOperatorAsync(string tenantId)
    {
        await _env.Factory.SeedPermissionsAsync();

        var email = $"pa_{Guid.NewGuid():N}@fin001sql.test";
        var user = await CreateUserAsync(email);

        using var scope = _env.Factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (name, display) in new[]
                 {
                     ("PlatformAdmin", "Platform Administrator"),
                     ("TenantAdmin", "Tenant Administrator")
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

        // Prefer the normalized row; fall back to the plain name in case a reflection-based
        // seeder inserted the role without a NormalizedName.
        var platformRoleId = await db.Roles
            .Where(r => r.NormalizedName == "PLATFORMADMIN" || r.Name == "PlatformAdmin")
            .OrderByDescending(r => r.NormalizedName == "PLATFORMADMIN")
            .Select(r => r.Id)
            .FirstAsync();

        if (!await db.UserRoles.AnyAsync(
                ur => ur.UserId == user.Id && ur.RoleId == platformRoleId))
        {
            db.UserRoles.Add(new IdentityUserRole<string>
            {
                UserId = user.Id,
                RoleId = platformRoleId
            });
        }

        db.TenantMemberships.Add(
            TenantMembership.Create(user.Id, tenantId, "TenantAdmin", TenantMembershipStatus.Active).Value);
        await db.SaveChangesAsync();

        return _env.Factory.GenerateTestToken(user.Id, user.Email!, ["PlatformAdmin"]);
    }

    private async Task<IdentityUser> CreateUserAsync(string email)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            return existing;
        }

        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }
}
