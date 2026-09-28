using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Promotions.Enums;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
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
/// TASK A.1 — HTTP-level regression coverage for the explicit PaymentTerms commercial decision.
///
/// The domain/handler tests prove the invariant; these tests prove the WIRE contract:
///   * POST /api/TenantPlans/{id}/renew-commercial  and
///   * POST /api/TenantPlans/{id}/change-plan
/// must carry the operator's explicit PaymentTerms choice (0 = FullUpfront, 1 = Installments)
/// all the way into the persisted Offer and Contract snapshot, and must REJECT the request
/// (creating no commercial transaction at all) when the field is absent.
///
/// The same plan + the same promotion are exercised with BOTH payment terms to prove
/// PaymentTerms stays an independent commercial decision and is never derived from
/// PromotionType, BonusMonths, installment rows, or prior payment history.
/// </summary>
[Collection("Integration")]
public class TaskA_PaymentTermsHttpIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TaskA_PaymentTermsHttpIntegrationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    #region Renewal endpoint

    [Fact]
    public async Task Renew_Http_FullUpfront_ReachesOfferAndContract()
    {
        var env = await SeedPlatformAsync();
        var subId = await SeedPlanAndActiveSubscriptionAsync(env.TenantId, planId: 9101, bonusMonths: 0);

        var response = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subId}/renew-commercial",
            new { paymentTerms = (int)PaymentTerms.FullUpfront },
            env);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var contractId = await ReadContractIdAsync(response);
        await AssertCommercialChainAsync(env.TenantId, contractId, PaymentTerms.FullUpfront);
    }

    [Fact]
    public async Task Renew_Http_Installments_ReachesOfferAndContract()
    {
        var env = await SeedPlatformAsync();
        var subId = await SeedPlanAndActiveSubscriptionAsync(env.TenantId, planId: 9102, bonusMonths: 0);

        var response = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subId}/renew-commercial",
            new { paymentTerms = (int)PaymentTerms.Installments },
            env);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var contractId = await ReadContractIdAsync(response);
        await AssertCommercialChainAsync(env.TenantId, contractId, PaymentTerms.Installments);
    }

    [Fact]
    public async Task Renew_Http_WithoutPaymentTerms_IsRejected_AndCreatesNothing()
    {
        var env = await SeedPlatformAsync();
        var subId = await SeedPlanAndActiveSubscriptionAsync(env.TenantId, planId: 9103, bonusMonths: 0);

        var (offersBefore, contractsBefore) = await CountCommercialAsync(env.TenantId);

        var response = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subId}/renew-commercial",
            new { }, // PaymentTerms deliberately absent
            env);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var (offersAfter, contractsAfter) = await CountCommercialAsync(env.TenantId);
        Assert.Equal(offersBefore, offersAfter);
        Assert.Equal(contractsBefore, contractsAfter);
    }

    [Fact]
    public async Task Renew_Http_UndefinedPaymentTermsValue_IsRejected()
    {
        var env = await SeedPlatformAsync();
        var subId = await SeedPlanAndActiveSubscriptionAsync(env.TenantId, planId: 9104, bonusMonths: 0);

        var (offersBefore, contractsBefore) = await CountCommercialAsync(env.TenantId);

        var response = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subId}/renew-commercial",
            new { paymentTerms = 7 }, // not a defined enum member
            env);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var (offersAfter, contractsAfter) = await CountCommercialAsync(env.TenantId);
        Assert.Equal(offersBefore, offersAfter);
        Assert.Equal(contractsBefore, contractsAfter);
    }

    #endregion

    #region Change-plan endpoint

    [Fact]
    public async Task ChangePlan_Http_FullUpfront_ReachesOfferAndContract()
    {
        var env = await SeedPlatformAsync();
        await SeedPlanAsync(9201, bonusMonths: 0);
        var subId = await SeedPlanAndActiveSubscriptionAsync(env.TenantId, planId: 9200, bonusMonths: 0);

        var response = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subId}/change-plan",
            new { newPlanId = 9201, paymentTerms = (int)PaymentTerms.FullUpfront },
            env);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var contractId = await ReadContractIdAsync(response);
        await AssertCommercialChainAsync(env.TenantId, contractId, PaymentTerms.FullUpfront);
    }

    [Fact]
    public async Task ChangePlan_Http_Installments_ReachesOfferAndContract()
    {
        var env = await SeedPlatformAsync();
        await SeedPlanAsync(9203, bonusMonths: 0);
        var subId = await SeedPlanAndActiveSubscriptionAsync(env.TenantId, planId: 9202, bonusMonths: 0);

        var response = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subId}/change-plan",
            new { newPlanId = 9203, paymentTerms = (int)PaymentTerms.Installments },
            env);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var contractId = await ReadContractIdAsync(response);
        await AssertCommercialChainAsync(env.TenantId, contractId, PaymentTerms.Installments);
    }

    [Fact]
    public async Task ChangePlan_Http_WithoutPaymentTerms_IsRejected_AndCreatesNothing()
    {
        var env = await SeedPlatformAsync();
        await SeedPlanAsync(9205, bonusMonths: 0);
        var subId = await SeedPlanAndActiveSubscriptionAsync(env.TenantId, planId: 9204, bonusMonths: 0);

        var (offersBefore, contractsBefore) = await CountCommercialAsync(env.TenantId);

        var response = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subId}/change-plan",
            new { newPlanId = 9205 }, // PaymentTerms deliberately absent
            env);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var (offersAfter, contractsAfter) = await CountCommercialAsync(env.TenantId);
        Assert.Equal(offersBefore, offersAfter);
        Assert.Equal(contractsBefore, contractsAfter);
    }

    #endregion

    #region Independence from the commercial context

    [Fact]
    public async Task SamePlanAndBonusMonths_AcceptsBothPaymentTerms_Renewal()
    {
        // Identical commercial context (same plan, same bonus months, no promotion) is renewed
        // twice with the two different explicit payment decisions. Both must succeed and each
        // contract must carry exactly the requested value — PaymentTerms is not derived.
        // One and the same plan (same id, same bonus months) is shared by both tenants.
        await SeedPlanAsync(9301, bonusMonths: 3);

        var envA = await SeedPlatformAsync();
        var subA = await SeedActiveSubscriptionAsync(envA.TenantId, planId: 9301, bonusMonths: 3);

        var envB = await SeedPlatformAsync();
        var subB = await SeedActiveSubscriptionAsync(envB.TenantId, planId: 9301, bonusMonths: 3);

        var fullUpfrontResponse = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subA}/renew-commercial",
            new { paymentTerms = (int)PaymentTerms.FullUpfront },
            envA);

        var installmentsResponse = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subB}/renew-commercial",
            new { paymentTerms = (int)PaymentTerms.Installments },
            envB);

        Assert.True(fullUpfrontResponse.StatusCode == HttpStatusCode.OK,
            $"FullUpfront renewal failed: {await fullUpfrontResponse.Content.ReadAsStringAsync()}");
        Assert.True(installmentsResponse.StatusCode == HttpStatusCode.OK,
            $"Installments renewal failed: {await installmentsResponse.Content.ReadAsStringAsync()}");

        var contractA = await ReadContractIdAsync(fullUpfrontResponse);
        var contractB = await ReadContractIdAsync(installmentsResponse);

        await AssertCommercialChainAsync(envA.TenantId, contractA, PaymentTerms.FullUpfront);
        await AssertCommercialChainAsync(envB.TenantId, contractB, PaymentTerms.Installments);

        // Same plan and same bonus months on both sides — only the explicit choice differs.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cA = await db.Contracts.IgnoreQueryFilters().SingleAsync(c => c.Id == contractA);
        var cB = await db.Contracts.IgnoreQueryFilters().SingleAsync(c => c.Id == contractB);

        Assert.Equal(cA.PlanId, cB.PlanId);
        Assert.Equal(cA.BonusMonths, cB.BonusMonths);
        Assert.Equal(3, cA.BonusMonths);
        Assert.NotEqual(cA.PaymentTerms, cB.PaymentTerms);
    }

    [Fact]
    public async Task SamePlanAndPromotion_AcceptsBothPaymentTerms_ChangePlan()
    {
        // One active Percentage promotion bound to the target plan. Two tenants change into the
        // same plan under the same promotion, differing ONLY by the explicit payment decision.
        // This proves PromotionType does not determine PaymentTerms.
        var envFull = await SeedPlatformAsync();
        var envInst = await SeedPlatformAsync();

        var planId = 9302;
        await SeedPlanAsync(planId, bonusMonths: 0);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var promotion = Promotion.Create(
                id: 0,
                name: "Both Arms",
                type: PromotionType.PercentageDiscount,
                planId: planId,
                durationMonths: 0,
                startsAtUtc: DateTime.UtcNow.AddDays(-1),
                endsAtUtc: DateTime.UtcNow.AddDays(30),
                percentage: 10m);
            Assert.True(promotion.IsSuccess);
            // A freshly created promotion is Draft; only an Active one is applicable.
            Assert.True(promotion.Value!.Activate().IsSuccess);
            db.Promotions.Add(promotion.Value);
            await db.SaveChangesAsync();
        }

        var subFull = await SeedPlanAndActiveSubscriptionAsync(envFull.TenantId, planId: 9300, bonusMonths: 0);
        var subInst = await SeedPlanAndActiveSubscriptionAsync(envInst.TenantId, planId: 9303, bonusMonths: 0);

        var fullResponse = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subFull}/change-plan",
            new { newPlanId = planId, paymentTerms = (int)PaymentTerms.FullUpfront },
            envFull);

        var instResponse = await SendAsync(HttpMethod.Post,
            $"/api/TenantPlans/{subInst}/change-plan",
            new { newPlanId = planId, paymentTerms = (int)PaymentTerms.Installments },
            envInst);

        Assert.True(fullResponse.StatusCode == HttpStatusCode.OK,
            $"FullUpfront change-plan failed: {await fullResponse.Content.ReadAsStringAsync()}");
        Assert.True(instResponse.StatusCode == HttpStatusCode.OK,
            $"Installments change-plan failed: {await instResponse.Content.ReadAsStringAsync()}");

        var contractFull = await ReadContractIdAsync(fullResponse);
        var contractInst = await ReadContractIdAsync(instResponse);

        await AssertCommercialChainAsync(envFull.TenantId, contractFull, PaymentTerms.FullUpfront);
        await AssertCommercialChainAsync(envInst.TenantId, contractInst, PaymentTerms.Installments);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var offerFull = await verifyDb.Offers.IgnoreQueryFilters().SingleAsync(o => o.ContractId == contractFull);
        var offerInst = await verifyDb.Offers.IgnoreQueryFilters().SingleAsync(o => o.ContractId == contractInst);

        // Identical commercial promotion applied on both sides.
        Assert.NotNull(offerFull.PromotionId);
        Assert.Equal(offerFull.PromotionId, offerInst.PromotionId);
        Assert.Equal(offerFull.PromotionType, offerInst.PromotionType);
        Assert.Equal(PromotionType.PercentageDiscount.ToString(), offerFull.PromotionType);

        // Yet the payment decision stays independent and explicit.
        Assert.Equal(PaymentTerms.FullUpfront, offerFull.PaymentTerms);
        Assert.Equal(PaymentTerms.Installments, offerInst.PaymentTerms);
    }

    #endregion

    #region Helpers

    private sealed record PlatformEnvironment(string Token, string TenantId);

    private async Task<PlatformEnvironment> SeedPlatformAsync()
    {
        await _factory.SeedPermissionsAsync();

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var store = sp.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        if (!await roleManager.RoleExistsAsync("PlatformAdmin"))
        {
            await roleManager.CreateAsync(new ApplicationRole("PlatformAdmin")
            {
                Code = "PlatformAdmin",
                DisplayName = "Platform Administrator",
                IsSystem = true,
                NormalizedName = "PLATFORMADMIN"
            });
        }

        var tenantId = Guid.NewGuid().ToString();
        var identifier = tenantId;

        var tenant = Tenant.Create(
            Guid.Parse(tenantId), identifier, identifier, identifier,
            "EG", "EGP", "Africa/Cairo", "O", "W",
            $"{identifier}@test.com", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        await store.TryAddAsync(new CenterixTenantInfo
        {
            Id = tenantId,
            Identifier = identifier,
            Name = identifier,
            Email = $"{identifier}@test.com",
            IsActive = true,
            ValidUpTo = DateTime.UtcNow.AddYears(1),
            CreatedAt = DateTime.UtcNow
        });

        var platformUser = new IdentityUser
        {
            Email = $"platform-{identifier}@test.com",
            UserName = $"platform-{identifier}@test.com",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(platformUser, "Platform@test123!");
        await userManager.AddToRoleAsync(platformUser, "PlatformAdmin");

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        var token = _factory.GenerateTestToken(
            platformUser.Id,
            platformUser.Email!,
            new[] { "PlatformAdmin" },
            Permissions.GetPlatformAdminPermissions().ToList());

        return new PlatformEnvironment(token, tenantId);
    }

    private async Task SeedPlanAsync(int planId, int bonusMonths)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Plans.Add(BuildPlan(planId, bonusMonths));
        await db.SaveChangesAsync();
    }

    private static Plan BuildPlan(int planId, int bonusMonths)
    {
        var plan = Plan.Create(
            id: planId,
            code: $"PLAN-{planId}",
            displayName: $"Plan {planId}",
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
            bonusMonths: bonusMonths);
        Assert.True(plan.IsSuccess);
        return plan.Value;
    }

    private async Task<Guid> SeedPlanAndActiveSubscriptionAsync(string tenantId, int planId, int bonusMonths)
    {
        await SeedPlanAsync(planId, bonusMonths);
        return await SeedActiveSubscriptionAsync(tenantId, planId, bonusMonths);
    }

    private async Task<Guid> SeedActiveSubscriptionAsync(string tenantId, int planId, int bonusMonths)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var subscription = TenantPlan.Create(
            Guid.NewGuid(), tenantId, planId, 1000m, 1000m, "EGP",
            12, bonusMonths, now, autoRenew: false, status: SubscriptionStatus.Pending);
        Assert.True(subscription.IsSuccess);
        Assert.True(subscription.Value.Activate(now).IsSuccess);
        db.TenantPlans.Add(subscription.Value);

        await db.SaveChangesAsync();

        return subscription.Value.Id;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object payload, PlatformEnvironment env)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", env.Token);
        request.Headers.Add("tenant", env.TenantId);
        request.Content = JsonContent.Create(payload);
        return await _client.SendAsync(request);
    }

    private static async Task<Guid> ReadContractIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("contractId").GetGuid();
    }

    private async Task<(int Offers, int Contracts)> CountCommercialAsync(string tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var offers = await db.Offers.IgnoreQueryFilters().CountAsync(o => o.TenantId == tenantId);
        var contracts = await db.Contracts.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId);
        return (offers, contracts);
    }

    private async Task AssertCommercialChainAsync(string tenantId, Guid contractId, PaymentTerms expected)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var contract = await db.Contracts.IgnoreQueryFilters().SingleAsync(c => c.Id == contractId);
        Assert.Equal(expected, contract.PaymentTerms);

        var offer = await db.Offers.IgnoreQueryFilters().SingleAsync(o => o.ContractId == contractId);
        Assert.Equal(expected, offer.PaymentTerms);
        Assert.Equal(tenantId, offer.TenantId);
    }

    #endregion
}
