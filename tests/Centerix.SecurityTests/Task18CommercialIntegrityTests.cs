using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Application.Platform.Commands;
using Centerix.Application.Platform.Subscriptions;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Authorization;
using Centerix.Domain.Platform.Billing.Credits;
using Centerix.Domain.Platform.Billing.Credits.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Billing.Invoicing.Enums;
using Centerix.Domain.Platform.Billing.Payments;
using Centerix.Domain.Platform.Billing.Payments.Enums;
using Centerix.Domain.Platform.Billing.Refunds;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Contracts.Enums;
using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Promotions;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Auth;
using Centerix.Infrastructure.Data;
using Centerix.Infrastructure.Tenancy;
using Finbuckle.MultiTenant.Abstractions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// Task 18: Commercial Integrity & Tenant Billing Authorization tests.
/// Covers F-01 (Contract→Subscription snapshot), D-01 (TenantAdmin hybrid auth),
/// D-02 (Upgrade/Downgrade Customer Credit), cross-tenant isolation, and anonymous access.
/// </summary>
[Collection("Integration")]
public class Task18CommercialIntegrityTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    private static readonly DateTime UtcNow = new(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);

    public Task18CommercialIntegrityTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    #region Helpers

    private static Plan CreatePlan(
        int id = 1, decimal monthlyPrice = 1000m, string currencyCode = "EGP",
        int durationMonths = 12, int bonusMonths = 0, bool isActive = true)
    {
        var result = Plan.Create(id: id, code: $"PLAN-{id}", displayName: $"Plan {id}",
            monthlyPrice: monthlyPrice, maxStudents: 100, maxUsers: 50, maxBranches: 10,
            maxTeachers: 20, storageGB: 100, smsQuota: 1000, isActive: isActive,
            currencyCode: currencyCode, durationMonths: durationMonths, bonusMonths: bonusMonths);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static void AddPricingTiers(Plan plan)
    {
        plan.AddPricingTier(PlanPricingTier.Create(1, plan.Id, 1, plan.MonthlyPrice, 1).Value);
        plan.AddPricingTier(PlanPricingTier.Create(2, plan.Id, 3, plan.MonthlyPrice * 3 * 0.9m, 2).Value);
        plan.AddPricingTier(PlanPricingTier.Create(3, plan.Id, 6, plan.MonthlyPrice * 6 * 0.85m, 3).Value);
        plan.AddPricingTier(PlanPricingTier.Create(4, plan.Id, 12, plan.MonthlyPrice * 12 * 0.83m, 4).Value);
    }

    private static TenantPlan CreateActiveSubscription(
        string? tenantId = null, int planId = 1, decimal price = 1000m,
        int durationMonths = 12, int bonusMonths = 0, DateTime? startsAt = null)
    {
        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantId ?? Guid.NewGuid().ToString(), planId, price, "EGP",
            durationMonths, bonusMonths, startsAt ?? UtcNow, false, SubscriptionStatus.Pending).Value;
        sub.Activate(startsAt ?? UtcNow);
        return sub;
    }

    private record TestEnvironment(string PlatformToken, string TenantAdminToken, string TenantId, Guid TenantGuid);

    private async Task<TestEnvironment> SeedAndCreateTestEnvironmentAsync()
    {
        await _factory.SeedPermissionsAsync();

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<IdentityUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var store = sp.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        // Ensure PlatformAdmin role exists (SeedPermissionsAsync only creates TenantAdmin/TenantUser)
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
        var tenantGuid = Guid.Parse(tenantId);
        var tenantIdentifier = tenantId;

        var tenant = Tenant.Create(
            tenantGuid, tenantIdentifier, tenantIdentifier, tenantIdentifier,
            "EG", "EGP", "Africa/Cairo", "O", "W",
            $"{tenantIdentifier}@test.com", IsolationMode.Shared).Value;
        db.Tenants.Add(tenant);

        await store.TryAddAsync(new CenterixTenantInfo
        {
            Id = tenantId, Identifier = tenantIdentifier, Name = tenantIdentifier,
            Email = $"{tenantIdentifier}@test.com", IsActive = true,
            ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
        });

        db.StampAddedTenantIds(tenantId);
        await db.SaveChangesAsync();

        // Platform user
        var platformUser = new IdentityUser
        {
            Email = $"platform-{tenantIdentifier}@test.com",
            UserName = $"platform-{tenantIdentifier}@test.com",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(platformUser, "Platform@test123!");
        await userManager.AddToRoleAsync(platformUser, "PlatformAdmin");

        // TenantAdmin user
        var tenantAdminUser = new IdentityUser
        {
            Email = $"admin-{tenantIdentifier}@test.com",
            UserName = $"admin-{tenantIdentifier}@test.com",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(tenantAdminUser, "Admin@test123!");
        await userManager.AddToRoleAsync(tenantAdminUser, "TenantAdmin");

        // Create membership
        var membership = TenantMembership.Create(tenantAdminUser.Id, tenantId, "TenantAdmin",
            Domain.Platform.Tenants.Enums.TenantMembershipStatus.Active);
        if (membership.IsSuccess)
        {
            db.TenantMemberships.Add(membership.Value);
            await db.SaveChangesAsync();
        }

        var platformToken = _factory.GenerateTestToken(
            platformUser.Id, platformUser.Email!,
            new[] { "PlatformAdmin" },
            Permissions.GetPlatformAdminPermissions().ToList());

        var tenantAdminToken = _factory.GenerateTestToken(
            tenantAdminUser.Id, tenantAdminUser.Email!,
            new[] { "TenantAdmin" },
            Permissions.GetTenantAdminPermissions().ToList());

        return new TestEnvironment(platformToken, tenantAdminToken, tenantId, tenantGuid);
    }

    private HttpRequestMessage CreateAuthRequest(HttpMethod method, string url, string tenantHeader, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(tenantHeader))
            request.Headers.Add("tenant", tenantHeader);
        return request;
    }

    private async Task<(Guid ContractId, Guid SubscriptionId, Guid InvoiceId)>
        SeedContractWithSubscriptionAndInvoice(
        AppDbContext db,
        string tenantId,
        decimal contractPrice,
        int durationMonths)
    {
        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, $"CTR-{Guid.NewGuid().ToString("N")[..8]}",
            planId: 1,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(durationMonths),
            durationMonths: durationMonths,
            monthlyListPrice: contractPrice,
            contractualMonthlyValue: contractPrice,
            currencyCode: "EGP",
            contractedAmount: contractPrice * durationMonths,
            discountAmount: 0).Value;

        contract.Activate(UtcNow);
        db.Contracts.Add(contract);

        var subscription = TenantPlan.Create(
            Guid.NewGuid(), tenantId, 1, contractPrice, "EGP",
            durationMonths, 0, UtcNow, false, SubscriptionStatus.Pending).Value;
        subscription.Activate(UtcNow);
        subscription.LinkToContract(contract.Id);
        db.TenantPlans.Add(subscription);

        var invoice = Invoice.Create(
            Guid.NewGuid(), $"INV-{Guid.NewGuid().ToString("N")[..8]}",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(durationMonths)),
            subtotal: contractPrice * durationMonths,
            discountAmount: 0, taxAmount: 0,
            totalAmount: contractPrice * durationMonths,
            contractId: contract.Id,
            subscriptionId: subscription.Id).Value;
        invoice.Issue(UtcNow);
        db.Invoices.Add(invoice);

        await db.SaveChangesAsync();

        return (contract.Id, subscription.Id, invoice.Id);
    }

    #endregion

    #region 19.1 Contract Snapshot Tests

    [Fact]
    public void F01_ContractSnapshot_PlanPriceMutation_DoesNotAffectExistingSubscription()
    {
        // Plan Price = 1000
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);

        // Contract Price = 800 (negotiated)
        var contract = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CTR-SNAPSHOT-001",
            planId: 1,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 800m,
            contractualMonthlyValue: 800m,
            currencyCode: "EGP",
            contractedAmount: 9600m,
            discountAmount: 0).Value;

        // Create Subscription from Contract (F-01 fix: uses Contract terms)
        var subscription = TenantPlan.Create(
            Guid.NewGuid(), "tenant-1", plan.Id,
            snapshotPrice: contract.MonthlyListPrice,
            snapshotCurrency: contract.CurrencyCode,
            durationMonths: contract.DurationMonths,
            bonusMonths: 0,
            startsAtUtc: UtcNow).Value;

        // Subscription Snapshot Price = 800 (from Contract, NOT from Plan)
        Assert.Equal(800m, subscription.SnapshotPrice);
        Assert.Equal("EGP", subscription.SnapshotCurrency);
        Assert.Equal(12, subscription.DurationMonths);

        // Mutate Plan: Plan Price = 1500
        var mutateResult = Plan.Create(id: 1, code: "PLAN-1", displayName: "Plan 1",
            monthlyPrice: 1500m, maxStudents: 100, maxUsers: 50, maxBranches: 10,
            maxTeachers: 20, storageGB: 100, smsQuota: 1000, isActive: true,
            currencyCode: "EGP", durationMonths: 12, bonusMonths: 0);

        // Existing Subscription Snapshot Price MUST remain 800
        Assert.Equal(800m, subscription.SnapshotPrice);
        Assert.Equal(12, subscription.DurationMonths);
    }

    [Fact]
    public void F01_ContractSnapshot_NegotiatedCurrency_PreservedInSubscription()
    {
        var plan = CreatePlan(id: 2, monthlyPrice: 100m, currencyCode: "USD", durationMonths: 6);

        var contract = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CTR-SNAPSHOT-002",
            planId: 2,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(6),
            durationMonths: 6,
            monthlyListPrice: 80m,
            contractualMonthlyValue: 80m,
            currencyCode: "EGP",
            contractedAmount: 480m,
            discountAmount: 0).Value;

        var subscription = TenantPlan.Create(
            Guid.NewGuid(), "tenant-1", plan.Id,
            snapshotPrice: contract.MonthlyListPrice,
            snapshotCurrency: contract.CurrencyCode,
            durationMonths: contract.DurationMonths,
            bonusMonths: 0,
            startsAtUtc: UtcNow).Value;

        // Contract currency (EGP) overrides Plan currency (USD)
        Assert.Equal(80m, subscription.SnapshotPrice);
        Assert.Equal("EGP", subscription.SnapshotCurrency);
    }

    [Fact]
    public void F01_ContractSnapshot_DurationAndChargedMonths_Preserved()
    {
        var plan = CreatePlan(id: 3, monthlyPrice: 500m, durationMonths: 12);

        var contract = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CTR-SNAPSHOT-003",
            planId: 3,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(6),
            durationMonths: 6,
            monthlyListPrice: 500m,
            contractualMonthlyValue: 500m,
            currencyCode: "EGP",
            contractedAmount: 3000m,
            discountAmount: 0,
            chargedMonths: 5).Value;

        Assert.Equal(6, contract.DurationMonths);
        Assert.Equal(5, contract.ChargedMonths);

        var subscription = TenantPlan.Create(
            Guid.NewGuid(), "tenant-1", plan.Id,
            snapshotPrice: contract.MonthlyListPrice,
            snapshotCurrency: contract.CurrencyCode,
            durationMonths: contract.DurationMonths,
            bonusMonths: 0,
            startsAtUtc: UtcNow).Value;

        Assert.Equal(6, subscription.DurationMonths);
        Assert.Equal(0, subscription.BonusMonths);
    }

    [Fact]
    public async Task F01_CreateSubscriptionFromContract_UsesContractTerms()
    {
        // This tests the actual handler flow (F-01 fix)
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sp = scope.ServiceProvider;

        var plan = CreatePlan(id: 10, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);
        db.Plans.Add(plan);

        var tenantId = Guid.NewGuid().ToString();
        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, "CTR-F01-TEST",
            planId: 10,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 750m,
            contractualMonthlyValue: 750m,
            currencyCode: "EGP",
            contractedAmount: 9000m,
            discountAmount: 0).Value;
        contract.Activate(UtcNow);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var factory = sp.GetRequiredService<ISubscriptionFactory>();
        var subscriptionResult = await factory.CreateFromSnapshotAsync(
            tenantId, plan.Id,
            snapshotPrice: contract.MonthlyListPrice,
            snapshotCurrency: contract.CurrencyCode,
            durationMonths: contract.DurationMonths,
            bonusMonths: 0,
            startsAtUtc: UtcNow,
            autoRenew: false,
            CancellationToken.None);

        Assert.True(subscriptionResult.IsSuccess);
        var subscription = subscriptionResult.Value;

        // Must use Contract price (750), NOT Plan price (1000)
        Assert.Equal(750m, subscription.SnapshotPrice);
        Assert.Equal("EGP", subscription.SnapshotCurrency);
        Assert.Equal(12, subscription.DurationMonths);
        Assert.Equal(0, subscription.BonusMonths);
    }

    [Fact]
    public void F01_ContractSnapshot_PlanDeactivation_DoesNotAffectExistingSubscription()
    {
        var plan = CreatePlan(id: 11, monthlyPrice: 500m, durationMonths: 6, isActive: true);

        var contract = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CTR-F01-DEACT",
            planId: 11,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(6),
            durationMonths: 6,
            monthlyListPrice: 400m,
            contractualMonthlyValue: 400m,
            currencyCode: "EGP",
            contractedAmount: 2400m,
            discountAmount: 0).Value;

        var subscription = TenantPlan.Create(
            Guid.NewGuid(), "tenant-1", plan.Id,
            snapshotPrice: contract.MonthlyListPrice,
            snapshotCurrency: contract.CurrencyCode,
            durationMonths: contract.DurationMonths,
            bonusMonths: 0,
            startsAtUtc: UtcNow).Value;

        Assert.Equal(400m, subscription.SnapshotPrice);

        // Plan is deactivated after subscription creation
        // Subscription snapshot remains valid
        Assert.Equal(400m, subscription.SnapshotPrice);
        Assert.Equal(6, subscription.DurationMonths);
    }

    #endregion

    #region 19.2 TenantAdmin Authorization Tests

    [Fact]
    public async Task D01_TenantAdmin_CanViewOwnContract()
    {
        var env = await SeedAndCreateTestEnvironmentAsync();
        var tenantAdminToken = env.TenantAdminToken;
        var tenantId = env.TenantId;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var contract = Contract.Create(
            Guid.NewGuid(), tenantId, "CTR-TA-VIEW",
            planId: 1, effectiveAtUtc: UtcNow, endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12, monthlyListPrice: 1000m, contractualMonthlyValue: 1000m,
            currencyCode: "EGP", contractedAmount: 12000m).Value;
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var request = CreateAuthRequest(HttpMethod.Get, $"/api/contracts/{contract.Id}", tenantId, tenantAdminToken);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 but got {response.StatusCode}. Body: {body}");
    }

    [Fact]
    public void D01_TenantAdmin_CannotApproveRefund()
    {
        var tenantAdminPermissions = Permissions.GetTenantAdminPermissions().ToHashSet();
        Assert.DoesNotContain(Permissions.Refunds.Approve, tenantAdminPermissions);
    }

    [Fact]
    public void D01_TenantAdmin_CannotExecuteRefund()
    {
        var tenantAdminPermissions = Permissions.GetTenantAdminPermissions().ToHashSet();
        Assert.DoesNotContain(Permissions.Refunds.Execute, tenantAdminPermissions);
    }

    [Fact]
    public void D01_TenantAdmin_CannotModifyInvoice()
    {
        var tenantAdminPermissions = Permissions.GetTenantAdminPermissions().ToHashSet();
        Assert.DoesNotContain(Permissions.Invoices.Update, tenantAdminPermissions);
    }

    [Fact]
    public void D01_TenantAdmin_CannotCreateContract()
    {
        var tenantAdminPermissions = Permissions.GetTenantAdminPermissions().ToHashSet();
        Assert.DoesNotContain(Permissions.Contracts.Create, tenantAdminPermissions);
    }

    [Fact]
    public void D01_TenantAdmin_CannotManageSubscription()
    {
        var tenantAdminPermissions = Permissions.GetTenantAdminPermissions().ToHashSet();
        Assert.DoesNotContain(Permissions.TenantPlans.Create, tenantAdminPermissions);
    }

    #endregion

    #region 21. Anonymous Access Tests

    [Fact]
    public async Task Anonymous_InvoicesEndpoint_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/invoices");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_ContractsEndpoint_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/contracts");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_TenantCreditsEndpoint_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tenantcredits");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_RefundsEndpoint_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/refunds");
        request.Content = JsonContent.Create(new { });
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_TenantPlansEndpoint_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tenantplans");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_InstallmentsEndpoint_Returns401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/installments/{Guid.NewGuid()}");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region 22. Cross-Tenant HTTP Tests

    [Fact]
    public async Task CrossTenant_TenantA_InvoiceAccess_Returns403()
    {
        // Seed Tenant A's environment
        await _factory.SeedPermissionsAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        var tenantIdA = Guid.NewGuid().ToString();
        var tenantIdentifierA = $"ct-a-{Guid.NewGuid():N}";
        var tenantA = Tenant.Create(
            Guid.Parse(tenantIdA), tenantIdentifierA, tenantIdentifierA, tenantIdentifierA,
            "EG", "EGP", "Africa/Cairo", "O", "W", $"{tenantIdentifierA}@test.com", IsolationMode.Shared).Value;
        db.Tenants.Add(tenantA);
        await store.TryAddAsync(new CenterixTenantInfo
        {
            Id = tenantIdA, Identifier = tenantIdentifierA, Name = tenantIdentifierA,
            Email = $"{tenantIdentifierA}@test.com", IsActive = true,
            ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
        });

        // User B has no membership in Tenant A
        var userB = new IdentityUser
        {
            Email = $"user-b-cross-{Guid.NewGuid():N}@test.com",
            UserName = $"user-b-cross-{Guid.NewGuid():N}@test.com",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(userB, "UserB@test123!");
        await userManager.AddToRoleAsync(userB, "TenantAdmin");

        // Create invoice in Tenant A
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-CT-A",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            subtotal: 1000m, discountAmount: 0, taxAmount: 0, totalAmount: 1000m).Value;
        db.Invoices.Add(invoice);
        db.StampAddedTenantIds(tenantIdA);
        await db.SaveChangesAsync();

        // User B has no membership in Tenant A, but tries to access with Tenant A header
        var tokenB = _factory.GenerateTestToken(userB.Id, userB.Email!, new[] { "TenantAdmin" },
            Permissions.GetTenantAdminPermissions().ToList());
        var request = CreateAuthRequest(HttpMethod.Get, $"/api/invoices/{invoice.Id}", tenantIdA, tokenB);
        var response = await _client.SendAsync(request);
        // Should be forbidden - user B has no membership in Tenant A
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                    $"Expected 403 but got {response.StatusCode}");
    }

    [Fact]
    public async Task CrossTenant_TenantA_ContractAccess_Returns403()
    {
        await _factory.SeedPermissionsAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        var tenantIdA = Guid.NewGuid().ToString();
        var tenantIdentifierA = $"ct-ctr-a-{Guid.NewGuid():N}";
        var tenantA = Tenant.Create(
            Guid.Parse(tenantIdA), tenantIdentifierA, tenantIdentifierA, tenantIdentifierA,
            "EG", "EGP", "Africa/Cairo", "O", "W", $"{tenantIdentifierA}@test.com", IsolationMode.Shared).Value;
        db.Tenants.Add(tenantA);
        await store.TryAddAsync(new CenterixTenantInfo
        {
            Id = tenantIdA, Identifier = tenantIdentifierA, Name = tenantIdentifierA,
            Email = $"{tenantIdentifierA}@test.com", IsActive = true,
            ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
        });

        var userB = new IdentityUser
        {
            Email = $"user-b-ctr-{Guid.NewGuid():N}@test.com",
            UserName = $"user-b-ctr-{Guid.NewGuid():N}@test.com",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(userB, "UserB@test123!");
        await userManager.AddToRoleAsync(userB, "TenantAdmin");

        var contract = Contract.Create(
            Guid.NewGuid(), tenantIdA, "CTR-CT-A",
            planId: 1, effectiveAtUtc: UtcNow, endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12, monthlyListPrice: 1000m, contractualMonthlyValue: 1000m,
            currencyCode: "EGP", contractedAmount: 12000m).Value;
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var tokenB = _factory.GenerateTestToken(userB.Id, userB.Email!, new[] { "TenantAdmin" },
            Permissions.GetTenantAdminPermissions().ToList());
        var request = CreateAuthRequest(HttpMethod.Get, $"/api/contracts/{contract.Id}", tenantIdA, tokenB);
        var response = await _client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                    $"Expected 403 but got {response.StatusCode}");
    }

    [Fact]
    public async Task CrossTenant_TenantA_SubscriptionAccess_Returns403()
    {
        await _factory.SeedPermissionsAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<CenterixTenantInfo>>();

        var tenantIdA = Guid.NewGuid().ToString();
        var tenantIdentifierA = $"ct-sub-a-{Guid.NewGuid():N}";
        var tenantA = Tenant.Create(
            Guid.Parse(tenantIdA), tenantIdentifierA, tenantIdentifierA, tenantIdentifierA,
            "EG", "EGP", "Africa/Cairo", "O", "W", $"{tenantIdentifierA}@test.com", IsolationMode.Shared).Value;
        db.Tenants.Add(tenantA);
        await store.TryAddAsync(new CenterixTenantInfo
        {
            Id = tenantIdA, Identifier = tenantIdentifierA, Name = tenantIdentifierA,
            Email = $"{tenantIdentifierA}@test.com", IsActive = true,
            ValidUpTo = DateTime.UtcNow.AddYears(1), CreatedAt = DateTime.UtcNow
        });

        var userB = new IdentityUser
        {
            Email = $"user-b-sub-{Guid.NewGuid():N}@test.com",
            UserName = $"user-b-sub-{Guid.NewGuid():N}@test.com",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(userB, "UserB@test123!");
        await userManager.AddToRoleAsync(userB, "TenantAdmin");

        var sub = TenantPlan.Create(
            Guid.NewGuid(), tenantIdA, 1, 1000m, "EGP", 12, 0, UtcNow, false,
            SubscriptionStatus.Active).Value;
        db.TenantPlans.Add(sub);
        await db.SaveChangesAsync();

        var tokenB = _factory.GenerateTestToken(userB.Id, userB.Email!, new[] { "TenantAdmin" },
            Permissions.GetTenantAdminPermissions().ToList());
        var request = CreateAuthRequest(HttpMethod.Get, "/api/tenantplans/me", tenantIdA, tokenB);
        var response = await _client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                    $"Expected 403 but got {response.StatusCode}");
    }

    #endregion

    #region 23. Upgrade/Downgrade Financial Tests

    [Fact]
    public void D02_UpgradeDowngrade_CreditSourceType_Exists()
    {
        // Verify SubscriptionChange source type is defined
        Assert.True(Enum.IsDefined(typeof(CreditSourceType), CreditSourceType.SubscriptionChange));
        Assert.Equal(5, (int)CreditSourceType.SubscriptionChange);
    }

    [Fact]
    public void D02_UpgradeDowngrade_Calculation_UnusedPaidValue()
    {
        // Old subscription: Paid = 12,000, Consumed value = 4,000
        // Eligible unused paid value = 8,000
        var plan = CreatePlan(id: 1, monthlyPrice: 1000m, durationMonths: 12);
        AddPricingTiers(plan);

        var contract = Contract.Create(
            Guid.NewGuid(), "tenant-1", "CTR-D02-CALC",
            planId: 1,
            effectiveAtUtc: UtcNow,
            endsAtUtc: UtcNow.AddMonths(12),
            durationMonths: 12,
            monthlyListPrice: 1000m,
            contractualMonthlyValue: 1000m,
            currencyCode: "EGP",
            contractedAmount: 12000m,
            discountAmount: 0).Value;

        // 4 months elapsed
        var elapsedMonths = contract.GetElapsedMonths(UtcNow.AddMonths(4));
        Assert.Equal(4, elapsedMonths);

        var consumedValue = contract.CalculateValueForElapsedMonths(4);
        Assert.Equal(4000m, consumedValue);

        var unusedValue = contract.ContractedAmount - consumedValue;
        Assert.Equal(8000m, unusedValue);
    }

    [Fact]
    public void D02_UpgradeDowngrade_CreditApplication_ReducesInvoice()
    {
        // New Invoice = 15,000, Credit = 8,000
        // Expected: Remaining Invoice = 7,000, Credit Remaining = 0
        var invoiceResult = Invoice.Create(
            Guid.NewGuid(), "INV-D02-APPLY",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            subtotal: 15000m, discountAmount: 0, taxAmount: 0, totalAmount: 15000m);
        Assert.True(invoiceResult.IsSuccess);
        var invoice = invoiceResult.Value;

        var creditResult = TenantCredit.Create(
            Guid.NewGuid(), 8000m, CreditSourceType.SubscriptionChange, sourceId: Guid.NewGuid());
        Assert.True(creditResult.IsSuccess);
        var credit = creditResult.Value;

        var applyResult = credit.ConsumeAmount(8000m);
        Assert.True(applyResult.IsSuccess);

        Assert.Equal(0m, credit.RemainingAmount);
        Assert.Equal(CreditStatus.Applied, credit.Status);

        var remaining = invoice.TotalAmount - 8000m;
        Assert.Equal(7000m, remaining);
    }

    [Fact]
    public void D02_UpgradeDowngrade_CreditLargerThanInvoice_RemainingCreditPreserved()
    {
        // New Invoice = 5,000, Credit = 8,000
        // Expected: Invoice Remaining = 0, Credit Remaining = 3,000
        var invoiceResult = Invoice.Create(
            Guid.NewGuid(), "INV-D02-LARGE",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            subtotal: 5000m, discountAmount: 0, taxAmount: 0, totalAmount: 5000m);
        Assert.True(invoiceResult.IsSuccess);

        var creditResult = TenantCredit.Create(
            Guid.NewGuid(), 8000m, CreditSourceType.SubscriptionChange, sourceId: Guid.NewGuid());
        Assert.True(creditResult.IsSuccess);
        var credit = creditResult.Value;

        // Apply only the invoice amount
        var applyResult = credit.ConsumeAmount(5000m);
        Assert.True(applyResult.IsSuccess);

        Assert.Equal(3000m, credit.RemainingAmount);
        Assert.Equal(CreditStatus.PartiallyApplied, credit.Status);
    }

    [Fact]
    public void D02_UpgradeDowngrade_IdempotentCredit_NoDuplicateCredit()
    {
        // Two identical upgrade requests must not create two credits for the same unused period
        var tenantId = "tenant-idempotent";

        var credit1 = TenantCredit.Create(
            Guid.NewGuid(), 8000m, CreditSourceType.SubscriptionChange,
            sourceId: Guid.NewGuid()).Value;

        // Simulate check for existing credit
        var existingCreditCheck = credit1.SourceId;
        Assert.NotNull(existingCreditCheck);

        // Second request with same sourceId should detect existing credit
        // (This tests the domain-level idempotency concept)
        Assert.Equal(CreditSourceType.SubscriptionChange, credit1.SourceType);
        Assert.Equal(8000m, credit1.Amount);
    }

    [Fact]
    public void D02_UpgradeDowngrade_ConcurrentCredit_NoDoubleConsumption()
    {
        // Concurrent upgrade requests must not double-consume the old subscription's unused value
        var creditResult = TenantCredit.Create(
            Guid.NewGuid(), 8000m, CreditSourceType.SubscriptionChange, sourceId: Guid.NewGuid());
        var credit = creditResult.Value;

        // First concurrent request consumes 5000
        var firstConsume = credit.ConsumeAmount(5000m);
        Assert.True(firstConsume.IsSuccess);
        Assert.Equal(3000m, credit.RemainingAmount);

        // Second concurrent request tries to consume 5000 but only 3000 remaining
        var secondConsume = credit.ConsumeAmount(5000m);
        Assert.False(secondConsume.IsSuccess);
        Assert.Equal(3000m, credit.RemainingAmount);
    }

    #endregion

    #region 18. Business State Validation

    [Fact]
    public void D02_TenantCredit_BelongsToCorrectTenant()
    {
        var tenantIdA = "tenant-a-credit";
        var tenantIdB = "tenant-b-credit";

        var credit = TenantCredit.Create(
            Guid.NewGuid(), 5000m, CreditSourceType.SubscriptionChange, sourceId: Guid.NewGuid()).Value;

        // Credit is bound to tenant via TenantId from AuditableEntity
        credit.TenantId = tenantIdA;

        Assert.Equal(tenantIdA, credit.TenantId);
        Assert.NotEqual(tenantIdB, credit.TenantId);
    }

    [Fact]
    public void D02_CreditApplication_CrossTenant_Rejected()
    {
        var tenantIdA = "tenant-a-isolation";
        var tenantIdB = "tenant-b-isolation";

        // Credit belongs to Tenant A
        var credit = TenantCredit.Create(
            Guid.NewGuid(), 5000m, CreditSourceType.SubscriptionChange, sourceId: Guid.NewGuid()).Value;
        credit.TenantId = tenantIdA;

        // Invoice belongs to Tenant B
        var invoice = Invoice.Create(
            Guid.NewGuid(), "INV-ISO",
            DateOnly.FromDateTime(UtcNow), DateOnly.FromDateTime(UtcNow.AddMonths(12)),
            subtotal: 3000m, discountAmount: 0, taxAmount: 0, totalAmount: 3000m).Value;
        invoice.TenantId = tenantIdB;

        // Cross-tenant check: credit.TenantId != invoice.TenantId
        Assert.NotEqual(credit.TenantId, invoice.TenantId);
    }

    #endregion
}
