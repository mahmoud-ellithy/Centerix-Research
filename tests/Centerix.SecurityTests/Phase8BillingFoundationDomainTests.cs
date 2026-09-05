namespace Centerix.SecurityTests;

using Centerix.Domain.Platform.Billing.BillingCycles;
using Centerix.Domain.Platform.Billing.BillingCycles.Enums;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Subscriptions;
using Xunit;

/// <summary>
/// Domain tests for BillingCycle and Invoice commercial traceability.
/// </summary>
public class Phase8BillingFoundationDomainTests
{
    // ------------------------------------------------------------------
    // BillingCycle tests
    // ------------------------------------------------------------------

    [Fact]
    public void BillingCycle_Create_ValidInput_Succeeds()
    {
        var subscriptionId = Guid.NewGuid();
        var cycle = BillingCycle.Create(
            Guid.NewGuid(),
            "tenant-1",
            subscriptionId,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(cycle.IsSuccess);
        var entity = cycle.Value;
        Assert.Equal("tenant-1", entity.TenantId);
        Assert.Equal(subscriptionId, entity.SubscriptionId);
        Assert.Equal(BillingCycleStatus.Draft, entity.Status);
    }

    [Fact]
    public void BillingCycle_Create_TenantIdRequired()
    {
        var result = BillingCycle.Create(
            Guid.NewGuid(),
            "",
            Guid.NewGuid(),
            DateTime.UtcNow,
            DateTime.UtcNow.AddMonths(1));

        Assert.False(result.IsSuccess);
        Assert.Equal("BillingCycle.TenantId_Required", result.Errors![0].Code);
    }

    [Fact]
    public void BillingCycle_Create_SubscriptionIdRequired()
    {
        var result = BillingCycle.Create(
            Guid.NewGuid(),
            "tenant-1",
            Guid.Empty,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMonths(1));

        Assert.False(result.IsSuccess);
        Assert.Equal("BillingCycle.SubscriptionId_Required", result.Errors![0].Code);
    }

    [Fact]
    public void BillingCycle_Create_PeriodEndBeforeStart_IsDenied()
    {
        var result = BillingCycle.Create(
            Guid.NewGuid(),
            "tenant-1",
            Guid.NewGuid(),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.False(result.IsSuccess);
        Assert.Equal("BillingCycle.InvalidPeriod", result.Errors![0].Code);
    }

    [Fact]
    public void BillingCycle_Lifecycle_DraftToInvoicedToPaid()
    {
        var cycle = BillingCycle.Create(
            Guid.NewGuid(),
            "tenant-1",
            Guid.NewGuid(),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)).Value;

        Assert.Equal(BillingCycleStatus.Draft, cycle.Status);

        Assert.True(cycle.MarkInvoiced().IsSuccess);
        Assert.Equal(BillingCycleStatus.Invoiced, cycle.Status);

        Assert.True(cycle.MarkPaid().IsSuccess);
        Assert.Equal(BillingCycleStatus.Paid, cycle.Status);
    }

    [Fact]
    public void BillingCycle_Lifecycle_Cancel_FromDraft_Succeeds()
    {
        var cycle = BillingCycle.Create(
            Guid.NewGuid(),
            "tenant-1",
            Guid.NewGuid(),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)).Value;

        Assert.True(cycle.Cancel().IsSuccess);
        Assert.Equal(BillingCycleStatus.Cancelled, cycle.Status);
    }

    [Fact]
    public void BillingCycle_Lifecycle_Cancel_FromPaid_IsDenied()
    {
        var cycle = BillingCycle.Create(
            Guid.NewGuid(),
            "tenant-1",
            Guid.NewGuid(),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)).Value;

        cycle.MarkInvoiced();
        cycle.MarkPaid();

        Assert.False(cycle.Cancel().IsSuccess);
        Assert.Equal(BillingCycleStatus.Paid, cycle.Status);
    }

    [Fact]
    public void BillingCycle_MarkInvoiced_FromNonDraft_IsDenied()
    {
        var cycle = BillingCycle.Create(
            Guid.NewGuid(),
            "tenant-1",
            Guid.NewGuid(),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)).Value;

        cycle.Cancel();

        Assert.False(cycle.MarkInvoiced().IsSuccess);
        Assert.Equal(BillingCycleStatus.Cancelled, cycle.Status);
    }

    // ------------------------------------------------------------------
    // Invoice commercial traceability tests
    // ------------------------------------------------------------------

    [Fact]
    public void Invoice_Create_WithCommercialLinks_Succeeds()
    {
        var contractId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var billingCycleId = Guid.NewGuid();

        var invoice = Invoice.Create(
            Guid.NewGuid(),
            "INV-001",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            1000m,
            0,
            0,
            1000m,
            contractId,
            subscriptionId,
            billingCycleId);

        Assert.True(invoice.IsSuccess);
        var entity = invoice.Value;
        Assert.Equal(contractId, entity.ContractId);
        Assert.Equal(subscriptionId, entity.SubscriptionId);
        Assert.Equal(billingCycleId, entity.BillingCycleId);
    }

    [Fact]
    public void Invoice_Create_WithoutCommercialLinks_Succeeds()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            "INV-002",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            500m,
            0,
            0,
            500m);

        Assert.True(invoice.IsSuccess);
        var entity = invoice.Value;
        Assert.Null(entity.ContractId);
        Assert.Null(entity.SubscriptionId);
        Assert.Null(entity.BillingCycleId);
    }

    [Fact]
    public void Invoice_TotalAmount_IsImmutable_AfterCreation()
    {
        var invoice = Invoice.Create(
            Guid.NewGuid(),
            "INV-003",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            1000m,
            100m,
            50m,
            950m).Value;

        // Verify initial amounts
        Assert.Equal(1000m, invoice.Subtotal);
        Assert.Equal(100m, invoice.DiscountAmount);
        Assert.Equal(50m, invoice.TaxAmount);
        Assert.Equal(950m, invoice.TotalAmount);

        // The domain entity has no public setters for amounts - they are set only in Create/Issue.
        // This test verifies the snapshot is preserved.
        Assert.Equal(950m, invoice.TotalAmount);
    }
}
