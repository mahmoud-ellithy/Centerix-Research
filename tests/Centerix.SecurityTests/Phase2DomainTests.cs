namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Plans;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Domain.Platform.Subscriptions.Enums;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Xunit;

/// <summary>
/// Phase 2 domain rules: tenant lifecycle state machine, subscription calendar-month math,
/// bonus handling, renewal anchoring, lazy expiration and entitlement snapshots.
/// </summary>
public class Phase2DomainTests
{
    private static Tenant NewTenant() => Tenant.Create(
        Guid.NewGuid(), "slug", "sub", "Display", "EG", "EGP", "Africa/Cairo",
        "First", "Last", "owner@test.com", IsolationMode.Shared).Value;

    private static Plan NewPlan(
        int durationMonths = 12,
        int bonusMonths = 1,
        string currency = "USD",
        decimal price = 100m) => Plan.Create(
        0, $"P{Guid.NewGuid():N}", "Plan", price, 500, 25, 10, 40, 50, 1000,
        isActive: true, description: "Test plan",
        currencyCode: currency, durationMonths, bonusMonths).Value;

    private static TenantPlan NewSubscription(
        Plan? plan = null,
        DateTime? startsAt = null,
        SubscriptionStatus status = SubscriptionStatus.Pending)
    {
        plan ??= NewPlan();
        var sub = TenantPlan.Create(
            Guid.NewGuid(), Guid.NewGuid().ToString(), plan.Id == 0 ? 1 : plan.Id,
            plan.MonthlyPrice, plan.CurrencyCode, plan.DurationMonths, plan.BonusMonths,
            startsAt ?? new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            autoRenew: false, status,
            plan.MaxStudents, plan.MaxUsers, plan.MaxBranches, plan.MaxTeachers,
            plan.StorageGB, plan.SMSQuota).Value;
        return sub;
    }

    // ------------------------------------------------------------------
    // Tenant lifecycle
    // ------------------------------------------------------------------

    [Fact]
    public void Tenant_Created_StartsPendingApproval_Inactive()
    {
        var t = NewTenant();
        Assert.Equal(LifecycleStatus.PendingApproval, t.LifecycleStatus);
        Assert.False(t.IsActive);
    }

    [Fact]
    public void Tenant_Approve_FromPendingApproval_GoesProvisioning_StillInactive()
    {
        var t = NewTenant();
        Assert.True(t.Approve().IsSuccess);
        Assert.Equal(LifecycleStatus.Provisioning, t.LifecycleStatus);
        Assert.False(t.IsActive);
    }

    [Fact]
    public void Tenant_Reject_FromPendingApproval_GoesRejected()
    {
        var t = NewTenant();
        var result = t.Reject("incomplete documents");
        Assert.True(result.IsSuccess);
        Assert.Equal(LifecycleStatus.Rejected, t.LifecycleStatus);
        Assert.False(t.IsActive);
        Assert.Equal("incomplete documents", t.SuspendedReason);
    }

    [Fact]
    public void Tenant_Reject_RequiresReason()
    {
        var t = NewTenant();
        Assert.False(t.Reject("  ").IsSuccess);
    }

    [Fact]
    public void Tenant_Activate_FromProvisioning_Succeeds()
    {
        var t = NewTenant();
        t.Approve();
        Assert.True(t.Activate().IsSuccess);
        Assert.Equal(LifecycleStatus.Active, t.LifecycleStatus);
        Assert.True(t.IsActive);
    }

    [Fact]
    public void Tenant_Activate_FromPendingApproval_IsDenied_CommercialGate()
    {
        var t = NewTenant();
        Assert.False(t.Activate().IsSuccess);
    }

    [Fact]
    public void Tenant_Activate_FromRejected_IsDenied()
    {
        var t = NewTenant();
        t.Reject("no");
        Assert.False(t.Activate().IsSuccess);
    }

    [Fact]
    public void Tenant_Suspend_FromActive_Succeeds_FromPendingApproval_Denied()
    {
        var pending = NewTenant();
        Assert.False(pending.Suspend("reason").IsSuccess);

        var active = NewTenant();
        active.Approve();
        active.Activate();
        Assert.True(active.Suspend("policy violation").IsSuccess);
        Assert.Equal(LifecycleStatus.Suspended, active.LifecycleStatus);
    }

    // ------------------------------------------------------------------
    // Calendar-month math (never 30-day approximations)
    // ------------------------------------------------------------------

