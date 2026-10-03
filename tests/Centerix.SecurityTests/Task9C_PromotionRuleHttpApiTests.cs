namespace Centerix.SecurityTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Centerix.Domain.Platform.Contracts.EligibilityRules;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions.Enums;
// NOTE: Centerix.Infrastructure.Auth is intentionally NOT imported here: it declares a
// static class `Plans` that collides with the Centerix.Domain.Platform.Plans namespace and
// breaks `Plan.Create` resolution. Auth types are fully qualified instead.
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// TASK 9 — canonical promotion rule proven at the REAL HTTP API boundary.
///
/// The production canonical implementation is already covered at the domain, application and SQL
/// layers. This suite covers exactly one missing layer: the actual ASP.NET Core pipeline.
///
/// Every test performs:
/// <code>
/// HttpClient → HTTP endpoint → PromotionsController → MediatR → Handler
///            → BenefitEligibilityRuleParser → Promotion domain → SQL Server persistence
/// </code>
///
/// Endpoints (from the real <see cref="Centerix.API.Controllers.PromotionsController"/>):
/// <list type="bullet">
///   <item><c>POST api/Promotions</c>    — [HasPermission(Promotions.Create)], returns 201 + id</item>
///   <item><c>PUT  api/Promotions/{id}</c> — [HasPermission(Promotions.Update)], returns 204</item>
/// </list>
///
/// Authorization is NOT weakened: the client carries a real signed bearer token whose
/// <c>PlatformAdmin</c> role claim satisfies both the <c>[HasPermission]</c> policy and the
/// handler's <c>IPlatformAdminGuard</c> exactly as production does. No controller, handler or
/// parser is mocked or bypassed.
/// </summary>
[Collection("SqlServerIntegration")]
[Trait("Category", "SqlServer")]
public class Task9C_PromotionRuleHttpApiTests
{
    private const string CreateEndpoint = "/api/promotions";
    private const string InvalidRuleCode = "Promotion.BenefitEligibilityRule_Invalid";

    private readonly SqlServerIntegrationFactory _env;
    private readonly HttpClient _client;
    private string _platformToken = string.Empty;

    public Task9C_PromotionRuleHttpApiTests(SqlServerIntegrationFactory env)
    {
        _env = env;
        _client = env.Client;
    }

    // ─────────────────────────────────────────────────────────────────
    // Seeding / helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a real Identity user holding the PlatformAdmin role and mints a real signed token.
    /// The role claim is what production authorization reads, so no permission bypass is involved.
    /// </summary>
    private async Task<string> EnsurePlatformAdminTokenAsync()
    {
        if (!string.IsNullOrEmpty(_platformToken))
            return _platformToken;

        using var scope = _env.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<Centerix.Infrastructure.Auth.ApplicationRole>>();

        var role = await roleManager.FindByNameAsync("PlatformAdmin");
        if (role is null)
        {
            var created = new Centerix.Infrastructure.Auth.ApplicationRole("PlatformAdmin")
            {
                Code = "PlatformAdmin", DisplayName = "Platform Admin",
                IsSystem = true, NormalizedName = "PLATFORMADMIN"
            };
            await roleManager.CreateAsync(created);
        }

        var email = $"promoapi_{Guid.NewGuid():N}@t9c.test";
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new IdentityUser
            {
                Email = email, UserName = email, EmailConfirmed = true,
                NormalizedEmail = email.ToUpperInvariant(),
                NormalizedUserName = email.ToUpperInvariant()
            };
            user.PasswordHash = new PasswordHasher<IdentityUser>().HashPassword(user, "Str0ng!Pass1");
            await userManager.CreateAsync(user);
            await userManager.AddToRoleAsync(user, "PlatformAdmin");
        }

