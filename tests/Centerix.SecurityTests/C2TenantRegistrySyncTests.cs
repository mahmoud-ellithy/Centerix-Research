using Centerix.Application.Common.Interfaces;
using Centerix.Application.Platform.Tenants.Commands;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Infrastructure.Tenancy;
using MediatR;
using NSubstitute;
using Xunit;

namespace Centerix.SecurityTests;

/// <summary>
/// C2 tests verifying that handlers correctly delegate to ITenantRegistrySync
/// for atomic dual-writes between Platform.Tenants and Platform.TenantRegistry.
/// </summary>
public class C2TenantRegistrySyncTests
{
    private static Tenant CreatePersistedTenant(string slug = "test")
    {
        var result = Tenant.Create(
            Guid.NewGuid(), slug, slug, $"Test {slug}",
            "EG", "EGP", "Africa/Cairo",
            "Test", "Owner", $"{slug}@test.com", IsolationMode.Shared);
        return result.Value;
    }

    private static IAuditWriter CreateMockAuditWriter()
    {
        var audit = Substitute.For<IAuditWriter>();
        audit.WriteAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return audit;
    }

    [Fact]
    public async Task CreateTenant_CallsSyncCreatedAsync()
    {
        var db = Substitute.For<IAppDbContext>();
        var sync = Substitute.For<ITenantRegistrySync>();

        var handler = new CreateTenantHandler(db, sync, CreateMockAuditWriter());
        var result = await handler.Handle(new CreateTenantCommand(
            "t1", "t1", "Tenant 1", "EG", "EGP", "Africa/Cairo",
            "A", "B", "a@b.com", IsolationMode.Shared), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await sync.Received(1).SyncCreatedAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTenant_PassesCorrectTenantToSync()
    {
        var db = Substitute.For<IAppDbContext>();
        Tenant? captured = null;
        var sync = Substitute.For<ITenantRegistrySync>();
        sync.SyncCreatedAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.Arg<Tenant>(); return Task.CompletedTask; });

        var handler = new CreateTenantHandler(db, sync, CreateMockAuditWriter());
        await handler.Handle(new CreateTenantCommand(
            "slug1", "sub1", "Display", "EG", "EGP", "Africa/Cairo",
            "F", "L", "f@l.com", IsolationMode.Shared), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("slug1", captured!.Slug);
        Assert.Equal("sub1", captured.Subdomain);
        Assert.Equal("Display", captured.DisplayName);
        Assert.Equal(LifecycleStatus.Provisioning, captured.LifecycleStatus);
        Assert.True(captured.IsActive);
    }

    [Fact]
    public async Task CreateTenant_DomainTenantIsAddedToDbContext()
    {
        var db = Substitute.For<IAppDbContext>();
        var sync = Substitute.For<ITenantRegistrySync>();

        var handler = new CreateTenantHandler(db, sync, CreateMockAuditWriter());
        await handler.Handle(new CreateTenantCommand(
            "s1", "s1", "T", "EG", "EGP", "Africa/Cairo",
            "A", "B", "a@b.com", IsolationMode.Shared), CancellationToken.None);

        db.Tenants.Received(1).Add(Arg.Any<Tenant>());
    }

    [Fact]
    public async Task CreateTenant_SyncIsCalledBeforeSave()
    {
        var db = Substitute.For<IAppDbContext>();
        var callOrder = new List<string>();
        var sync = Substitute.For<ITenantRegistrySync>();
        sync.SyncCreatedAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(ci => { callOrder.Add("sync"); return Task.CompletedTask; });
        db.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(ci => { callOrder.Add("save"); return 1; });

        var handler = new CreateTenantHandler(db, sync, CreateMockAuditWriter());
        await handler.Handle(new CreateTenantCommand(
            "s1", "s1", "T", "EG", "EGP", "Africa/Cairo",
            "A", "B", "a@b.com", IsolationMode.Shared), CancellationToken.None);

        Assert.Equal("sync", callOrder[0]);
    }

    [Fact]
    public async Task SuspendTenant_CallsSyncLifecycleAsync()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("sus");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();

        var handler = new SuspendTenantHandler(db, sync, CreateMockAuditWriter());
        var result = await handler.Handle(
            new SuspendTenantCommand(tenant.Id, "reason"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await sync.Received(1).SyncLifecycleAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendTenant_StateIsSuspendedBeforeSync()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("sus2");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        Tenant? captured = null;
        var sync = Substitute.For<ITenantRegistrySync>();
        sync.SyncLifecycleAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.Arg<Tenant>(); return Task.CompletedTask; });

        var handler = new SuspendTenantHandler(db, sync, CreateMockAuditWriter());
        await handler.Handle(new SuspendTenantCommand(tenant.Id, "why"), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(LifecycleStatus.Suspended, captured!.LifecycleStatus);
        Assert.False(captured.IsActive);
        Assert.Equal("why", captured.SuspendedReason);
    }

    [Fact]
    public async Task ActivateTenant_CallsSyncLifecycleAsync()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("act");
        tenant.Suspend("test");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();

        var handler = new ReactivateTenantHandler(db, sync, CreateMockAuditWriter());
        var result = await handler.Handle(
            new ReactivateTenantCommand(tenant.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await sync.Received(1).SyncLifecycleAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateTenant_StateIsActiveBeforeSync()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("act2");
        tenant.Suspend("test");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        Tenant? captured = null;
        var sync = Substitute.For<ITenantRegistrySync>();
        sync.SyncLifecycleAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.Arg<Tenant>(); return Task.CompletedTask; });

        var handler = new ReactivateTenantHandler(db, sync, CreateMockAuditWriter());
        await handler.Handle(new ReactivateTenantCommand(tenant.Id), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(LifecycleStatus.Active, captured!.LifecycleStatus);
        Assert.True(captured.IsActive);
    }

    [Fact]
    public async Task CancelTenant_CallsSyncLifecycleAsync()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("can");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();

        var handler = new CancelTenantHandler(db, sync, CreateMockAuditWriter());
        var result = await handler.Handle(
            new CancelTenantCommand(tenant.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await sync.Received(1).SyncLifecycleAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelTenant_StateIsCancelledBeforeSync()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("can2");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        Tenant? captured = null;
        var sync = Substitute.For<ITenantRegistrySync>();
        sync.SyncLifecycleAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.Arg<Tenant>(); return Task.CompletedTask; });

        var handler = new CancelTenantHandler(db, sync, CreateMockAuditWriter());
        await handler.Handle(new CancelTenantCommand(tenant.Id), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(LifecycleStatus.Cancelled, captured!.LifecycleStatus);
        Assert.False(captured.IsActive);
    }

    [Fact]
    public async Task UpdateTenant_CallsSyncMetadataAsync()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("upd");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();

        var handler = new UpdateTenantHandler(db, sync, CreateMockAuditWriter());
        var result = await handler.Handle(
            new UpdateTenantCommand(tenant.Id, "New Name", null, null, null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await sync.Received(1).SyncMetadataAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTenant_PassesCorrectMetadataToSync()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("upd2");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        Tenant? captured = null;
        var sync = Substitute.For<ITenantRegistrySync>();
        sync.SyncMetadataAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.Arg<Tenant>(); return Task.CompletedTask; });

        var handler = new UpdateTenantHandler(db, sync, CreateMockAuditWriter());
        await handler.Handle(new UpdateTenantCommand(
            tenant.Id, "Updated", "http://logo.png", "#FF0000", null), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Updated", captured!.DisplayName);
        Assert.Equal("http://logo.png", captured.LogoUrl);
        Assert.Equal("#FF0000", captured.PrimaryColor);
    }

    [Fact]
    public async Task SuspendTenant_ReturnsNotFoundForMissingTenant()
    {
        var db = Substitute.For<IAppDbContext>();
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);
        var sync = Substitute.For<ITenantRegistrySync>();
        var handler = new SuspendTenantHandler(db, sync, CreateMockAuditWriter());

        var result = await handler.Handle(
            new SuspendTenantCommand(Guid.NewGuid(), "reason"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await sync.DidNotReceive().SyncLifecycleAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelTenant_ReturnsNotFoundForMissingTenant()
    {
        var db = Substitute.For<IAppDbContext>();
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);
        var sync = Substitute.For<ITenantRegistrySync>();
        var handler = new CancelTenantHandler(db, sync, CreateMockAuditWriter());

        var result = await handler.Handle(
            new CancelTenantCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await sync.DidNotReceive().SyncLifecycleAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendTenant_ReturnsErrorForAlreadySuspended()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("dup");
        tenant.Suspend("first");
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();
        var handler = new SuspendTenantHandler(db, sync, CreateMockAuditWriter());

        var result = await handler.Handle(
            new SuspendTenantCommand(tenant.Id, "second"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await sync.DidNotReceive().SyncLifecycleAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateTenant_ReturnsErrorForAlreadyActive()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("alr");
        tenant.Activate();
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();
        var handler = new ReactivateTenantHandler(db, sync, CreateMockAuditWriter());

        var result = await handler.Handle(
            new ReactivateTenantCommand(tenant.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await sync.DidNotReceive().SyncLifecycleAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelTenant_ReturnsErrorForAlreadyCancelled()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("alc");
        tenant.Cancel();
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();
        var handler = new CancelTenantHandler(db, sync, CreateMockAuditWriter());

        var result = await handler.Handle(
            new CancelTenantCommand(tenant.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await sync.DidNotReceive().SyncLifecycleAsync(
            Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateTenant_ReturnsErrorForCancelled()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("canc");
        tenant.Cancel();
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();
        var handler = new ReactivateTenantHandler(db, sync, CreateMockAuditWriter());

        var result = await handler.Handle(
            new ReactivateTenantCommand(tenant.Id), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SuspendTenant_ReturnsErrorForCancelled()
    {
        var db = Substitute.For<IAppDbContext>();
        var tenant = CreatePersistedTenant("canc2");
        tenant.Cancel();
        db.Tenants.FindAsync(Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(tenant);
        var sync = Substitute.For<ITenantRegistrySync>();
        var handler = new SuspendTenantHandler(db, sync, CreateMockAuditWriter());

        var result = await handler.Handle(
            new SuspendTenantCommand(tenant.Id, "reason"), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void TenantLifecycle_Provisioning_IsActive()
    {
        var tenant = CreatePersistedTenant();
        Assert.Equal(LifecycleStatus.Provisioning, tenant.LifecycleStatus);
        Assert.True(tenant.IsActive);
    }

    [Fact]
    public void TenantLifecycle_Suspended_IsInactive()
    {
        var tenant = CreatePersistedTenant();
        tenant.Suspend("reason");
        Assert.Equal(LifecycleStatus.Suspended, tenant.LifecycleStatus);
        Assert.False(tenant.IsActive);
    }

    [Fact]
    public void TenantLifecycle_Cancelled_IsInactive()
    {
        var tenant = CreatePersistedTenant();
        tenant.Cancel();
        Assert.Equal(LifecycleStatus.Cancelled, tenant.LifecycleStatus);
        Assert.False(tenant.IsActive);
    }

    [Fact]
    public void TenantLifecycle_ActivateFromSuspended_SetsIsActive()
    {
        var tenant = CreatePersistedTenant();
        tenant.Suspend("reason");
        tenant.Activate();
        Assert.Equal(LifecycleStatus.Active, tenant.LifecycleStatus);
        Assert.True(tenant.IsActive);
    }

    [Fact]
    public void TenantLifecycle_CannotActivateFromCancelled()
    {
        var tenant = CreatePersistedTenant();
        tenant.Cancel();
        var result = tenant.Activate();
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void TenantLifecycle_CannotSuspendCancelled()
    {
        var tenant = CreatePersistedTenant();
        tenant.Cancel();
        var result = tenant.Suspend("reason");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void TenantId_TenantRegistryUsesGuidToString()
    {
        var tenant = CreatePersistedTenant();
        var registryId = tenant.Id.ToString();
        Assert.False(string.IsNullOrEmpty(registryId));
        Assert.NotEqual("root", registryId);
        Assert.True(Guid.TryParse(registryId, out _));
    }
}
