namespace Centerix.SecurityTests;

using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Billing.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Billing.Invoicing;
using Centerix.Domain.Platform.Contracts;
using Centerix.Domain.Platform.Subscriptions;
using Centerix.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

/// <summary>
/// TASK-21.1: Invoice Trust-Boundary & Traceability Hardening Tests.
/// Tests server-authoritative amount derivation, commercial traceability,
/// and cross-tenant isolation for invoice creation.
/// </summary>
public class TASK21_InvoiceTrustBoundaryTests
{
    private const string TenantA = "tenant-A";
    private const string TenantB = "tenant-B";

    // ==================================================================
    // Helpers
    // ==================================================================

    private static AppDbContext CreateDbContext(string? tenantId = null)
    {
        var dbName = $"TASK21_Test_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var mediator = Substitute.For<IMediator>();
        var currentTenant = Substitute.For<ICurrentTenant>();
        currentTenant.TenantId.Returns(tenantId ?? TenantA);
        currentTenant.IsAuthorized.Returns(tenantId != null);

        return new AppDbContext(options, mediator, currentTenant);
    }

    private static async Task<Contract> CreateActiveContractAsync(
        AppDbContext db,
        string tenantId,
        decimal contractedAmount = 10000m,
        decimal discountAmount = 500m)
    {
        var contractResult = Contract.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            contractNumber: $"CONTRACT-{Guid.NewGuid():N}",
            planId: 1,
            effectiveAtUtc: DateTime.UtcNow.AddMonths(-1),
            endsAtUtc: DateTime.UtcNow.AddMonths(11),
            durationMonths: 12,
            monthlyListPrice: contractedAmount,
            contractualMonthlyValue: contractedAmount,
            currencyCode: "EGP",
            contractedAmount: contractedAmount,
            entitlementSnapshotVersion: Contract.CompleteEntitlementSnapshotVersion,
            discountAmount: discountAmount);

        if (!contractResult.IsSuccess)
            throw new InvalidOperationException("Failed to create contract");

