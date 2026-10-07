using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// F3 — the tenant membership/invitation role contract: only the canonical tenant roles
/// (TenantAdmin, TenantUser) may be bound to a tenant-scoped row.
/// <para>
/// Vulnerability: membership and invitation RoleName accepted ANY Identity role except
/// PlatformAdmin. A custom role backed by real RolePermission rows (e.g. "Ops Manager" granted
/// Students.Read) flowed through the tenant permission resolver exactly like the
/// production-seeded matrices — so the effective grant surface of the tenant path was
/// "whatever roles an operator happens to create", not the audited two-matrix contract.
/// </para>
/// <para>
/// Invariant under test: non-canonical names are REJECTED (Forbidden, never defaulted or
/// silently accepted) at every layer — domain factories, the HTTP invitation boundary, and the
/// authorization pipeline for legacy rows.
/// </para>
/// </summary>
public class F3_TenantRoleContractTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public F3_TenantRoleContractTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ==================================================================
    // A. Domain write contract
    // ==================================================================

    [Theory]
    [InlineData("Ops Manager")]
    [InlineData("LimitedUser")]
    [InlineData("tenantadmin")]        // case variant — Resolve() classifies it, the write contract rejects it
    [InlineData("TENANTUSER")]
    [InlineData(" TenantAdmin ")]      // whitespace-padded
    [InlineData("TenantUser ")]
    [InlineData("Tenant Admin")]
    public void Membership_NonCanonicalRole_IsRejectedAsForbidden(string roleName)
    {
        var result = TenantMembership.Create("user-1", "tenant-1", roleName, TenantMembershipStatus.Active);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantMembership.RoleNotAllowed");
        Assert.Contains(result.Errors!, e => e.Type == ErrorKind.Forbidden);
    }

    [Fact]
    public void Membership_EmptyRole_DefaultsToCanonicalTenantUser()
    {
        var result = TenantMembership.Create("user-1", "tenant-1", "", TenantMembershipStatus.Active);

        Assert.True(result.IsSuccess, DescribeErrors(result.Errors));
        Assert.Equal("TenantUser", result.Value.RoleName);
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("TenantUser")]
    public void Membership_CanonicalRole_IsAccepted(string roleName)
    {
        var result = TenantMembership.Create("user-1", "tenant-1", roleName, TenantMembershipStatus.Active);

        Assert.True(result.IsSuccess, DescribeErrors(result.Errors));
        Assert.Equal(roleName, result.Value.RoleName);
    }

    [Fact]
    public void Invitation_NonCanonicalRole_IsRejectedAsForbidden()
    {
        var result = TenantInvitation.Create(
            Guid.NewGuid(),
            "tenant-1",
            "invitee@f3.test",
            "inviter-1",
            "Ops Manager",
            Convert.ToHexString(new byte[32]),
            DateTimeOffset.UtcNow.AddDays(7));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantMembership.RoleNotAllowed");
        Assert.Contains(result.Errors!, e => e.Type == ErrorKind.Forbidden);
    }

    // ==================================================================
    // B. Application boundary — HTTP 403 regardless of the Identity catalog
    // ==================================================================

    /// <summary>
    /// A custom role that DOES exist in Identity must still be refused with 403: the contract is
    /// about which roles may become a membership, not about catalog existence. (Pre-fix, this
    /// request was accepted with 201.)
    /// </summary>
    [Fact]
    public async Task CreateInvitation_WithExistingCustomRole_Returns403()
    {
        var tenantId = await SeedAsync();
        var admin = await CreateTenantAdminAsync(tenantId);
        await EnsureRoleAsync("Ops Manager"); // exists in Identity — must not matter

        var response = await SendAsync(
            HttpMethod.Post,
            "/api/invitations",
            tenantId,
            Token(admin, "TenantAdmin"),
            new { email = $"invitee_{Guid.NewGuid():N}@f3.test", roleName = "Ops Manager", expirationDays = 7 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(
            await db.TenantInvitations.AnyAsync(i => i.RoleName == "Ops Manager"),
            "No invitation may be persisted with a non-canonical role.");
    }

    /// <summary>
    /// A custom role that does NOT exist in Identity must also be 403 (RoleNotAllowed), not the
    /// pre-existing 400 RoleNotFound: the refusal is the ROLE CONTRACT, evaluated before the
    /// catalog lookup. (Pre-fix this returned 400, confirming the flow reached the lookup.)
    /// </summary>
    [Fact]
    public async Task CreateInvitation_WithUnknownCustomRole_Returns403NotRoleNotFound()
    {
        var tenantId = await SeedAsync();
        var admin = await CreateTenantAdminAsync(tenantId);

        var response = await SendAsync(
            HttpMethod.Post,
            "/api/invitations",
            tenantId,
            Token(admin, "TenantAdmin"),
            new { email = $"invitee_{Guid.NewGuid():N}@f3.test", roleName = "Does Not Exist Role", expirationDays = 7 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Positive control: the canonical invitation still succeeds.</summary>
    [Fact]
    public async Task CreateInvitation_WithCanonicalRole_StillReturns201()
    {
        var tenantId = await SeedAsync();
        var admin = await CreateTenantAdminAsync(tenantId);

        var response = await SendAsync(
            HttpMethod.Post,
            "/api/invitations",
            tenantId,
            Token(admin, "TenantAdmin"),
            new { email = $"invitee_{Guid.NewGuid():N}@f3.test", roleName = "TenantUser", expirationDays = 7 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ==================================================================
    // C. Authorization defense in depth — legacy rows with custom roles
    // ==================================================================

    /// <summary>
    /// THE discriminator for F3. A pre-invariant membership carrying a custom role that has REAL
    /// RolePermission grants (Students.Read) must publish NOTHING. Pre-fix, this exact request
    /// returned 200 — the tenant resolver honored any Identity role. Flipping the SAME membership
    /// to the canonical TenantAdmin restores 200, proving the denial is caused by the role name
    /// contract and nothing else (not by missing permissions, tenant resolution, or tokens).
    /// </summary>
    [Fact]
    public async Task MembershipCarryingCustomRole_GrantsNoTenantPermissions()
    {
        var tenantId = await SeedAsync();

        var user = await CreateUserAsync($"legacy_{Guid.NewGuid():N}@f3.test");
        await EnsureRoleAsync("Ops Manager");
        await GrantPermissionToRoleAsync("Ops Manager", Permissions.Students.Read);

        // The domain factory now rejects this row — seed it exactly like a pre-invariant
        // database would hold it (legal name first, then overwritten).
        await SeedLegacyMembershipAsync(user.Id, tenantId, "Ops Manager");

        var denied = await SendAsync(
            HttpMethod.Get, "/api/students", tenantId, Token(user, "TenantUser"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Positive control: same principal, same endpoint, canonical role → allowed.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = db.TenantMemberships.Single(m => m.UserId == user.Id && m.TenantId == tenantId);
            typeof(TenantMembership).GetProperty(nameof(TenantMembership.RoleName))!
                .SetValue(membership, "TenantAdmin");
            await db.SaveChangesAsync();
        }

        var allowed = await SendAsync(
            HttpMethod.Get, "/api/students", tenantId, Token(user, "TenantUser"));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // ==================================================================
    // Helpers (mirrors SEC001's seed surface)
    // ==================================================================

    private static string DescribeErrors(List<Error>? errors)
        => errors is null || errors.Count == 0
            ? "domain validation failed without a reported error"
            : string.Join("; ", errors.Select(e => e.Code));

    private async Task<string> SeedAsync()
    {
        var tenantId = $"f3-{Guid.NewGuid():N}";
        await _factory.SeedPermissionsAsync();

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Finbuckle.MultiTenant.Abstractions.IMultiTenantStore<Centerix.Infrastructure.Tenancy.CenterixTenantInfo>>();

        if (await store.TryGetAsync(tenantId) is null)
        {
            await store.TryAddAsync(new Centerix.Infrastructure.Tenancy.CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = tenantId,
                Name = tenantId,
                Email = $"{tenantId}@f3.test",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }

        await EnsureRoleAsync("PlatformAdmin");
        await EnsureRoleAsync("TenantAdmin");
        await EnsureRoleAsync("TenantUser");

        return tenantId;
    }

    private async Task EnsureRoleAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roleManager.RoleExistsAsync(name))
        {
            await roleManager.CreateAsync(new ApplicationRole(name)
            {
                Code = name,
                DisplayName = name,
                IsSystem = true,
                NormalizedName = name.ToUpperInvariant()
            });
        }
    }

    private async Task GrantPermissionToRoleAsync(string roleName, string permissionCode)
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var role = await roleManager.FindByNameAsync(roleName);
        Assert.NotNull(role);

        var permission = await db.Permissions.SingleAsync(p => p.Code == permissionCode);
        var alreadyGranted = await db.RolePermissions.AnyAsync(
            rp => rp.RoleId == role!.Id && rp.PermissionId == permission.Id);
        if (!alreadyGranted)
        {
            db.RolePermissions.Add(RolePermission.Create(role!.Id, permission.Id).Value);
            await db.SaveChangesAsync();
        }
    }

    private async Task<IdentityUser> CreateTenantAdminAsync(string tenantId)
    {
        var user = await CreateUserAsync($"admin_{Guid.NewGuid():N}@f3.test");
        await AddToRoleAsync(user, "TenantAdmin");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = TenantMembership.Create(user.Id, tenantId, "TenantAdmin", TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess, DescribeErrors(membership.Errors));
        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task SeedLegacyMembershipAsync(string userId, string tenantId, string roleName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var membership = TenantMembership.Create(userId, tenantId, "TenantUser", TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess, DescribeErrors(membership.Errors));
        typeof(TenantMembership).GetProperty(nameof(TenantMembership.RoleName))!
            .SetValue(membership.Value, roleName);

        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();
    }

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
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, StrongPassword);
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? tenantHeader, string? token, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(tenantHeader))
            request.Headers.Add("tenant", tenantHeader);
        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
        else if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request);
    }
}
