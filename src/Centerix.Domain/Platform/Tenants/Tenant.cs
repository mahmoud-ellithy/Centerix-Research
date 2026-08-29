namespace Centerix.Domain.Platform.Tenants;

using Centerix.Domain.Common;
using Centerix.Domain.Common.Results;
using Centerix.Domain.Platform.Tenants.Enums;
using Centerix.Domain.Platform.Tenants.Events;

/// <summary>
/// Central registry for every subscribed center in the platform.
/// Platform-scoped (NOT IHasTenantId â€” this IS the tenant).
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

        // A tenant only becomes operational (IsActive=true) through platform approval +
        // provisioning completion. PendingApproval starts INACTIVE so the tenant guard blocks
        // every tenant-scoped request until the platform activates it.
        IsActive = lifecycleStatus == LifecycleStatus.Active || lifecycleStatus == LifecycleStatus.Provisioning;
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
            isolationMode, LifecycleStatus.PendingApproval);

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

    /// <summary>
    /// Platform Admin approves the tenant application. Only PendingApproval tenants can be
    /// approved; approval moves the tenant into Provisioning (subscription assignment happens
    /// in the same application transaction by the caller).
    /// </summary>
    public Result<Updated> Approve()
    {
        if (LifecycleStatus != LifecycleStatus.PendingApproval)
            return TenantErrors.CannotApprove(LifecycleStatus);

        LifecycleStatus = LifecycleStatus.Provisioning;
        IsActive = false;

        AddDomainEvent(new TenantApprovedEvent(Id));

        return Result.Updated;
    }

    /// <summary>
    /// Platform Admin rejects the tenant application. Terminal for this phase.
    /// </summary>
    public Result<Updated> Reject(string reason)
    {
        if (LifecycleStatus != LifecycleStatus.PendingApproval)
            return TenantErrors.CannotReject(LifecycleStatus);

        if (string.IsNullOrWhiteSpace(reason))
            return TenantErrors.RejectionReasonRequired;

        LifecycleStatus = LifecycleStatus.Rejected;
        IsActive = false;
        SuspendedReason = reason.Trim();

        AddDomainEvent(new TenantRejectedEvent(Id, SuspendedReason));

        return Result.Updated;
    }

    public Result<Updated> Activate()
    {
        if (LifecycleStatus == LifecycleStatus.Active)
            return TenantErrors.AlreadyActive;

        // Commercial gate: a PendingApproval tenant MUST NOT be activated without platform
        // approval (Approve), and a Rejected application cannot self-activate.
        if (LifecycleStatus == LifecycleStatus.PendingApproval || LifecycleStatus == LifecycleStatus.Rejected)
            return TenantErrors.InvalidLifecycleStatus;

        if (LifecycleStatus == LifecycleStatus.Cancelled)
            return TenantErrors.AlreadyCancelled;

        LifecycleStatus = LifecycleStatus.Active;
        IsActive = true;

        AddDomainEvent(new TenantReactivatedEvent(Id));

        return Result.Updated;
    }

    public Result<Updated> Suspend(string reason)
    {
        if (LifecycleStatus == LifecycleStatus.Suspended)
            return TenantErrors.AlreadySuspended;

        // Suspension is an operational action on a live/provisioning tenant.
        if (LifecycleStatus is not (LifecycleStatus.Active or LifecycleStatus.Provisioning))
            return TenantErrors.InvalidLifecycleStatus;

        if (string.IsNullOrWhiteSpace(reason))
            return TenantErrors.SuspensionReasonRequired;

        LifecycleStatus = LifecycleStatus.Suspended;
        IsActive = false;
        SuspendedReason = reason.Trim();

        AddDomainEvent(new TenantSuspendedEvent(Id, SuspendedReason));

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

    public void SetValidUpTo(DateTime validUpTo)
    {
        ValidUpTo = validUpTo;
    }
}