        var contract = contractResult.Value;
        contract.Activate(DateTime.UtcNow);

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract;
    }

    private static async Task<TenantPlan> CreateActiveSubscriptionAsync(
        AppDbContext db,
        string tenantId,
        Guid contractId,
        decimal snapshotPrice = 9500m)
    {
        var subscription = TenantPlan.Create(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            planId: 1,
            snapshotPrice: snapshotPrice,
            snapshotCurrency: "EGP",
            durationMonths: 12,
            bonusMonths: 0,
            startsAtUtc: DateTime.UtcNow.AddMonths(-1)).Value;

        // Manually set ContractId through reflection for testing
        // The property has a private setter, so we need NonPublic binding
        var contractIdProperty = typeof(TenantPlan).GetProperty("ContractId",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (contractIdProperty != null)
        {
            var setMethod = contractIdProperty.GetSetMethod(true);
            setMethod?.Invoke(subscription, new object[] { contractId });
        }

        subscription.Activate(DateTime.UtcNow);

        db.TenantPlans.Add(subscription);
        await db.SaveChangesAsync();
        return subscription;
    }

    private static ICurrentTenant CreateTenantService(string tenantId)
    {
        var tenantService = Substitute.For<ICurrentTenant>();
        tenantService.TenantId.Returns(tenantId);
        tenantService.IsAuthorized.Returns(true);
        return tenantService;
    }

    // ==================================================================
    // A. Client Amount Tampering — TotalAmount mismatch
    // ==================================================================

    [Fact]
    public async Task ClientAmountTampering_TotalAmountMismatch_Rejects()
    {
        using var db = CreateDbContext(TenantA);
        var contract = await CreateActiveContractAsync(db, TenantA);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        // Client sends TotalAmount = 1, but authoritative should be 9500 (10000 - 500)
        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contract.Id,
            SubscriptionId: null,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: 1m); // Client tries to send 1 instead of 9500

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.ClientAmountMismatch");
    }

    // ==================================================================
    // B. Client Discount Tampering — DiscountAmount mismatch
    // ==================================================================

    [Fact]
    public async Task ClientDiscountTampering_DiscountAmountMismatch_Rejects()
    {
        using var db = CreateDbContext(TenantA);
        var contract = await CreateActiveContractAsync(db, TenantA, discountAmount: 500m);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        // Client sends DiscountAmount = 999999 instead of 500
        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contract.Id,
            SubscriptionId: null,
            Subtotal: null,
            DiscountAmount: 999999m, // Tampering attempt
            TaxAmount: null,
            TotalAmount: null);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.ClientAmountMismatch");
    }

    // ==================================================================
    // C. Client Subtotal Tampering — Subtotal mismatch
    // ==================================================================

    [Fact]
    public async Task ClientSubtotalTampering_SubtotalMismatch_Rejects()
    {
        using var db = CreateDbContext(TenantA);
        var contract = await CreateActiveContractAsync(db, TenantA, contractedAmount: 10000m);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        // Client sends Subtotal = 1 instead of 10000
        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contract.Id,
            SubscriptionId: null,
            Subtotal: 1m, // Tampering attempt
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.ClientAmountMismatch");
    }

    // ==================================================================
    // D. Missing Contract — No ContractId
    // ==================================================================

    [Fact]
    public async Task MissingContract_ContractNotFound_Rejects()
    {
        using var db = CreateDbContext(TenantA);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: Guid.NewGuid(), // Non-existent contract
            SubscriptionId: null,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.ContractNotFound");
    }

    // ==================================================================
    // E. Cross-Tenant Contract — Tenant A uses Tenant B's Contract
    // ==================================================================

    [Fact]
    public async Task CrossTenantContract_TenantBContract_Rejects()
    {
        using var db = CreateDbContext(TenantA);
        var contractB = await CreateActiveContractAsync(db, TenantB);
        var tenantServiceA = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantServiceA,
            Substitute.For<IAuditWriter>());

        // Tenant A tries to create invoice using Tenant B's contract
        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contractB.Id,
            SubscriptionId: null,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.ContractNotOwnedByTenant");
    }

    // ==================================================================
    // F. Contract/Subscription Mismatch — Contract A + Subscription B
    // ==================================================================

    [Fact]
    public async Task ContractSubscriptionMismatch_ContractASubscriptionB_Rejects()
    {
        using var db = CreateDbContext(TenantA);
        var contractA = await CreateActiveContractAsync(db, TenantA);
        var contractB = await CreateActiveContractAsync(db, TenantA);
        var subscriptionB = await CreateActiveSubscriptionAsync(db, TenantA, contractB.Id);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        // Contract A + Subscription B (which belongs to Contract B)
        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contractA.Id,
            SubscriptionId: subscriptionB.Id,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.SubscriptionContractMismatch");
    }

    // ==================================================================
    // G. Subscription Cross-Tenant Mismatch
    // ==================================================================

    [Fact]
    public async Task SubscriptionCrossTenantMismatch_Rejects()
    {
        using var db = CreateDbContext(TenantA);
        var contractA = await CreateActiveContractAsync(db, TenantA);
        var contractB = await CreateActiveContractAsync(db, TenantB);
        var subscriptionB = await CreateActiveSubscriptionAsync(db, TenantB, contractB.Id);
        var tenantServiceA = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantServiceA,
            Substitute.For<IAuditWriter>());

        // Tenant A tries to use Tenant B's subscription with Contract A
        // First rejection will be SubscriptionContractMismatch because Subscription B belongs to Contract B
        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contractA.Id,
            SubscriptionId: subscriptionB.Id,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        // First error is SubscriptionContractMismatch because subscription doesn't belong to contract
        Assert.Contains(result.Errors!, e => e.Code == "Invoice.SubscriptionContractMismatch");
    }

    // ==================================================================
    // H. Invoice Number Uniqueness within Tenant
    // ==================================================================

    [Fact]
    public async Task InvoiceNumberUniqueness_SameTenant_SameNumber_RejectsDuplicate()
    {
        using var db = CreateDbContext(TenantA);
        var contract = await CreateActiveContractAsync(db, TenantA);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        var sameInvoiceNumber = "INV-SAME-NUMBER";

        // First invoice
        var command1 = new CreateInvoiceCommand(
            InvoiceNumber: sameInvoiceNumber,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contract.Id,
            SubscriptionId: null,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result1 = await handler.Handle(command1, CancellationToken.None);
        Assert.True(result1.IsSuccess);

        // Second invoice with same number for same tenant
        var command2 = new CreateInvoiceCommand(
            InvoiceNumber: sameInvoiceNumber,
            PeriodStart: new DateOnly(2026, 2, 1),
            PeriodEnd: new DateOnly(2026, 2, 28),
            ContractId: contract.Id,
            SubscriptionId: null,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result2 = await handler.Handle(command2, CancellationToken.None);
        Assert.False(result2.IsSuccess);
        Assert.Contains(result2.Errors!, e => e.Code == "Invoice.DuplicateInvoiceNumber");
    }

    // ==================================================================
    // I. Valid Server-Derived Invoice Creation
    // ==================================================================

    [Fact]
    public async Task ValidServerDerivedInvoice_NoClientAmounts_Succeeds()
    {
        using var db = CreateDbContext(TenantA);
        var contract = await CreateActiveContractAsync(db, TenantA, contractedAmount: 10000m, discountAmount: 500m);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        // No client amounts provided - server derives from Contract
        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contract.Id,
            SubscriptionId: null,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Verify invoice amounts match server-authoritative values
        var invoice = await db.Invoices.FirstAsync(i => i.ContractId == contract.Id);
        Assert.Equal(10000m, invoice.Subtotal);
        Assert.Equal(500m, invoice.DiscountAmount);
        Assert.Equal(0m, invoice.TaxAmount);
        Assert.Equal(9500m, invoice.TotalAmount); // 10000 - 500 + 0
    }

    // ==================================================================
    // J. Invoice Mathematical Integrity — TotalAmount = Subtotal - Discount + Tax
    // ==================================================================

    [Fact]
    public async Task InvoiceMathematicalIntegrity_ValidAmounts_CreatesInvoice()
    {
        using var db = CreateDbContext(TenantA);
        var contract = await CreateActiveContractAsync(db, TenantA);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        // Provide amounts that match server derivation (valid)
        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contract.Id,
            SubscriptionId: null,
            Subtotal: 10000m,
            DiscountAmount: 500m,
            TaxAmount: 0m,
            TotalAmount: 9500m);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    // ==================================================================
    // K. Valid Invoice with Subscription Link
    // ==================================================================

    [Fact]
    public async Task ValidInvoice_WithSubscription_LinksCorrectly()
    {
        using var db = CreateDbContext(TenantA);
        var contract = await CreateActiveContractAsync(db, TenantA);
        var subscription = await CreateActiveSubscriptionAsync(db, TenantA, contract.Id);
        var tenantService = CreateTenantService(TenantA);

        var handler = new CreateInvoiceHandler(
            db,
            tenantService,
            Substitute.For<IAuditWriter>());

        var command = new CreateInvoiceCommand(
            InvoiceNumber: null,
            PeriodStart: new DateOnly(2026, 1, 1),
            PeriodEnd: new DateOnly(2026, 1, 31),
            ContractId: contract.Id,
            SubscriptionId: subscription.Id,
            Subtotal: null,
            DiscountAmount: null,
            TaxAmount: null,
            TotalAmount: null);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var invoice = await db.Invoices.FirstAsync(i => i.ContractId == contract.Id);
        Assert.Equal(contract.Id, invoice.ContractId);
        Assert.Equal(subscription.Id, invoice.SubscriptionId);
    }
}
