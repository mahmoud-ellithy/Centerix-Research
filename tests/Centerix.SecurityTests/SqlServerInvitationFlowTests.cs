using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Centerix.Domain.Platform.Authorization;
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
/// Relational integration tests against a REAL SQL Server database (real migrations applied).
/// These tests exist because EF InMemory allowed migration/model drift to escape detection.
/// They verify schema, persistence, transactional atomicity, concurrency and cross-tenant
/// isolation with true relational semantics. The environment comes from the collection fixture.
/// </summary>
[Collection("SqlServerIntegration")]
public class SqlServerInvitationFlowTests
{
    private const string StrongPassword = "Str0ng!Pass1";

    private readonly SqlServerIntegrationFactory _env;

    public SqlServerInvitationFlowTests(SqlServerIntegrationFactory env)
    {
        _env = env;
        _env.EmailSender.Clear();
    }

    // ==================================================================
    // Schema / migration verification
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Migrations_Applied_SchemaContainsRoleNameAndInvitations_NoPendingMigrations()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantDbContext = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // The full migration chain applied cleanly.
        Assert.Empty(await appDbContext.Database.GetPendingMigrationsAsync());
        Assert.Empty(await tenantDbContext.Database.GetPendingMigrationsAsync());

        // The exact drift that escaped InMemory testing is present in the real schema.
        var roleNameExists = await appDbContext.Database.SqlQuery<bool>(
            $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'Platform'
                  AND TABLE_NAME = 'TenantMemberships'
                  AND COLUMN_NAME = 'RoleName') THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Value
            """).SingleAsync();

        var invitationsTableExists = await appDbContext.Database.SqlQuery<bool>(
            $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = 'Platform'
                  AND TABLE_NAME = 'TenantInvitations') THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS Value
            """).SingleAsync();

        Assert.True(roleNameExists, "Platform.TenantMemberships.RoleName is missing from the deployed schema");
        Assert.True(invitationsTableExists, "Platform.TenantInvitations is missing from the deployed schema");
    }

    // ==================================================================
    // Registration from invitation (new users)
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_NewUser_PersistsUserMembershipRoleName_AndAcceptsInvitation()
    {
        const string tenantId = "sql-reg-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);
        var email = UniqueEmail("register");

        var rawToken = await CreateInvitationViaApiAsync(adminToken, tenantId, email);

        var response = await _env.Client.SendAsync(
            AnonymousPost("/api/invitations/register", new { token = rawToken, password = StrongPassword }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        // RoleName persisted through the REAL column added by the new migration.
        var membership = db.TenantMemberships.Single(m => m.UserId == user!.Id && m.TenantId == tenantId);
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);

        var invitation = db.TenantInvitations.Single(i => i.NormalizedEmail == email.ToUpperInvariant());
        Assert.Equal(InvitationStatus.Accepted, invitation.Status);
        Assert.Equal(user.Id, invitation.AcceptedByUserId);
        Assert.NotNull(invitation.AcceptedAtUtc);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_InvalidToken_Returns401()
    {
        var response = await _env.Client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token = "not-a-real-token", password = StrongPassword }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_ExpiredToken_MarksExpired_Returns409_PersistsTransition()
    {
        const string tenantId = "sql-expired-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);

        var invitation = await SeedInvitationAsync(tenantId, UniqueEmail("expired"), InvitationStatus.Pending, expiresInDays: -1);

        var response = await _env.Client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token = invitation.RawToken, password = StrongPassword }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Expiry transition persisted relationally.
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(InvitationStatus.Expired, db.TenantInvitations.Single(i => i.TokenHash == invitation.TokenHash).Status);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_RevokedToken_Returns409_StatePersists()
    {
        const string tenantId = "sql-revoked-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);

        var invitation = await SeedInvitationAsync(tenantId, UniqueEmail("revoked"), InvitationStatus.Revoked);

        var response = await _env.Client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token = invitation.RawToken, password = StrongPassword }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(InvitationStatus.Revoked, db.TenantInvitations.Single(i => i.TokenHash == invitation.TokenHash).Status);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_AlreadyAccepted_Returns409()
    {
        const string tenantId = "sql-accepted-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);

        var invitation = await SeedInvitationAsync(tenantId, UniqueEmail("accepted"), InvitationStatus.Accepted);

        var response = await _env.Client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token = invitation.RawToken, password = StrongPassword }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_ExistingEmail_Returns409_InvitationRemainsPending()
    {
        const string tenantId = "sql-existing-email-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);

        var email = UniqueEmail("existing");
        await CreateUserAsync(email);
        var rawToken = await CreateInvitationViaApiAsync(adminToken, tenantId, email);

        var response = await _env.Client.SendAsync(AnonymousPost(
            "/api/invitations/register",
            new { token = rawToken, password = StrongPassword }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, db.Users.Count(u => u.NormalizedEmail == email.ToUpperInvariant()));
        Assert.Equal(
            InvitationStatus.Pending,
            db.TenantInvitations.Single(i => i.NormalizedEmail == email.ToUpperInvariant()).Status);
    }

    /// <summary>
    /// Atomicity proof on real SQL Server: the invitation points at a tenant id absent from the
    /// registry, so the membership INSERT violates FK_TenantMemberships_TenantRegistry_TenantId at
    /// SaveChanges. The explicit transaction must roll back the IdentityUser creation as well —
    /// no orphan user, no membership, invitation still Pending and usable afterwards.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_MembershipInsertFails_RollsBackUserCreation_NoOrphanIdentityUser()
    {
        const string ghostTenantId = "ghost-tenant-does-not-exist";
        const string hostTenantId = "sql-orphan-tenant";
        await EnsureTenantAsync(hostTenantId);
        var adminToken = await SeedAdminAsync(hostTenantId);

        var email = UniqueEmail("orphan");
        var rawToken = await CreateInvitationViaApiAsync(adminToken, hostTenantId, email);
        var tokenHash = TestInviteTokens.Sha256Hex(rawToken);

        // Repoint the invitation at the nonexistent tenant to force the FK failure at SaveChanges.
        using (var repoint = _env.Factory.Services.CreateScope())
        {
            var db = repoint.ServiceProvider.GetRequiredService<AppDbContext>();
            var invitation = db.TenantInvitations.Single(i => i.TokenHash == tokenHash);

            typeof(TenantInvitation).GetProperty(nameof(TenantInvitation.TenantId))!
                .SetValue(invitation, ghostTenantId);
            await db.SaveChangesAsync();
        }

        var response = await _env.Client.SendAsync(AnonymousPost(
            "/api/invitations/register", new { token = rawToken, password = StrongPassword }));

        Assert.False(response.IsSuccessStatusCode);

        using var verify = _env.Factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        // No orphan IdentityUser, no membership for the ghost tenant.
        Assert.Equal(0, vdb.Users.Count(u => u.NormalizedEmail == email.ToUpperInvariant()));
        Assert.Empty(vdb.TenantMemberships.Where(m => m.TenantId == ghostTenantId));

        // Invitation mutation rolled back too: still Pending (usable).
        var after = vdb.TenantInvitations.AsNoTracking().Single(i => i.TokenHash == tokenHash);
        Assert.Equal(InvitationStatus.Pending, after.Status);
        Assert.Null(after.AcceptedByUserId);
    }

    /// <summary>Concurrent registrations racing on one token: exactly one wins.</summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Register_ConcurrentSameToken_ExactlyOneSucceeds_OneUserOneMembership()
    {
        const string tenantId = "sql-race-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);
        var email = UniqueEmail("race");

        var rawToken = await CreateInvitationViaApiAsync(adminToken, tenantId, email);

        var request1 = AnonymousPost("/api/invitations/register", new { token = rawToken, password = StrongPassword });
        var request2 = AnonymousPost("/api/invitations/register", new { token = rawToken, password = StrongPassword });
        var responses = await Task.WhenAll(_env.Client.SendAsync(request1), _env.Client.SendAsync(request2));

        Assert.Single(responses.Where(r => r.IsSuccessStatusCode));

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, db.Users.Count(u => u.NormalizedEmail == email.ToUpperInvariant()));
        Assert.Equal(1, db.TenantMemberships.Count(m => m.TenantId == tenantId));
        Assert.Equal(
            InvitationStatus.Accepted,
            db.TenantInvitations.Single(i => i.NormalizedEmail == email.ToUpperInvariant()).Status);
    }

    // ==================================================================
    // Existing-user acceptance, reactivation, isolation, resolution
    // ==================================================================

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Accept_ExistingUser_CreatesMembership_OnSqlServer()
    {
        const string tenantId = "sql-accept-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);

        var email = UniqueEmail("acceptor");
        var user = await CreateUserAsync(email);
        var rawToken = await CreateInvitationViaApiAsync(adminToken, tenantId, email);

        var response = await _env.Client.SendAsync(AcceptRequest(rawToken, user, tenantId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var membership = db.TenantMemberships.Single(m => m.UserId == user.Id && m.TenantId == tenantId);
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Accept_ReactivatesPreviouslyRevokedMembership_SingleRow()
    {
        const string tenantId = "sql-reactivate-tenant";
        await EnsureTenantAsync(tenantId);
        var adminToken = await SeedAdminAsync(tenantId);

        var email = UniqueEmail("reactivate");
        var user = await CreateUserAsync(email);

        using (var seedScope = _env.Factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var revoked = TenantMembership.Create(user.Id, tenantId, "TenantUser", TenantMembershipStatus.Revoked);
            Assert.True(revoked.IsSuccess);
            db.TenantMemberships.Add(revoked.Value);
            await db.SaveChangesAsync();
        }

        var rawToken = await CreateInvitationViaApiAsync(adminToken, tenantId, email);
        var response = await _env.Client.SendAsync(AcceptRequest(rawToken, user, tenantId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _env.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var memberships = db2.TenantMemberships
            .AsNoTracking()
            .Where(m => m.UserId == user.Id && m.TenantId == tenantId)
            .ToList();

        var membership = Assert.Single(memberships); // reactivated in place, no duplicate row
        Assert.Equal(TenantMembershipStatus.Active, membership.Status);
        Assert.Equal("TenantUser", membership.RoleName);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task RoleName_RoundTrips_ThroughRealColumn()
    {
        const string tenantId = "sql-rolename-tenant";
        await EnsureTenantAsync(tenantId);

        var roleName = $"Ops Manager L{Random.Shared.Next(100)}"; // nvarchar(128), spaces preserved
        var user = await CreateUserAsync(UniqueEmail("rolename"));

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = TenantMembership.Create(user.Id, tenantId, roleName, TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reloaded = db.TenantMemberships.AsNoTracking()
                .Single(m => m.UserId == user.Id && m.TenantId == tenantId);
            Assert.Equal(roleName, reloaded.RoleName);
        }
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task DuplicateMembership_ViolatesPrimaryKey()
    {
        const string tenantId = "sql-dupe-tenant";
        await EnsureTenantAsync(tenantId);
        var user = await CreateUserAsync(UniqueEmail("dupe"));

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var first = TenantMembership.Create(user.Id, tenantId);
        Assert.True(first.IsSuccess);
        db.TenantMemberships.Add(first.Value);
        await db.SaveChangesAsync();

        var second = TenantMembership.Create(user.Id, tenantId);
        Assert.True(second.IsSuccess);
        db.TenantMemberships.Add(second.Value);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task PermissionResolution_TenantAdminAllowed_TenantUserDenied_AtHttpLevel()
    {
        const string tenantId = "sql-perm-tenant";
        await EnsureTenantAsync(tenantId);

        var adminToken = await SeedAdminAsync(tenantId); // TenantAdmin → full permission catalog

        var userEmail = UniqueEmail("plainuser");
        var user = await CreateUserAsync(userEmail);
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = TenantMembership.Create(user.Id, tenantId, "TenantUser", TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }

        var userToken = _env.Factory.GenerateTestToken(user.Id, userEmail, []);

        // TenantAdmin: Invitations.Read granted via role-permission rows in SQL → 200.
        var adminListRequest = new HttpRequestMessage(HttpMethod.Get, "/api/invitations");
        adminListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        adminListRequest.Headers.Add("tenant", tenantId);
        Assert.Equal(HttpStatusCode.OK, (await _env.Client.SendAsync(adminListRequest)).StatusCode);

        // TenantUser: Invitations.Create NOT granted → 403 despite active membership.
        var userCreateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/invitations");
        userCreateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        userCreateRequest.Headers.Add("tenant", tenantId);
        userCreateRequest.Content = new StringContent(
            JsonSerializer.Serialize(new { email = UniqueEmail("denied"), roleName = "TenantUser" }),
            Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await _env.Client.SendAsync(userCreateRequest)).StatusCode);

        // TenantUser: Memberships.Read IS granted → 200 (positive control).
        var userMeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/memberships/me");
        userMeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        userMeRequest.Headers.Add("tenant", tenantId);
        Assert.Equal(HttpStatusCode.OK, (await _env.Client.SendAsync(userMeRequest)).StatusCode);
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task CrossTenantIsolation_UserWithoutMembership_Denied_OnRealDatabase()
    {
        const string tenantA = "sql-iso-a";
        const string tenantB = "sql-iso-b";
        await EnsureTenantAsync(tenantA);
        await EnsureTenantAsync(tenantB);

        // Seeds the permission catalog and TenantAdmin/TenantUser roles so the students
        // endpoint's Students.Read requirement can be satisfied for a TenantAdmin member.
        await _env.Factory.SeedPermissionsAsync();

        var email = UniqueEmail("isolated");
        var user = await CreateUserAsync(email);

        // Membership ONLY in tenant A, with an admin role for the students endpoints.
        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = TenantMembership.Create(user.Id, tenantA, "TenantAdmin", TenantMembershipStatus.Active);
            Assert.True(membership.IsSuccess);
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }

        var token = _env.Factory.GenerateTestToken(user.Id, email, ["TenantAdmin"]);

        // Requesting tenant A: allowed.
        var okRequest = new HttpRequestMessage(HttpMethod.Get, "/api/students");
        okRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        okRequest.Headers.Add("tenant", tenantA);
        Assert.Equal(HttpStatusCode.OK, (await _env.Client.SendAsync(okRequest)).StatusCode);

        // Requesting tenant B with only-A membership: guard rejects against real data.
        var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/students");
        deniedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        deniedRequest.Headers.Add("tenant", tenantB);
        Assert.Equal(HttpStatusCode.Forbidden, (await _env.Client.SendAsync(deniedRequest)).StatusCode);
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string UniqueEmail(string prefix) => $"{prefix}_{Guid.NewGuid():N}@sql.test";

    private static HttpRequestMessage AnonymousPost(string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
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

    private Task EnsureTenantAsync(string id)
        => EnsureTenantAsync(_env.Factory.Services.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>(), id);

    private async Task<IdentityUser> CreateUserAsync(string email, string password = StrongPassword)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null) return existing;

        var user = new IdentityUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        };
        user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, password);
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return user;
    }

    /// <summary>
    /// Seeds roles + permission catalog + an ACTIVE TenantAdmin membership for the tenant and
    /// returns the admin's bearer JWT.
    /// </summary>
    private async Task<string> SeedAdminAsync(string tenantId)
    {
        await _env.Factory.SeedPermissionsAsync();

        var adminEmail = $"admin_{Guid.NewGuid():N}@sql.test";
        var user = await CreateUserAsync(adminEmail);

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

    private sealed record SeededInvitation(Guid Id, string RawToken, string TokenHash, string Email);

    /// <summary>Seeds an invitation row directly in an arbitrary state without the create endpoint.</summary>
    private async Task<SeededInvitation> SeedInvitationAsync(
        string tenantId, string email, InvitationStatus status, int expiresInDays = 7)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // InvitedByUserId must satisfy the AspNetUsers FK: use any seeded admin of this tenant.
        var inviterMembership = db.TenantMemberships
            .Where(m => m.TenantId == tenantId && m.RoleName == "TenantAdmin")
            .OrderBy(m => m.JoinedAtUtc)
            .First();

        var rawToken = TestInviteTokens.NewToken();
        var tokenHash = TestInviteTokens.Sha256Hex(rawToken);

        // Domain validation requires a FUTURE expiry at creation; a past expiry is applied
        // afterwards via reflection so tests can exercise the runtime expiry-check path.
        var createdAtExpiry = DateTimeOffset.UtcNow.AddDays(expiresInDays > 0 ? expiresInDays : 7);

        var invitationResult = TenantInvitation.Create(
            Guid.NewGuid(), tenantId, email, inviterMembership.UserId,
            "TenantUser", tokenHash, createdAtExpiry);
        Assert.True(invitationResult.IsSuccess);
        var invitation = invitationResult.Value;

        switch (status)
        {
            case InvitationStatus.Revoked:
                invitation.Revoke(inviterMembership.UserId);
                break;
            case InvitationStatus.Accepted:
                invitation.Accept(inviterMembership.UserId);
                break;
            case InvitationStatus.Expired:
                invitation.MarkExpired();
                break;
            case InvitationStatus.Pending when expiresInDays <= 0:
                typeof(TenantInvitation).GetProperty(nameof(TenantInvitation.ExpiresAtUtc))!
                    .SetValue(invitation, DateTimeOffset.UtcNow.AddDays(expiresInDays));
                break;
        }

        db.TenantInvitations.Add(invitation);
        await db.SaveChangesAsync();
        return new SeededInvitation(invitation.Id, rawToken, tokenHash, email);
    }

    /// <summary>Creates an invitation through the API and recovers the RAW token from the captured e-mail.</summary>
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
        Assert.Equal(email, sent.To);

        return TestInviteTokens.ExtractTokenFromEmailBody(sent.Body);
    }
}
