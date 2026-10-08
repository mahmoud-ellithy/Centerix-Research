using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Data.Migrations;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// F5 — the TenantMemberships.RoleName two-value allow-list as a property of the DATA.
/// <para>
/// The F3 domain contract rejects non-canonical roles at every write path, but the table is
/// also reachable through seeds, repairs and raw SQL. The CHECK constraint
/// <c>CK_TenantMemberships_RoleName_TenantRoleAllowList</c> makes the contract durable at the
/// storage layer, and the migration that adds it is FAIL-CLOSED: when pre-existing
/// non-conforming rows are present it THROWs (reporting count + identifying sample) instead
/// of adding the constraint, and it never deletes or modifies a row. An operator remediates
/// explicitly and re-runs migrations.
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
    /// Fail-closed proof on the exact five-row adversarial dataset: TenantAdmin, TenantUser,
    /// PlatformAdmin, CustomRole, tenantadmin.
    /// <para>
    /// Inside ONE transaction with the constraint dropped (the pre-F5 schema), the five legacy
    /// rows are inserted via raw SQL. The REAL migration (<see
    /// cref="AddRoleNameTenantRoleAllowList.Up"/>, invoked — not copied) is then executed:
    /// it must THROW, must delete/modify NOTHING (all five rows survive), and must fail the
    /// same way on a deterministic re-run. After an EXPLICIT operator remediation step
    /// (performed by the test as the operator would), re-executing the same migration
    /// succeeds and the canonical restriction is enforced again. A ROLLBACK restores both
    /// the constraint (SQL Server DDL is transactional) and the contents, leaving no residue.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FailClosed_AdversarialRows_ArePreserved_AndConstraintEnforcedAfterRemediation()
    {
        const string tenantId = "f5-failclosed-tenant";
        await EnsureTenantAsync(tenantId);

        // The exact adversarial dataset from the review.
        string[] roles = ["TenantAdmin", "TenantUser", "PlatformAdmin", "CustomRole", "tenantadmin"];
        var userIds = new List<string>();
        foreach (var role in roles)
        {
            var user = await CreateUserAsync($"f5fc_{role}_{Guid.NewGuid():N}@f5.test");
            userIds.Add(user.Id);
        }

        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            // 1. Pre-F5 schema: drop the constraint (transactional DDL).
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [Platform].[TenantMemberships] DROP CONSTRAINT [{ConstraintName}]");

            // 2. Pre-migration state: five rows, bypassing the domain gate via raw SQL.
            for (var i = 0; i < roles.Length; i++)
            {
                var userId = userIds[i];
                var role = roles[i];
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO [Platform].[TenantMemberships] ([UserId], [TenantId], [RoleName], [Status], [JoinedAtUtc]) VALUES ({userId}, {tenantId}, {role}, 0, SYSUTCDATETIME())");
            }

            Assert.Equal(5, await CountTenantRowsAsync(db, tenantId));

            // 3. Execute the REAL migration: it must fail safely.
            var first = await Assert.ThrowsAsync<SqlException>(() => ExecuteF5MigrationUpAsync(db));
            Assert.Equal(51000, first.Number);
            Assert.Contains(ConstraintName, first.Message);
            Assert.Contains("3 row(s)", first.Message);

            // 4. No membership row was silently deleted or modified.
            Assert.Equal(5, await CountTenantRowsAsync(db, tenantId));
            var survivingRoles = await db.TenantMemberships.AsNoTracking()
                .Where(m => m.TenantId == tenantId)
                .Select(m => m.RoleName)
                .ToListAsync();
            Assert.Equivalent(
                roles.OrderBy(r => r, StringComparer.Ordinal).ToList(),
                survivingRoles.OrderBy(r => r, StringComparer.Ordinal).ToList());

            // 5. Determinism: a second run fails identically and still preserves everything.
            var second = await Assert.ThrowsAsync<SqlException>(() => ExecuteF5MigrationUpAsync(db));
            Assert.Equal(51000, second.Number);
            Assert.Equal(5, await CountTenantRowsAsync(db, tenantId));

            // 6. EXPLICIT operator remediation (a deliberate, auditable step — never performed
            // by the migration itself): normalize the three non-canonical rows.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [Platform].[TenantMemberships] SET [RoleName] = 'TenantUser' WHERE [TenantId] = {tenantId} AND [RoleName] COLLATE Latin1_General_CS_AS NOT IN ('TenantAdmin', 'TenantUser')");
            Assert.Equal(5, await CountTenantRowsAsync(db, tenantId));

            // 7. Re-executing the same migration now succeeds and the constraint is back.
            await ExecuteF5MigrationUpAsync(db);
            Assert.True(await ConstraintExistsAsync(db));
            Assert.Equal(5, await CountTenantRowsAsync(db, tenantId));

            // 8. The canonical restriction is enforced again: a new bad row is rejected by
            // the constraint, naming it so operators can find it.
            var badUser = await CreateUserAsync($"f5fc_bad_{Guid.NewGuid():N}@f5.test");
            var badUserId = badUser.Id;
            var rejected = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [Platform].[TenantMemberships] ([UserId], [TenantId], [RoleName], [Status], [JoinedAtUtc]) VALUES ({badUserId}, {tenantId}, 'CustomRole', 0, SYSUTCDATETIME())"));
            Assert.Contains(ConstraintName, rejected.Message);
        }
        finally
        {
            // 9. Roll back: constraint and contents must both be as they were.
            await transaction.RollbackAsync();
        }

        Assert.True(
            await ConstraintExistsAsync(),
            "SQL Server DDL is transactional: the DROP CONSTRAINT must be rolled back too.");

        using (var verifyScope = _env.Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(
                0,
                await verifyDb.TenantMemberships.AsNoTracking().CountAsync(m => m.TenantId == tenantId));
        }
    }

    // ==================================================================
    // Helpers
    // ==================================================================

    /// <summary>
    /// Executes the REAL F5 migration (<see cref="AddRoleNameTenantRoleAllowList.Up"/>) by
    /// generating its SQL Server commands from the migration class itself — never a copy of
    /// its SQL — and running them on the given connection (inside the caller's transaction
    /// when one is active). A fail-closed guard violation surfaces as
    /// <see cref="SqlException"/> (error 51000).
    /// </summary>
    private static async Task ExecuteF5MigrationUpAsync(AppDbContext db)
    {
        var migration = new AddRoleNameTenantRoleAllowList();
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        typeof(Migration).GetMethod("Up", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(migration, [builder]);

        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(builder.Operations, db.Model))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
    }

    private static Task<int> CountTenantRowsAsync(AppDbContext db, string tenantId) =>
        db.TenantMemberships.AsNoTracking().CountAsync(m => m.TenantId == tenantId);

    /// <summary>Same-connection variant for use inside an uncommitted transaction.</summary>
    private static async Task<bool> ConstraintExistsAsync(AppDbContext db)
    {
        var count = await db.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'Platform.TenantMemberships') AND name = {ConstraintName}")
            .SingleAsync();
        return count == 1;
    }

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
