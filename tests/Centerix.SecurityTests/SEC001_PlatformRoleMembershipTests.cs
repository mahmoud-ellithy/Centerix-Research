namespace Centerix.SecurityTests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// SEC-001 — a platform-authority role must never be bound to a tenant-scoped row.
/// <para>
/// Vulnerability: <c>CreateInvitationHandler</c> only checked that the target role exists in
/// Identity. <c>PlatformAdmin</c> IS an Identity role, and its <c>RolePermission</c> rows are
/// <c>Permissions.GetPlatformAdminPermissions()</c> — the entire catalog. Because the tenant
/// permission resolver derives grants from <c>TenantMembership.RoleName</c>, a membership (or an
/// invitation that mints one) carrying <c>PlatformAdmin</c> published every permission —
/// including tenant-scoped codes — as a tenant-derived grant to a member of ONE tenant.
/// </para>
/// <para>
/// Invariant: platform authority is expressed ONLY through the Identity role +
/// <see cref="Centerix.Application.Common.Interfaces.IPlatformAdminVerifier"/> and is consumed
/// exclusively by the platform branch of <c>PermissionAuthorizationHandler</c>. It can never be
/// carried by a tenant membership or an invitation.
/// </para>
/// </summary>
[Trait("Category", "SEC001")]
public class SEC001_PlatformRoleMembershipTests : IClassFixture<TestWebApplicationFactory>
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SEC001_PlatformRoleMembershipTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ==================================================================
    // A. Domain invariants
    // ==================================================================

    [Theory]
    [InlineData("PlatformAdmin")]
    [InlineData("platformadmin")]
    [InlineData(" PlatformAdmin ")]
    [InlineData("PLATFORMADMIN")]
    public void Membership_PlatformRole_IsRejected_AsForbidden(string roleName)
    {
        var result = TenantMembership.Create("user-1", "tenant-1", roleName, TenantMembershipStatus.Active);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantMembership.PlatformRoleNotAllowed");
        Assert.Contains(result.Errors!, e => e.Type == ErrorKind.Forbidden);
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("TenantUser")]
    [InlineData("Ops Manager")] // custom tenant roles stay permitted (validated by the Identity catalog)
    [InlineData("")]
    public void Membership_TenantOrCustomRole_StillSucceeds(string roleName)
    {
        var result = TenantMembership.Create("user-1", "tenant-1", roleName, TenantMembershipStatus.Active);

        Assert.True(result.IsSuccess, DescribeErrors(result.Errors));
    }

    [Fact]
    public void Invitation_PlatformRole_IsRejected_AsForbidden()
    {
        var result = TenantInvitation.Create(
            Guid.NewGuid(),
            "tenant-1",
            "invitee@sec001.test",
            "inviter-1",
            "PlatformAdmin",
            Convert.ToHexString(new byte[32]),
            DateTimeOffset.UtcNow.AddDays(7));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "TenantMembership.PlatformRoleNotAllowed");
        Assert.Contains(result.Errors!, e => e.Type == ErrorKind.Forbidden);
    }

    // ==================================================================
    // B. Application boundary — HTTP 403
    // ==================================================================

    /// <summary>
    /// The invitation must be refused with 403 (not 409/500) BEFORE any work is performed.
    /// </summary>
    [Fact]
    public async Task CreateInvitation_WithPlatformAdminRole_Returns403()
    {
        var tenantId = await SeedAsync();
        var admin = await CreateTenantAdminAsync(tenantId);

        var response = await SendAsync(
            HttpMethod.Post,
            "/api/invitations",
            tenantId,
            Token(admin, "TenantAdmin"),
            new { email = $"invitee_{Guid.NewGuid():N}@sec001.test", roleName = "PlatformAdmin", expirationDays = 7 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Positive control: the very same request with a tenant role is accepted.</summary>
    [Fact]
    public async Task CreateInvitation_WithTenantAdminRole_Returns201()
    {
        var tenantId = await SeedAsync();
        var admin = await CreateTenantAdminAsync(tenantId);

        var response = await SendAsync(
            HttpMethod.Post,
            "/api/invitations",
            tenantId,
            Token(admin, "TenantAdmin"),
            new { email = $"invitee_{Guid.NewGuid():N}@sec001.test", roleName = "TenantUser", expirationDays = 7 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// A pre-existing invitation row (written before the invariant existed, so seeded here by
    /// reflection) must not be consumable: accepting it must fail and must create NO membership.
    /// </summary>
    [Fact]
    public async Task Accept_InvitationCarryingPlatformRole_Fails_AndCreatesNoMembership()
    {
        var tenantId = await SeedAsync();
        var inviter = await CreateTenantAdminAsync(tenantId);
        var invitee = await CreateUserAsync($"invitee_{Guid.NewGuid():N}@sec001.test");

        var invitation = await SeedInvitationAsync(
            tenantId, inviter.Id, invitee.Email!, "PlatformAdmin");

        var response = await SendAsync(
            HttpMethod.Post,
            $"/api/invitations/{invitation.RawToken}/accept",
            tenantId,
            Token(invitee, "TenantUser"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(
            await db.TenantMemberships.AnyAsync(m =>
                m.UserId == invitee.Id
                && m.TenantId == tenantId
                && m.RoleName == "PlatformAdmin"),
            "A membership carrying a platform role must never be created.");
    }

    /// <summary>Registration must fail closed for the same reason (no account, invitation reusable).</summary>
    [Fact]
    public async Task Register_InvitationCarryingPlatformRole_Fails()
    {
        var tenantId = await SeedAsync();
        var inviter = await CreateTenantAdminAsync(tenantId);
        var email = $"newbie_{Guid.NewGuid():N}@sec001.test";

        var invitation = await SeedInvitationAsync(tenantId, inviter.Id, email, "PlatformAdmin");

        var response = await _client.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, "/api/invitations/register")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { token = invitation.RawToken, password = StrongPassword }),
                Encoding.UTF8, "application/json")
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            InvitationStatus.Pending,
            db.TenantInvitations.AsNoTracking().Single(i => i.TokenHash == invitation.TokenHash).Status);
        Assert.False(
            await db.TenantMemberships.AnyAsync(m => m.TenantId == tenantId && m.RoleName == "PlatformAdmin"),
            "A membership carrying a platform role must never be created.");
    }

    // ==================================================================
    // C. Authorization defense in depth (rows written before the invariant)
    // ==================================================================

    /// <summary>
    /// A membership row that ALREADY carries <c>PlatformAdmin</c> (only reachable today by a
    /// pre-invariant database, hence written here by reflection) must publish NO tenant
    /// permission. Before the fix it published the entire platform permission set through the
    /// tenant path, granting a member of ONE tenant every tenant-scoped permission there is.
    /// </summary>
    [Fact]
    public async Task MembershipCarryingPlatformRole_GrantsNoTenantPermissions()
    {
        var tenantId = await SeedAsync();

        var user = await CreateUserAsync($"legacy_{Guid.NewGuid():N}@sec001.test");
        await AddToRoleAsync(user, "PlatformAdmin");
        await SeedLegacyMembershipAsync(user.Id, tenantId, "PlatformAdmin");

        var denied = await SendAsync(
            HttpMethod.Get, "/api/students", tenantId, Token(user, "PlatformAdmin"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Positive control: the SAME principal is allowed once the membership carries a tenant
        // role (the legitimate path proven by T22).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = db.TenantMemberships.Single(m => m.UserId == user.Id && m.TenantId == tenantId);
            typeof(TenantMembership).GetProperty(nameof(TenantMembership.RoleName))!
                .SetValue(membership, "TenantAdmin");
            await db.SaveChangesAsync();
        }

        var allowed = await SendAsync(
            HttpMethod.Get, "/api/students", tenantId, Token(user, "PlatformAdmin"));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    /// <summary>Eagerly-built assertion messages must not dereference a null error list.</summary>
    private static string DescribeErrors(List<Error>? errors)
        => errors is null || errors.Count == 0
            ? "domain validation failed without a reported error"
            : string.Join("; ", errors.Select(e => e.Code));

    /// <summary>Creates tenant + roles + permission matrix; returns the tenant id.</summary>
    private async Task<string> SeedAsync()
    {
        var tenantId = $"sec001-{Guid.NewGuid():N}";
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
                Email = $"{tenantId}@sec001.test",
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

    private async Task<IdentityUser> CreateTenantAdminAsync(string tenantId)
    {
        var user = await CreateUserAsync($"admin_{Guid.NewGuid():N}@sec001.test");
        await AddToRoleAsync(user, "TenantAdmin");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = TenantMembership.Create(user.Id, tenantId, "TenantAdmin", TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess, DescribeErrors(membership.Errors));
        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Seeds a membership whose RoleName could not be produced by the domain factory any more -
    /// it is created with a tenant role and then overwritten, exactly like a pre-SEC-001 row.
    /// </summary>
    private async Task SeedLegacyMembershipAsync(string userId, string tenantId, string roleName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var membership = TenantMembership.Create(userId, tenantId, "TenantUser", TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess);
        typeof(TenantMembership).GetProperty(nameof(TenantMembership.RoleName))!
            .SetValue(membership.Value, roleName);

        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();
    }

    private sealed record SeededInvitation(string RawToken, string TokenHash);

    private async Task<SeededInvitation> SeedInvitationAsync(
        string tenantId, string inviterUserId, string email, string roleName)
    {
        var rawToken = TestInviteTokens.NewToken();
        var tokenHash = TestInviteTokens.Sha256Hex(rawToken);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Created with a legal tenant role, then overwritten: the row predates the invariant.
        var created = TenantInvitation.Create(
            Guid.NewGuid(), tenantId, email, inviterUserId, "TenantUser",
            tokenHash, DateTimeOffset.UtcNow.AddDays(7));
        Assert.True(created.IsSuccess, DescribeErrors(created.Errors));
        typeof(TenantInvitation).GetProperty(nameof(TenantInvitation.RoleName))!
            .SetValue(created.Value, roleName);

        db.TenantInvitations.Add(created.Value);
        await db.SaveChangesAsync();

        return new SeededInvitation(rawToken, tokenHash);
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