        _platformToken = _env.Factory.GenerateTestToken(user.Id, email, ["PlatformAdmin"]);
        return _platformToken;
    }

    private async Task<int> SeedPlanAsync(decimal monthlyPrice = 1000m)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var code = "Api9C_" + Guid.NewGuid().ToString("N");
        code = code[..28];

        var created = Plan.Create(
            id: 0,
            code: code,
            displayName: "T9C API Plan",
            monthlyPrice: monthlyPrice,
            maxStudents: 100,
            maxUsers: 5,
            maxBranches: 1,
            maxTeachers: 10,
            storageGB: 10,
            smsQuota: 100,
            isActive: true,
            description: null,
            currencyCode: "EGP",
            durationMonths: 12,
            bonusMonths: 0);

        Assert.True(created.IsSuccess);
        var plan = created.Value;

        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private async Task<int> CountPromotionsAsync(int planId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Promotions.IgnoreQueryFilters().CountAsync(p => p.PlanId == planId);
    }

    /// <summary>Reads the RAW stored rule text straight from the column.</summary>
    private async Task<string?> ReadRawRuleAsync(int promotionId)
    {
        using var scope = _env.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database
            .SqlQuery<string>(
                $"SELECT BenefitEligibilityRule AS Value FROM Platform.Promotions WHERE Id = {promotionId}")
            .SingleOrDefaultAsync();
    }

    private static object CreateBody(int planId, string? ruleJson, string name = "T9C API benefit") => new
    {
        name,
        type = (int)PromotionType.AdditionalBenefits,   // 5
        planId,
        durationMonths = 12,
        startsAtUtc = "2026-01-01T00:00:00Z",
        endsAtUtc = "2030-01-01T00:00:00Z",
        priority = 0,
        benefitName = "Barcode Printer",
        benefitDescription = "Free barcode printer",
        benefitValue = 500m,
        benefitType = (int)ContractBenefitType.PhysicalGift, // 0
        benefitCurrencyCode = "EGP",
        benefitEligibilityRule = ruleJson
    };

    private static object UpdateBody(int planId, int id, string? ruleJson, string name = "T9C API benefit") => new
    {
        id,
        name,
        type = (int)PromotionType.AdditionalBenefits,
        planId,
        durationMonths = 12,
        startsAtUtc = "2026-01-01T00:00:00Z",
        endsAtUtc = "2030-01-01T00:00:00Z",
        priority = 0,
        benefitName = "Barcode Printer",
        benefitDescription = "Free barcode printer",
        benefitValue = 500m,
        benefitType = (int)ContractBenefitType.PhysicalGift,
        benefitCurrencyCode = "EGP",
        benefitEligibilityRule = ruleJson
    };

    private async Task<HttpResponseMessage> PostAsync(object body)
    {
        var token = await EnsurePlatformAdminTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, CreateEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PutAsync(int id, object body)
    {
        var token = await EnsurePlatformAdminTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, $"{CreateEndpoint}/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private static async Task AssertReportsRuleErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(InvalidRuleCode, body, StringComparison.Ordinal);
    }

    private static EligibilityRule CanonicalRule() =>
        EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.AmountPaidAtLeast(4321m));

    // ─────────────────────────────────────────────────────────────────
    // HTTP-C01 — Create rejects a semantically valid but non-canonical rule
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HttpC01_Create_NonCanonicalRule_IsRejectedOverHttp_AndNothingIsPersisted()
    {
        var token = await EnsurePlatformAdminTokenAsync();
        var planId = await SeedPlanAsync();

        // before count = N
        var before = await CountPromotionsAsync(planId);

        // Semantically valid (deserializes to ContractActive) but NOT canonical.
        const string nonCanonical = "{\"type\":\"contract_active\",\"extra\":\"not allowed\"}";
        Assert.Equal(EligibilityRule.ContractActive(), EligibilityRuleSerializer.Deserialize(nonCanonical));

        var response = await PostAsync(CreateBody(planId, nonCanonical));

        // The request reached the real controller and was rejected by the established pipeline.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertReportsRuleErrorAsync(response);

        // after count = N — the database was not modified.
        Assert.Equal(before, await CountPromotionsAsync(planId));
        Assert.Equal(0, before);

        // And the authenticated caller was a genuine platform admin (no bypass was used).
        Assert.False(string.IsNullOrEmpty(token));
    }

    // ─────────────────────────────────────────────────────────────────
    // HTTP-C02 — Create accepts the canonical rule
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HttpC02_Create_CanonicalRule_IsAcceptedOverHttp_AndPersistsCanonicalJson()
    {
        await EnsurePlatformAdminTokenAsync();
        var planId = await SeedPlanAsync();

        var canonical = EligibilityRuleSerializer.Serialize(CanonicalRule());

        var response = await PostAsync(CreateBody(planId, canonical));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var id = JsonSerializer.Deserialize<int>(await response.Content.ReadAsStringAsync());
        Assert.True(id > 0, "The API must return the new promotion id.");

        Assert.Equal(1, await CountPromotionsAsync(planId));

        // Byte-for-byte identical to the canonical string.
        Assert.Equal(canonical, await ReadRawRuleAsync(id));

        // And through the read endpoint it is still exactly canonical.
        var get = new HttpRequestMessage(HttpMethod.Get, $"{CreateEndpoint}/{id}");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await EnsurePlatformAdminTokenAsync());
        var getResponse = await _client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        using var document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal(canonical, document.RootElement.GetProperty("benefitEligibilityRule").GetString());
    }

    // ─────────────────────────────────────────────────────────────────
    // HTTP-C03 — Update rejects a non-canonical rule and leaves the promotion unchanged
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HttpC03_Update_NonCanonicalRule_IsRejectedOverHttp_AndPromotionIsUnchanged()
    {
        await EnsurePlatformAdminTokenAsync();
        var planId = await SeedPlanAsync();

        var originalCanonical = EligibilityRuleSerializer.Serialize(CanonicalRule());

        // Create a valid Promotion through the normal production HTTP flow.
        var createResponse = await PostAsync(CreateBody(planId, originalCanonical));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var id = JsonSerializer.Deserialize<int>(await createResponse.Content.ReadAsStringAsync());
        Assert.Equal(originalCanonical, await ReadRawRuleAsync(id));

        const string nonCanonical = "{\"type\":\"contract_active\",\"extra\":\"not allowed\"}";
        var updateResponse = await PutAsync(id, UpdateBody(planId, id, nonCanonical));

        Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);
        await AssertReportsRuleErrorAsync(updateResponse);

        // The original canonical rule is still persisted, unchanged.
        Assert.Equal(originalCanonical, await ReadRawRuleAsync(id));
    }

    // ─────────────────────────────────────────────────────────────────
    // HTTP-C04 — Update accepts the canonical rule
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HttpC04_Update_CanonicalRule_IsAcceptedOverHttp_AndPersistsCanonicalJson()
    {
        await EnsurePlatformAdminTokenAsync();
        var planId = await SeedPlanAsync();

        var originalCanonical = EligibilityRuleSerializer.Serialize(CanonicalRule());
        var createResponse = await PostAsync(CreateBody(planId, originalCanonical));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var id = JsonSerializer.Deserialize<int>(await createResponse.Content.ReadAsStringAsync());

        var replacement = EligibilityRule.AllOf(
            EligibilityRule.ContractActive(),
            EligibilityRule.PaymentTermsEquals(PaymentTerms.FullUpfront),
            EligibilityRule.NoOverdueInstallment());
        var replacementCanonical = EligibilityRuleSerializer.Serialize(replacement);
        Assert.NotEqual(originalCanonical, replacementCanonical);

        var updateResponse = await PutAsync(id, UpdateBody(planId, id, replacementCanonical));

        // The existing API contract for update is 204 No Content.
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        // The reloaded Promotion contains exactly the canonical serialized rule.
        Assert.Equal(replacementCanonical, await ReadRawRuleAsync(id));
    }
}
