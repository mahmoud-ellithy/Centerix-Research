using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
/// F6 — the F3 role contract proven through the REAL invitation consumption flows on a real
/// SQL Server (real migrations, real transactions, unique indexes).
/// <para>
/// A legacy invitation row (written before the canonical-role contract existed, hence seeded
/// here by reflection) carrying a custom role must be un-consumable on BOTH paths:
/// <list type="bullet">
///   <item><c>POST /api/invitations/register</c> — the ONE-ATOMIC-TRANSACTION registration must
///   roll back completely: no orphan account, no membership, invitation still Pending;</item>
///   <item><c>POST /api/invitations/{token}/accept</c> — the authenticated accept must fail with
///   403 and create no membership.</item>
/// </list>
/// A canonical invitation remains fully consumable (positive control).
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
public class SEC001_SqlServerInvitationRoleBoundaryTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly SqlServerIntegrationFactory _env;

    public SEC001_SqlServerInvitationRoleBoundaryTests(SqlServerIntegrationFactory env)
    {
        _env = env;
        _env.EmailSender.Clear();
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_ThroughLegacyCustomRoleInvitation_Fails_AndLeavesNothingBehind()
    {
        const string tenantId = "f6-legacy-register";
        await EnsureTenantAsync(tenantId);
        await SeedAdminAsync(tenantId); // seeds the inviter membership used as InvitedByUserId

        var email = UniqueEmail("legacyreg");
        var seeded = await SeedLegacyInvitationAsync(tenantId, email, "Ops Manager");

        var response = await _env.Client.SendAsync(RegisterRequest(seeded.RawToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The registration transaction must have rolled back ENTIRELY.
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.False(
            await db.Users.AnyAsync(u => u.NormalizedEmail == email.ToUpperInvariant()),
            "No orphan account may exist after the role-contract rejection (atomic rollback).");
        Assert.False(
            await db.TenantMemberships.AnyAsync(m => m.TenantId == tenantId && m.RoleName == "Ops Manager"),
            "No membership may carry a non-canonical role.");

        var invitation = await db.TenantInvitations.SingleAsync(i => i.TokenHash == seeded.TokenHash);
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Accept_ThroughLegacyCustomRoleInvitation_Fails_AndCreatesNoMembership()
    {
        const string tenantId = "f6-legacy-accept";
        await EnsureTenantAsync(tenantId);
        await SeedAdminAsync(tenantId); // seeds the inviter membership used as InvitedByUserId

        var email = UniqueEmail("legacyacc");
        var invitee = await CreateUserAsync(email);
        var seeded = await SeedLegacyInvitationAsync(tenantId, email, "Ops Manager");

        var response = await _env.Client.SendAsync(AcceptRequest(seeded.RawToken, invitee, tenantId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.False(
            await db.TenantMemberships.AnyAsync(m =>
                m.UserId == invitee.Id && m.TenantId == tenantId),
            "Accepting a non-canonical invitation must create no membership.");
        Assert.Equal(
            InvitationStatus.Pending,
            (await db.TenantInvitations.SingleAsync(i => i.TokenHash == seeded.TokenHash)).Status);
    }

    /// <summary>Positive control: the same two flows with a canonical invitation still succeed.</summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task CanonicalInvitation_StillConsumesOnBothFlows()
    {
        const string tenantId = "f6-canonical";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);

        // Register flow (brand-new account).
        var registerEmail = UniqueEmail("canonreg");
        var rawRegisterToken = await CreateInvitationViaApiAsync(adminToken, tenantId, registerEmail);
        var registerResponse = await _env.Client.SendAsync(RegisterRequest(rawRegisterToken));
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        // Accept flow (existing account).
        var acceptEmail = UniqueEmail("canonacc");
        var invitee = await CreateUserAsync(acceptEmail);
        var rawAcceptToken = await CreateInvitationViaApiAsync(adminToken, tenantId, acceptEmail);
        var acceptResponse = await _env.Client.SendAsync(AcceptRequest(rawAcceptToken, invitee, tenantId));
        Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(
            await db.TenantMemberships.AnyAsync(m => m.RoleName == "TenantUser" && m.TenantId == tenantId),
            "The canonical role must still produce memberships.");
    }

    // ==================================================================
    // Helpers (mirrors SqlServerInvitationFlowTests' seed surface)
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@f6.test";

    private HttpRequestMessage RegisterRequest(string rawToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations/register");
        // Unique per-call source: RegisterPolicy (60/min) is shared across the SQL collection's
        // single host — never let this test touch another test's bucket.
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, RemoteIp());
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { token = rawToken, password = StrongPassword }),
            Encoding.UTF8, "application/json");
        return request;
    }

    private HttpRequestMessage AcceptRequest(string rawToken, IdentityUser user, string tenantId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/invitations/{rawToken}/accept");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _env.Factory.GenerateTestToken(user.Id, user.Email!, []));
        request.Headers.Add("tenant", tenantId);
        return request;
    }

    private static string RemoteIp()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return $"10.{(bytes[0] % 200) + 1}.{(bytes[1] % 200) + 1}.{(bytes[2] % 200) + 1}";
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

    private sealed record SeededInvitation(string RawToken, string TokenHash);

    private async Task<SeededInvitation> SeedLegacyInvitationAsync(
        string tenantId, string email, string roleName)
    {
        // Resolve the inviter's user id from the admin membership (same strategy as the
        // invitation-flow suite: InvitedByUserId must satisfy the AspNetUsers FK).
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inviterMembership = db.TenantMemberships
            .Where(m => m.TenantId == tenantId && m.RoleName == "TenantAdmin")
            .OrderBy(m => m.JoinedAtUtc)
            .First();

        var rawToken = TestInviteTokens.NewToken();
        var tokenHash = TestInviteTokens.Sha256Hex(rawToken);

        // Created with a canonical role (the domain contract), then overwritten to simulate a
        // pre-invariant row — the CHECK constraint does NOT exist on invitation rows (by
        // design: only memberships are constrained), so this insert is legal.
        var created = TenantInvitation.Create(
            Guid.NewGuid(), tenantId, email, inviterMembership.UserId, "TenantUser",
            tokenHash, DateTimeOffset.UtcNow.AddDays(7));
        Assert.True(created.IsSuccess);
        typeof(TenantInvitation).GetProperty(nameof(TenantInvitation.RoleName))!
            .SetValue(created.Value, roleName);

        db.TenantInvitations.Add(created.Value);
        await db.SaveChangesAsync();

        return new SeededInvitation(rawToken, tokenHash);
    }

    private async Task<string> CreateInvitationViaApiAsync(string adminToken, string tenantId, string email)
    {
        _env.EmailSender.Clear();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Headers.Add("tenant", tenantId);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { email, roleName = "TenantUser", expirationDays = 7 }),
            Encoding.UTF8, "application/json");

        var response = await _env.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var sent = Assert.Single(_env.EmailSender.Sent);
        return TestInviteTokens.ExtractTokenFromEmailBody(sent.Body);
    }

    private async Task<string> SeedAdminAsync(string tenantId)
    {
        await _env.Factory.SeedPermissionsAsync();

        var user = await CreateUserAsync($"admin_{Guid.NewGuid():N}@f6.test");

        using var scope = _env.Factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleManager.RoleExistsAsync("TenantAdmin"))
        {
            await roleManager.CreateAsync(new ApplicationRole("TenantAdmin")
            {
                Code = "TenantAdmin",
                DisplayName = "Tenant Administrator",
                IsSystem = true,
                NormalizedName = "TENANTADMIN"
            });
        }

        var membership = TenantMembership.Create(user.Id, tenantId, "TenantAdmin", TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess);
        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();

        return _env.Factory.GenerateTestToken(user.Id, user.Email!, ["TenantAdmin"]);
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