    [Fact]
    public void Subscription_Jan31_Plus_OneMonth_ClampsToFeb28()
    {
        var start = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        var oneMonthPlusBonus = NewPlan(durationMonths: 1, bonusMonths: 1);
        var sub = NewSubscription(plan: oneMonthPlusBonus, startsAt: start);

        Assert.Equal(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc), sub.BaseEndsAtUtc);
        // Bonus of 1 month applies from the CLAMPED base (Feb 28 + 1 = Mar 28).
        Assert.Equal(new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc), sub.EffectiveEndsAtUtc);
    }

    [Fact]
    public void Subscription_TwelveMonths_PreservesAnniversary()
    {
        var start = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var sub = TenantPlan.Create(
            Guid.NewGuid(), "t1", 1, 100m, "USD", 12, 0, start).Value;

        Assert.Equal(new DateTime(2027, 3, 15, 0, 0, 0, DateTimeKind.Utc), sub.BaseEndsAtUtc);
        Assert.Equal(sub.BaseEndsAtUtc, sub.EffectiveEndsAtUtc); // no bonus
    }

    [Fact]
    public void Subscription_BonusMonths_AreStored_AndExtendEffectiveEnd()
    {
        var start = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        var sub = TenantPlan.Create(Guid.NewGuid(), "t1", 1, 50m, "EUR", 6, 2, start).Value;

        Assert.Equal(6, sub.DurationMonths);
        Assert.Equal(2, sub.BonusMonths);
        Assert.Equal(new DateTime(2026, 12, 30, 0, 0, 0, DateTimeKind.Utc), sub.BaseEndsAtUtc);
        Assert.Equal(new DateTime(2027, 2, 28, 0, 0, 0, DateTimeKind.Utc), sub.EffectiveEndsAtUtc);
    }

    [Fact]
    public void Subscription_Create_RejectsInvalidCommercialTerms()
    {
        Assert.False(TenantPlan.Create(Guid.NewGuid(), "", 1, 10m, "USD", 1, 0, DateTime.UtcNow).IsSuccess);
        Assert.False(TenantPlan.Create(Guid.NewGuid(), "t", 0, 10m, "USD", 1, 0, DateTime.UtcNow).IsSuccess);
        Assert.False(TenantPlan.Create(Guid.NewGuid(), "t", 1, -1m, "USD", 1, 0, DateTime.UtcNow).IsSuccess);
        Assert.False(TenantPlan.Create(Guid.NewGuid(), "t", 1, 10m, "USDD", 1, 0, DateTime.UtcNow).IsSuccess);
        Assert.False(TenantPlan.Create(Guid.NewGuid(), "t", 1, 10m, "USD", 0, 0, DateTime.UtcNow).IsSuccess);
        Assert.False(TenantPlan.Create(Guid.NewGuid(), "t", 1, 10m, "USD", 1, -1, DateTime.UtcNow).IsSuccess);
        Assert.False(TenantPlan.Create(Guid.NewGuid(), "t", 1, 10m, "USD", 1, 0, default).IsSuccess);
    }

    // ------------------------------------------------------------------
    // Lifecycle transitions on the subscription
    // ------------------------------------------------------------------

    [Fact]
    public void Subscription_Activate_FromPending_Succeeds_WhenNotExpired()
    {
        var sub = NewSubscription(status: SubscriptionStatus.Pending);
        var result = sub.Activate(DateTime.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.NotNull(sub.ActivatedAtUtc);
    }

    [Fact]
    public void Subscription_Activate_AlreadyExpiredTerm_IsDenied()
    {
        var past = DateTime.UtcNow.AddMonths(-6);
        var sub = TenantPlan.Create(Guid.NewGuid(), "t", 1, 10m, "USD", 1, 0, past).Value; // ended ~5 months ago
        Assert.False(sub.Activate(DateTime.UtcNow).IsSuccess);
    }

    [Fact]
    public void Subscription_Renew_BeforeExpiry_AnchorsAtEffectiveEnd_PreservingPaidTime()
    {
        var start = DateTime.UtcNow.AddDays(-10);
        var sub = TenantPlan.Create(Guid.NewGuid(), "t", 1, 10m, "USD", 12, 1, start).Value;
        sub.Activate(DateTime.UtcNow);

        var effectiveBefore = sub.EffectiveEndsAtUtc;
        Assert.True(sub.Renew(additionalMonths: 3, additionalBonusMonths: 0, DateTime.UtcNow).IsSuccess);

        Assert.Equal(15, sub.DurationMonths);   // 12 + 3
        Assert.Equal(1, sub.BonusMonths);       // unchanged
        // New effective end = old effective end + 3 calendar months (remaining paid time kept).
        Assert.Equal(effectiveBefore.AddMonths(3), sub.EffectiveEndsAtUtc);
        Assert.True(sub.EffectiveEndsAtUtc > effectiveBefore);
    }

    [Fact]
    public void Subscription_Renew_AfterExpiry_StartsFreshFromNow()
    {
        var longAgo = DateTime.UtcNow.AddYears(-2);
        var sub = TenantPlan.Create(Guid.NewGuid(), "t", 1, 10m, "USD", 1, 0, longAgo).Value;
        sub.Activate(longAgo.AddDays(1));

        var now = DateTime.UtcNow;
        Assert.True(sub.Renew(6, 0, now).IsSuccess);
        Assert.Equal(now.AddMonths(6), sub.EffectiveEndsAtUtc);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public void Subscription_Renew_Cancelled_IsDenied()
    {
        var sub = NewSubscription(startsAt: DateTime.UtcNow, status: SubscriptionStatus.Pending);
        sub.Activate(DateTime.UtcNow);
        Assert.True(sub.Cancel(DateTime.UtcNow).IsSuccess);
        Assert.False(sub.Renew(1, 0, DateTime.UtcNow).IsSuccess);
    }

    [Fact]
    public void Subscription_MarkExpired_OnlyAfterEffectiveEnd()
    {
        var start = DateTime.UtcNow.AddMonths(-2);
        var sub = TenantPlan.Create(Guid.NewGuid(), "t", 1, 10m, "USD", 3, 1, start).Value;
        sub.Activate(start);

        Assert.False(sub.MarkExpired(DateTime.UtcNow).IsSuccess); // still within effective term

        var after = sub.EffectiveEndsAtUtc.AddSeconds(1);
        Assert.True(sub.MarkExpired(after).IsSuccess);
        Assert.Equal(SubscriptionStatus.Expired, sub.Status);
        Assert.False(sub.IsActiveAsOf(after));
    }

    [Fact]
    public void Subscription_SuspendBlocksAccess_ReactivateRestores()
    {
        var sub = NewSubscription(startsAt: DateTime.UtcNow, status: SubscriptionStatus.Pending);
        sub.Activate(DateTime.UtcNow);
        Assert.True(sub.IsActiveAsOf(DateTime.UtcNow));

        sub.Suspend();
        Assert.False(sub.IsActiveAsOf(DateTime.UtcNow)); // suspended blocks even before expiry

        sub.Reactivate(DateTime.UtcNow);
        Assert.True(sub.IsActiveAsOf(DateTime.UtcNow));
    }

    [Fact]
    public void Subscription_Cancel_PendingAllowed_ExpiredDenied()
    {
        var pending = NewSubscription(status: SubscriptionStatus.Pending);
        Assert.True(pending.Cancel(DateTime.UtcNow).IsSuccess);

        // A row persisted as Active whose effective end already passed cannot be cancelled
        // (it is commercially dead; renewal is the only forward path).
        var expired = TenantPlan.Create(
            Guid.NewGuid(), "t", 1, 10m, "USD", 1, 0,
            DateTime.UtcNow.AddMonths(-2), false, SubscriptionStatus.Active).Value;
        Assert.False(expired.Cancel(DateTime.UtcNow).IsSuccess);
    }

    // ------------------------------------------------------------------
    // Entitlement + limit snapshots
    // ------------------------------------------------------------------

    [Fact]
    public void Subscription_GrantFeature_Snapshot_DuplicatesRejected_CaseInsensitive()
    {
        var sub = NewSubscription();
        Assert.True(sub.GrantFeature("Students").IsSuccess);
        Assert.False(sub.GrantFeature("students").IsSuccess); // same code, different case
        Assert.Single(sub.Features);
        Assert.Equal("Students", sub.Features[0].FeatureCode);
    }

    [Fact]
    public void Subscription_LimitSnapshot_ResolvesKnownTypes_UnknownNull()
    {
        var plan = Plan.Create(0, "X" + Guid.NewGuid().ToString("N")[..8], "P", 10m, 111, 222, 333, 444, 55, 66,
            true, null, "SAR", 3, 4).Value;
        var subResult = TenantPlan.Create(Guid.NewGuid(), "t", 1, plan.MonthlyPrice, "SAR", plan.DurationMonths, plan.BonusMonths,
            DateTime.UtcNow, false, SubscriptionStatus.Pending,
            plan.MaxStudents, plan.MaxUsers, plan.MaxBranches, plan.MaxTeachers,
            plan.StorageGB, plan.SMSQuota);
        Assert.True(subResult.IsSuccess, string.Join(",", subResult.Errors?.Select(e => e.Code) ?? []));
        var sub = subResult.Value;

        Assert.Equal(111, sub.GetSnapshotLimit(LimitTypeCodes.Students));
        Assert.Equal(222, sub.GetSnapshotLimit(LimitTypeCodes.Users));
        Assert.Equal(333, sub.GetSnapshotLimit(LimitTypeCodes.Branches));
        Assert.Equal(444, sub.GetSnapshotLimit(LimitTypeCodes.Teachers));
        Assert.Null(sub.GetSnapshotLimit("StorageGB")); // not a counter-enforced limit type
    }

    // ------------------------------------------------------------------
    // Plan validation
    // ------------------------------------------------------------------

    [Fact]
    public void Plan_Create_ValidatesCurrencyDurationBonus()
    {
        Assert.False(Plan.Create(0, "c", "n", 10m, 1, 1, 1, 1, 1, 1, true, null, "US", 1, 0).IsSuccess);
        Assert.False(Plan.Create(0, "c", "n", 10m, 1, 1, 1, 1, 1, 1, true, null, "USD", 0, 0).IsSuccess);
        Assert.False(Plan.Create(0, "c", "n", 10m, 1, 1, 1, 1, 1, 1, true, null, "USD", 1, -1).IsSuccess);

        var ok = Plan.Create(0, "c", "n", 10m, 1, 1, 1, 1, 1, 1, true, "desc", "eur", 3, 2).Value;
        Assert.Equal("EUR", ok.CurrencyCode); // normalized upper-case
        Assert.Equal(3, ok.DurationMonths);
        Assert.Equal(2, ok.BonusMonths);
    }
}
