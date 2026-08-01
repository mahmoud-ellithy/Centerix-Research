namespace Centerix.Domain.Platform.Tenants;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Platform.Tenants.Events;

/// <summary>
/// Central registry for every subscribed center in the platform.
/// Platform-scoped (NOT IHasTenantId — this IS the tenant).
/// </summary>
public class Tenant : AuditableEntity
{
    public Guid Id { get; private set; }
    public string Slug { get; private set; } = default!;
    public string Subdomain { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string? LogoUrl { get; private set; }
    public string? PrimaryColor { get; private set; }
    public string Country { get; private set; } = default!;
    public string Currency { get; private set; } = default!;
    public string Timezone { get; private set; } = default!;
    public string OwnerFirstName { get; private set; } = default!;
    public string OwnerLastName { get; private set; } = default!;
    public string OwnerEmail { get; private set; } = default!;
    public string? OwnerPhone { get; private set; }
    public IsolationMode IsolationMode { get; private set; }
    public string? DatabaseServer { get; private set; }
    public string? ConnectionStringRef { get; private set; }
    public int? CurrentPlanId { get; private set; }
    public LifecycleStatus LifecycleStatus { get; private set; }
    public string? SuspendedReason { get; private set; }
    public DateTime? TrialEndsAt { get; private set; }
    public DateTime? ValidUpTo { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime? LastSyncedAt { get; private set; }

    private Tenant() { }

    private Tenant(
        Guid id,
        string slug,
        string subdomain,
        string displayName,
        string country,
        string currency,
        string timezone,
        string ownerFirstName,
        string ownerLastName,
        string ownerEmail,
        IsolationMode isolationMode,
        LifecycleStatus lifecycleStatus)
    {
        Id = id;
        Slug = slug;
        Subdomain = subdomain;
        DisplayName = displayName;
        Country = country;
        Currency = currency;
        Timezone = timezone;
        OwnerFirstName = ownerFirstName;
        OwnerLastName = ownerLastName;
        OwnerEmail = ownerEmail;
        IsolationMode = isolationMode;
        LifecycleStatus = lifecycleStatus;
        IsActive = true;
    }

    public static Result<Tenant> Create(
        Guid id,
        string slug,
        string subdomain,
        string displayName,
        string country,
        string currency,
        string timezone,
        string ownerFirstName,
        string ownerLastName,
        string ownerEmail,
        IsolationMode isolationMode,
        string? logoUrl = null,
        string? primaryColor = null,
        string? ownerPhone = null,
        string? databaseServer = null,
        string? connectionStringRef = null)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return TenantErrors.SlugRequired;

        if (string.IsNullOrWhiteSpace(subdomain))
            return TenantErrors.SubdomainRequired;

        if (string.IsNullOrWhiteSpace(displayName))
            return TenantErrors.DisplayNameRequired;

        if (string.IsNullOrWhiteSpace(country) || country.Length != 2)
            return TenantErrors.CountryRequired;

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            return TenantErrors.CurrencyRequired;

        if (string.IsNullOrWhiteSpace(timezone))
            return TenantErrors.TimezoneRequired;

        if (string.IsNullOrWhiteSpace(ownerFirstName))
            return TenantErrors.OwnerFirstNameRequired;

        if (string.IsNullOrWhiteSpace(ownerLastName))
            return TenantErrors.OwnerLastNameRequired;

        if (string.IsNullOrWhiteSpace(ownerEmail))
            return TenantErrors.OwnerEmailRequired;

        if (!Enum.IsDefined(isolationMode))
            return TenantErrors.InvalidIsolationMode;

        var tenant = new Tenant(
            id, slug.ToLowerInvariant(), subdomain.ToLowerInvariant(),
            displayName, country.ToUpperInvariant(), currency.ToUpperInvariant(),
            timezone, ownerFirstName, ownerLastName, ownerEmail,
            isolationMode, LifecycleStatus.Provisioning);

        tenant.LogoUrl = logoUrl;
        tenant.PrimaryColor = primaryColor;
        tenant.OwnerPhone = ownerPhone;
        tenant.DatabaseServer = databaseServer;
        tenant.ConnectionStringRef = connectionStringRef;

        tenant.AddDomainEvent(new TenantCreatedEvent(id, slug));

        return tenant;
    }

    public Result<Updated> Update(
        string displayName,
        string? logoUrl,
        string? primaryColor,
        string? ownerPhone)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return TenantErrors.DisplayNameRequired;

        DisplayName = displayName;
        LogoUrl = logoUrl;
        PrimaryColor = primaryColor;
        OwnerPhone = ownerPhone;

        return Result.Updated;
    }

    public Result<Updated> Activate()
    {
        if (LifecycleStatus == LifecycleStatus.Active)
            return TenantErrors.AlreadyActive;

        if (LifecycleStatus == LifecycleStatus.Cancelled)
            return TenantErrors.AlreadyCancelled;

        LifecycleStatus = LifecycleStatus.Active;

        AddDomainEvent(new TenantReactivatedEvent(Id));

        return Result.Updated;
    }

    public Result<Updated> Suspend(string reason)
    {
        if (LifecycleStatus == LifecycleStatus.Suspended)
            return TenantErrors.AlreadySuspended;

        if (LifecycleStatus == LifecycleStatus.Cancelled)
            return TenantErrors.AlreadyCancelled;

        LifecycleStatus = LifecycleStatus.Suspended;
        SuspendedReason = reason;

        AddDomainEvent(new TenantSuspendedEvent(Id, reason));

        return Result.Updated;
    }

    public Result<Updated> Cancel()
    {
        if (LifecycleStatus == LifecycleStatus.Cancelled)
            return TenantErrors.AlreadyCancelled;

        LifecycleStatus = LifecycleStatus.Cancelled;
        IsActive = false;

        AddDomainEvent(new TenantCancelledEvent(Id));

        return Result.Updated;
    }

    public Result<Updated> UpgradePlan(int newPlanId)
    {
        if (LifecycleStatus != LifecycleStatus.Active)
            return TenantErrors.InvalidLifecycleStatus;

        CurrentPlanId = newPlanId;

        return Result.Updated;
    }

    public void MarkSynced(DateTime utcNow)
    {
        LastSyncedAt = utcNow;
    }
}
