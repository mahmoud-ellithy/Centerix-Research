using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// F5 — the TenantMemberships.RoleName two-value allow-list as a property of the DATA.
/// <para>
/// The F3 domain contract rejects non-canonical roles at every write path, but the table is
/// also reachable through seeds, repairs and raw SQL. The CHECK constraint
/// <c>CK_TenantMemberships_RoleName_TenantRoleAllowList</c> makes the contract durable at the
/// storage layer, and the migration that adds it first DELETEs pre-existing non-conforming rows
/// with a predicate mirrored 1:1 from the constraint — so the migration is safe on a populated
/// production database.
/// </para>
/// <para>
/// All proofs run against the REAL migrated database of the SqlServerIntegration collection.
/// </para>
/// </summary>
[Collection("SqlServerIntegration")]
public class F5_TenantRoleCheckConstraintTests
{
    private const string ConstraintName = "CK_TenantMemberships_RoleName_TenantRoleAllowList";

    private readonly SqlServerIntegrationFactory _env;

    public F5_TenantRoleCheckConstraintTests(SqlServerIntegrationFactory env) => _env = env;

    private sealed class CheckConstraintRow
    {
        public string Name { get; set; } = string.Empty;
        public string Definition { get; set; } = string.Empty;
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task CheckConstraint_IsPresent_WithTheExactAllowList()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.Database.SqlQuery<CheckConstraintRow>(
                $"SELECT name AS [Name], definition AS [Definition] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'Platform.TenantMemberships') AND name = {ConstraintName}")
            .ToListAsync();

        var row = Assert.Single(rows);
        // SQL Server may normalize a small IN-list into a chain of "=" comparisons, so assert on
        // the semantic ingredients rather than the exact shape of the definition.
        Assert.Contains("[RoleName]", row.Definition);
        Assert.Contains("'TenantAdmin'", row.Definition);
        Assert.Contains("'TenantUser'", row.Definition);
        // Case-sensitivity of the backstop must be at least as strict as the domain gate.
        Assert.Contains("Latin1_General_CS_AS", row.Definition);
    }

    /// <summary>
    /// The domain gate is bypassed (reflection overwrite, exactly like a pre-invariant writer
    /// would) — the DATABASE must be the one to refuse the row, and the refusal must name the
    /// constraint so operators can find it.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Insert_WithNonCanonicalRole_IsRejected_ByTheCheckConstraint()
    {
        const string tenantId = "f5-reject-tenant";
        await EnsureTenantAsync(tenantId);
        var user = await CreateUserAsync($"f5_{Guid.NewGuid():N}@f5.test");

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var membership = TenantMembership.Create(user.Id, tenantId, "TenantUser", TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess);
        typeof(TenantMembership).GetProperty(nameof(TenantMembership.RoleName))!
            .SetValue(membership.Value, "Ops Manager");
        db.TenantMemberships.Add(membership.Value);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync());

        var message = $"{exception.Message} {exception.InnerException?.Message}";
        Assert.Contains(ConstraintName, message);
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("TenantUser")]
    [Trait("Category", "SqlServer")]
    public async Task Insert_WithCanonicalRole_Succeeds(string roleName)
    {
        const string tenantId = "f5-accept-tenant";
        await EnsureTenantAsync(tenantId);
        var user = await CreateUserAsync($"f5_{Guid.NewGuid():N}@f5.test");

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var membership = TenantMembership.Create(user.Id, tenantId, roleName, TenantMembershipStatus.Active);
        Assert.True(membership.IsSuccess);
        db.TenantMemberships.Add(membership.Value);
        await db.SaveChangesAsync();

        var reloaded = await db.TenantMemberships.AsNoTracking()
            .SingleAsync(m => m.UserId == user.Id && m.TenantId == tenantId);
        Assert.Equal(roleName, reloaded.RoleName);
    }

    /// <summary>
    /// The migration's DELETE predicate, executed exactly as written in
    /// AddRoleNameTenantRoleAllowList.Up(): inside ONE transaction with the constraint dropped,
    /// a legacy row must be removed while a canonical row survives — and a ROLLBACK must restore
    /// BOTH the constraint (SQL Server DDL is transactional) and the database contents, leaving
    /// no test residue behind.
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task MigrationDelete_RemovesOnlyNonCanonicalRows_AndTheConstraintSurvivesRollback()
    {
        const string tenantId = "f5-legacy-tenant";
        await EnsureTenantAsync(tenantId);
        var legacyUser = await CreateUserAsync($"f5legacy_{Guid.NewGuid():N}@f5.test");
        var canonicalUser = await CreateUserAsync($"f5canon_{Guid.NewGuid():N}@f5.test");

        // Pre-transaction state: the constraint must exist BEFORE we tamper with it.
        Assert.True(await ConstraintExistsAsync());

        {
            using var scope = _env.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync();

            // 1. Drop the constraint (transactional DDL).
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [Platform].[TenantMemberships] DROP CONSTRAINT [{ConstraintName}]");

            // 2. Seed one legacy row (bypassing the now-dropped check) and one canonical row.
            var legacy = TenantMembership.Create(
                legacyUser.Id, tenantId, "TenantUser", TenantMembershipStatus.Active).Value;
            typeof(TenantMembership).GetProperty(nameof(TenantMembership.RoleName))!
                .SetValue(legacy, "Ops Manager");
            db.TenantMemberships.Add(legacy);

            db.TenantMemberships.Add(
                TenantMembership.Create(
                    canonicalUser.Id, tenantId, "TenantAdmin", TenantMembershipStatus.Active).Value);
            await db.SaveChangesAsync();

            // 3. The migration's DELETE literal, verbatim.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM [Platform].[TenantMemberships] " +
                "WHERE [RoleName] COLLATE Latin1_General_CS_AS NOT IN ('TenantAdmin', 'TenantUser')");

            var legacyGone = !await db.TenantMemberships.AsNoTracking().AnyAsync(
                m => m.UserId == legacyUser.Id && m.TenantId == tenantId);
            var canonicalKept = await db.TenantMemberships.AsNoTracking().AnyAsync(
                m => m.UserId == canonicalUser.Id && m.TenantId == tenantId);

            Assert.True(legacyGone, "The migration DELETE must remove the non-canonical row.");
            Assert.True(canonicalKept, "The migration DELETE must never touch a canonical row.");

            // 4. Roll back: constraint and contents must both be as they were.
            await transaction.RollbackAsync();
        }

        Assert.True(
            await ConstraintExistsAsync(),
            "SQL Server DDL is transactional: the DROP CONSTRAINT must be rolled back too.");

        using (var scope = _env.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(
                await db.TenantMemberships.AsNoTracking().AnyAsync(
                    m => (m.UserId == legacyUser.Id || m.UserId == canonicalUser.Id)
                      && m.TenantId == tenantId),
                "The proof transaction must leave no rows behind.");
        }
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    private async Task<bool> ConstraintExistsAsync()
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'Platform.TenantMemberships') AND name = {ConstraintName}")
            .SingleAsync();
        return count == 1;
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

    private const string StrongPassword = "Str0ng!Pass1";
}
