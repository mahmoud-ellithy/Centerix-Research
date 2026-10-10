using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
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

// REV-002: the offer evaluation endpoint MUST reject any client-supplied clock.
// The test passes an "evaluationTimeUtc" inside an expired-but-Active window and
// asserts the server ignores the field and returns the no-promotion price.
[Collection("Integration")]
public class Rev002ServerSideOfferEvaluationTimeTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public Rev002ServerSideOfferEvaluationTimeTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostOfferCalculate_ClientEvaluationTimeUtc_IsIgnored()
    {
        var env = await SeedAsync();

        // An "old"/expired-but-Active window: endsAtUtc is in the past for the SERVER clock,
        // and startsAtUtc is also far in the past. The promotion is Active in the DB.
        // A client that could pass evaluationTimeUtc into the past would replay prices.
        var (planId, _) = await SeedPlanAndExpiredPromotionAsync(env.TenantId);
        var noPromotionBase = 12 * 1000m; // mirror the plan's monthlyPrice × 12

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/offers/calculate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", env.Token);
        request.Headers.Add("tenant", env.TenantId);
        request.Content = JsonContent.Create(new
        {
            planId,
            durationMonths = 12,
            paymentTerms = 0, // PaymentTerms.FullUpfront
            // REV-002: the field MUST NOT bind on the wire.
            evaluationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // finalAmount must equal the no-promotion base price — the expired promotion was NOT
        // resurrected by the attacker-supplied evaluationTimeUtc.
        var finalAmount = root.GetProperty("finalAmount").GetDecimal();
        var discountAmount = root.GetProperty("discountAmount").GetDecimal();

        Assert.Equal(noPromotionBase, finalAmount);
        Assert.Equal(0m, discountAmount);
    }

    [Fact]
    public async Task PostOfferCalculate_RequestBody_EvaluationTimeUtc_DoesNotBind()
    {
        // The request body no longer contains an evaluationTimeUtc property. Probing the contract
        // directly is enough: ASP.NET Core model binder will not find the field, so it has no
        // effect on the server-side evaluation. The previous test already proves the commercial
        // invariant; this one captures the contract.
        var env = await SeedAsync();
        var (planId, _) = await SeedPlanAndExpiredPromotionAsync(env.TenantId);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/offers/calculate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", env.Token);
        request.Headers.Add("tenant", env.TenantId);

        // Same payload shape the attacker would send. We deliberately include the extra field to
        // prove the binder ignores it (no error, no special-casing in the controller either).
        request.Content = JsonContent.Create(new
        {
            planId,
            durationMonths = 12,
            paymentTerms = 0,
            evaluationTimeUtc = "2020-01-01T00:00:00Z"
        });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(12_000m, root.GetProperty("finalAmount").GetDecimal());
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private sealed record Env(string Token, string TenantId);

    private async Task<Env> SeedAsync()
    {
        await _factory.SeedPermissionsAsync();

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var store = sp.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

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

        var tenantId = Guid.NewGuid().ToString();
        var identifier = tenantId;

        var tenant = Tenant.Create(
            Guid.Parse(tenantId), identifier, identifier, identifier,
            "EG", "EGP", "Africa/Cairo", "O", "W",
            $"{identifier}@rev002.test", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        var stored = await store.TryGetAsync(tenantId);
        if (stored is null)
        {
            await store.TryAddAsync(new CenterixTenantInfo
            {
                Id = tenantId,
                Identifier = identifier,
                Name = identifier,
                Email = $"{identifier}@rev002.test",
                IsActive = true,
                ValidUpTo = DateTime.UtcNow.AddYears(1),
                CreatedAt = DateTime.UtcNow
            });
        }

        var tenantAdmin = new IdentityUser
        {
            Email = $"tadmin-{identifier}@rev002.test",
            UserName = $"tadmin-{identifier}@rev002.test",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(tenantAdmin, "Rev002@test123!");
        await userManager.AddToRoleAsync(tenantAdmin, "TenantAdmin");

        if (!db.TenantMemberships.Any(m => m.UserId == tenantAdmin.Id && m.TenantId == tenantId))
        {
            db.TenantMemberships.Add(TenantMembership.Create(
                tenantAdmin.Id, tenantId, "TenantAdmin", TenantMembershipStatus.Active).Value);
        }

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var token = _factory.GenerateTestToken(
            tenantAdmin.Id, tenantAdmin.Email!, new[] { "TenantAdmin" });

        return new Env(token, tenantId);
    }

    private async Task<(int PlanId, int PromotionId)> SeedPlanAndExpiredPromotionAsync(string tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var planCode = $"REV002-{Guid.NewGuid():N}"[..24];
        var planResult = Plan.Create(
            id: 0,
            code: planCode,
            displayName: "REV-002 Plan",
            monthlyPrice: 1000m,
            maxStudents: 100,
            maxUsers: 50,
            maxBranches: 10,
            maxTeachers: 20,
            storageGB: 100,
            smsQuota: 1000,
            isActive: true,
            currencyCode: "EGP",
            durationMonths: 12,
            bonusMonths: 0);
        Assert.True(planResult.IsSuccess);
        var plan = planResult.Value;

        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        // Promotion whose window is completely in the past relative to the server clock.
        var promoResult = Promotion.Create(
            id: 0,
            name: $"REV002-Expired-{Guid.NewGuid():N}"[..24],
            type: PromotionType.PercentageDiscount,
            planId: plan.Id,
            durationMonths: 12,
            startsAtUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            endsAtUtc: new DateTime(2020, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            priority: 100,
            percentage: 50m);
        Assert.True(promoResult.IsSuccess);
        var promo = promoResult.Value;
        Assert.True(promo.Activate().IsSuccess);

        db.Promotions.Add(promo);
        await db.SaveChangesAsync();

        return (plan.Id, promo.Id);
    }
}
