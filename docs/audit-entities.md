# 1. Entities

## 1.1 Authorization Entities (Tenant-scoped, Identity-based)

### `Permission`

- **File:** `src/Centerix.Domain/Platform/Authorization/Permission.cs`
- **Base class:** `GlobalAuditableEntity<int>` (NOT tenant-scoped — shared across all tenants)
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `Id` | `int` | PK, Identity(1,1) |
| `Module` | `string` | Required, MaxLength(50) |
| `Action` | `string` | Required, MaxLength(50) |
| `Code` | `string` | Required, MaxLength(80), Unique Index |
| `Description` | `string?` | Nullable, MaxLength(200) |
| `CreatedAtUtc` | `DateTimeOffset` | Required (column: `CreatedAt`) |
| `CreatedBy` | `string?` | Nullable, MaxLength(450) |
| `LastModifiedUtc` | `DateTimeOffset` | Required (column: `ModifiedAt`) |
| `LastModifiedBy` | `string?` | Nullable, MaxLength(450) |

- **Navigations:** `RolePermissions` (collection → `RolePermission`)
- **DB table:** `Platform.Permissions`
- **Implements:** Inherits `GlobalAuditableEntity<int>` → `AuditableEntity` → `Entity`. **Does NOT implement `IHasTenantId`** — intentionally global.
- **ASP.NET Identity dependency:** None. Custom domain entity.
- **Code constraint:** `Code` must follow `"{Module}.{Action}"` format (enforced in `Permission.Create()`)

```csharp
public class Permission : GlobalAuditableEntity<int>
{
    public string Module { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Code { get; private set; } = default!;
    public string? Description { get; private set; }

    private readonly List<RolePermission> _rolePermissions = [];
    public IReadOnlyList<RolePermission> RolePermissions => _rolePermissions.AsReadOnly();
}
```

### `RolePermission`

- **File:** `src/Centerix.Domain/Platform/Authorization/RolePermission.cs`
- **Base class:** `Entity` (no audit fields)
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `RoleId` | `string` | PK (part 1), MaxLength(450), FK → `AspNetRoles.Id` |
| `PermissionId` | `int` | PK (part 2), FK → `Platform.Permissions.Id` |

- **Navigations:** `Permission` (single)
- **DB table:** `Platform.RolePermissions`
- **Composite PK:** `(RoleId, PermissionId)`
- **Implements:** None (inherits `Entity` only). **No `IHasTenantId`** — `TenantId` was explicitly removed in the `RemoveTenantIdFromRolePermission` migration.
- **ASP.NET Identity dependency:** Yes — `RoleId` references `AspNetRoles.Id`.

```csharp
public class RolePermission : Entity
{
    public string RoleId { get; private set; } = default!;
    public int PermissionId { get; private set; }
    public Permission Permission { get; private set; } = default!;
}
```

### `ApplicationRole`

- **File:** `src/Centerix.Infrastructure/Auth/ApplicationRole.cs`
- **Base class:** `IdentityRole` (ASP.NET Core Identity)
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `Id` | `string` | PK (inherited from IdentityRole) |
| `Name` | `string?` | MaxLength(256) |
| `NormalizedName` | `string?` | MaxLength(256), Unique Index (filtered: NOT NULL) |
| `ConcurrencyStamp` | `string?` | MaxLength(max) |
| `Code` | `string?` | MaxLength(100), Unique Index (filtered: `[Code] IS NOT NULL`) |
| `DisplayName` | `string?` | MaxLength(150) |
| `IsSystem` | `bool` | Default: false |
| `Discriminator` | `string` | Required (TPH inheritance), MaxLength(21) |

- **DB table:** `AspNetRoles` (extends default Identity table)
- **Implements:** Implicitly via `IdentityRole` — **no `IHasTenantId`**.
- **ASP.NET Identity dependency:** **Direct** — extends `IdentityRole`.

```csharp
public class ApplicationRole : IdentityRole
{
    public string? Code { get; set; }
    public string? DisplayName { get; set; }
    public bool IsSystem { get; set; }
}
```

### `CenterixTenantInfo`

- **File:** `src/Centerix.Infrastructure/Tenancy/CenterixTenantInfo.cs`
- **Base class:** `ITenantInfo` (Finbuckle interface)
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `Id` | `string` | PK |
| `Identifier` | `string` | |
| `Name` | `string` | |
| `ConnectionString` | `string?` | |
| `Email` | `string?` | |
| `FirstName` | `string?` | |
| `LastName` | `string?` | |
| `ValidUpTo` | `DateTime` | |
| `IsActive` | `bool` | |
| `Slug` | `string?` | MaxLength(60) |
| `Subdomain` | `string?` | MaxLength(100), Unique Index |
| `DisplayName` | `string?` | MaxLength(200) |
| `LogoUrl` | `string?` | MaxLength(500) |
| `PrimaryColor` | `string?` | MaxLength(7) |
| `Country` | `string?` | MaxLength(2) |
| `Currency` | `string?` | MaxLength(3) |
| `Timezone` | `string?` | MaxLength(60) |
| `Status` | `byte` | |
| `TrialEndsAt` | `DateTime?` | |
| `CreatedAt` | `DateTime` | |

- **DB table:** `Platform.TenantRegistry` (in `TenantDbContext`)
- **ASP.NET Identity dependency:** None.

---

## 1.2 Platform Staff Entities (Global, non-Identity)

All Platform Staff entities inherit directly from `Entity` (no audit fields, no tenant filter).

### `PlatformUser`

- **File:** `src/Centerix.Domain/Platform/Staff/PlatformUser.cs`
- **Base class:** `Entity`
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `Id` | `Guid` | PK (column: `PlatformUserId`), ValueGeneratedNever |
| `Email` | `string` | Required, MaxLength(200), Unique Index |
| `FullName` | `string` | Required, MaxLength(200) |
| `PasswordHash` | `string` | Required, MaxLength(500) |
| `Is2FAEnabled` | `bool` | Default: false |
| `IsActive` | `bool` | Default: true |
| `CreatedAt` | `DateTimeOffset` | Required |

- **Navigations:** `UserRoles` (collection → `PlatformUserRole`)
- **DB table:** `Platform.PlatformUsers`
- **Implements:** None (inherits `Entity`). No `IHasTenantId`.
- **ASP.NET Identity dependency:** None. Completely standalone.

```csharp
public class PlatformUser : Entity
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = default!;
    public string FullName { get; private set; } = default!;
    public string PasswordHash { get; private set; } = default!;
    public bool Is2FAEnabled { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<PlatformUserRole> _userRoles = [];
    public IReadOnlyList<PlatformUserRole> UserRoles => _userRoles.AsReadOnly();
}
```

### `PlatformRole`

- **File:** `src/Centerix.Domain/Platform/Staff/PlatformRole.cs`
- **Base class:** `Entity`
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `Id` | `int` | PK (column: `RoleId`), Identity(1,1) |
| `Code` | `string` | Required, MaxLength(40), Unique Index (`UX_PlatformRoles_Code`) |
| `DisplayName` | `string` | Required, MaxLength(100) |

- **Navigations:** `UserRoles` (collection → `PlatformUserRole`), `RolePermissions` (collection → `PlatformRolePermission`)
- **DB table:** `Platform.PlatformRoles`
- **Implements:** None (inherits `Entity`). No `IHasTenantId`.
- **ASP.NET Identity dependency:** None.

```csharp
public class PlatformRole : Entity
{
    public int Id { get; private set; }
    public string Code { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;

    private readonly List<PlatformUserRole> _userRoles = [];
    public IReadOnlyList<PlatformUserRole> UserRoles => _userRoles.AsReadOnly();

    private readonly List<PlatformRolePermission> _rolePermissions = [];
    public IReadOnlyList<PlatformRolePermission> RolePermissions => _rolePermissions.AsReadOnly();
}
```

### `PlatformPermission`

- **File:** `src/Centerix.Domain/Platform/Staff/PlatformPermission.cs`
- **Base class:** `Entity`
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `Id` | `int` | PK (column: `PermissionId`), Identity(1,1) |
| `Module` | `string` | Required, MaxLength(20) |
| `Action` | `string` | Required, MaxLength(30) |
| `Code` | `string` | Required, MaxLength(80), Unique Index (`UX_PlatformPermissions_Code`) |

- **Navigations:** `RolePermissions` (collection → `PlatformRolePermission`)
- **DB table:** `Platform.PlatformPermissions`
- **Implements:** None (inherits `Entity`). No `IHasTenantId`.
- **ASP.NET Identity dependency:** None.

```csharp
public class PlatformPermission : Entity
{
    public int Id { get; private set; }
    public string Module { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Code { get; private set; } = default!;

    private readonly List<PlatformRolePermission> _rolePermissions = [];
    public IReadOnlyList<PlatformRolePermission> RolePermissions => _rolePermissions.AsReadOnly();
}
```

### `PlatformUserRole`

- **File:** `src/Centerix.Domain/Platform/Staff/PlatformUserRole.cs`
- **Base class:** `Entity`
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `PlatformUserId` | `Guid` | PK (part 1), FK → `PlatformUsers.PlatformUserId` (CASCADE) |
| `RoleId` | `int` | PK (part 2), FK → `PlatformRoles.RoleId` (CASCADE) |

- **Navigations:** `PlatformUser` (single), `Role` (single)
- **DB table:** `Platform.PlatformUserRoles`
- **Composite PK:** `(PlatformUserId, RoleId)`
- **Implements:** None (inherits `Entity`). No `IHasTenantId`.
- **ASP.NET Identity dependency:** None.

```csharp
public class PlatformUserRole : Entity
{
    public Guid PlatformUserId { get; private set; }
    public int RoleId { get; private set; }

    public PlatformUser PlatformUser { get; private set; } = default!;
    public PlatformRole Role { get; private set; } = default!;
}
```

### `PlatformRolePermission`

- **File:** `src/Centerix.Domain/Platform/Staff/PlatformRolePermission.cs`
- **Base class:** `Entity`
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `RoleId` | `int` | PK (part 1), FK → `PlatformRoles.RoleId` (CASCADE) |
| `PermissionId` | `int` | PK (part 2), FK → `PlatformPermissions.PermissionId` (CASCADE) |

- **Navigations:** `Role` (single), `Permission` (single)
- **DB table:** `Platform.PlatformRolePermissions`
- **Composite PK:** `(RoleId, PermissionId)`
- **Implements:** None (inherits `Entity`). No `IHasTenantId`.
- **ASP.NET Identity dependency:** None.

```csharp
public class PlatformRolePermission : Entity
{
    public int RoleId { get; private set; }
    public int PermissionId { get; private set; }

    public PlatformRole Role { get; private set; } = default!;
    public PlatformPermission Permission { get; private set; } = default!;
}
```

### `ImpersonationLog`

- **File:** `src/Centerix.Domain/Platform/Staff/ImpersonationLog.cs`
- **Base class:** `Entity`
- **Properties:**

| Property | Type | Constraints |
|----------|------|-------------|
| `Id` | `Guid` | PK (column: `LogId`), ValueGeneratedNever |
| `PlatformUserId` | `Guid` | Required, FK → `PlatformUsers.PlatformUserId` (RESTRICT) |
| `TenantId` | `string` | Required, MaxLength(450) (manual — NOT via `IHasTenantId`) |
| `TargetUserId` | `Guid` | Required |
| `StartedAt` | `DateTime` | Required, Indexed |
| `EndedAt` | `DateTime?` | Nullable |
| `Reason` | `string` | Required, MaxLength(300) |
| `IPAddress` | `string` | Required, MaxLength(45) |

- **Navigations:** `PlatformUser` (single)
- **DB table:** `Platform.ImpersonationLogs`
- **Indexes:** `IX_ImpersonationLogs_PlatformUserId`, `IX_ImpersonationLogs_TenantId`, `IX_ImpersonationLogs_StartedAt`
- **Implements:** None (inherits `Entity`). No `IHasTenantId` — has a manual `TenantId` string property.
- **ASP.NET Identity dependency:** None.

```csharp
public class ImpersonationLog : Entity
{
    public Guid Id { get; private set; }
    public Guid PlatformUserId { get; private set; }
    public string TenantId { get; private set; } = default!;
    public Guid TargetUserId { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public string Reason { get; private set; } = default!;
    public string IPAddress { get; private set; } = default!;

    public PlatformUser PlatformUser { get; private set; } = default!;
}
```

---

## 1.3 Entity Hierarchy Summary

```
Entity (abstract) — src/Centerix.Domain/Common/Entity.cs
  │   Properties: DomainEvents (collection)
  │
  ├── AuditableEntity — src/Centerix.Domain/Common/AuditableEntity.cs
  │     │   Properties: CreatedAtUtc, CreatedBy, LastModifiedUtc, LastModifiedBy
  │     │
  │     ├── AuditableEntity<TId> : IHasTenantId  ← Tenant-scoped entities
  │     │     │   Properties: Id, TenantId
  │     │     │
  │     │     ├── RefreshToken        (Domain/Authentication/)
  │     │     ├── AuditLog            (Domain/Auditing/)
  │     │     ├── TenantPlan          (Domain/Platform/Subscriptions/)
  │     │     ├── TenantCRMLead       (Domain/Platform/Leads/)
  │     │     ├── TenantCredit        (Domain/Platform/Billing/Credits/)
  │     │     ├── TenantAddOn         (Domain/Platform/Subscriptions/AddOns/)
  │     │     ├── TenantLimitOverride (Domain/Platform/Subscriptions/LimitOverrides/)
  │     │     ├── TenantReferralCode  (Domain/Platform/Referrals/)
  │     │     ├── TenantReferral      (Domain/Platform/Referrals/)
  │     │     ├── TenantSetting       (Domain/Platform/Operations/)
  │     │     ├── TenantProvisioningJob (Domain/Platform/Operations/)
  │     │     ├── TenantSchemaVersion (Domain/Platform/Operations/)
  │     │     ├── Invoice             (Domain/Platform/Billing/Invoicing/)
  │     │     └── TenantUsageCounter  (Domain/Platform/Subscriptions/UsageCounters/)
  │     │
  │     └── GlobalAuditableEntity<TId>  ← Global catalog entities (NO tenant filter)
  │           │   Properties: Id
  │           │
  │           ├── Permission          (Domain/Platform/Authorization/)
  │           ├── Plan                (Domain/Platform/Plans/)
  │           ├── Feature             (Domain/Platform/Features/)
  │           └── AddOnCatalog        (Domain/Platform/Subscriptions/AddOns/)
  │
  └── (direct from Entity — no audit fields)
        │
        ├── RolePermission            (Domain/Platform/Authorization/)  ← Identity junction
        ├── PlatformUser              (Domain/Platform/Staff/)
        ├── PlatformRole              (Domain/Platform/Staff/)
        ├── PlatformPermission        (Domain/Platform/Staff/)
        ├── PlatformUserRole          (Domain/Platform/Staff/)
        ├── PlatformRolePermission    (Domain/Platform/Staff/)
        └── ImpersonationLog          (Domain/Platform/Staff/)
```

---

## 1.4 `IHasTenantId` Interface

- **File:** `src/Centerix.Domain/Common/IHasTenantId.cs`

```csharp
public interface IHasTenantId
{
    string? TenantId { get; }
}
```

**Applied to:** All entities extending `AuditableEntity<TId>` or `SoftDeletableEntity<TId>` automatically. Also explicitly on `RefreshToken`, `AuditLog`, `TenantProvisioningJob`.

**NOT applied to:** `Permission` (uses `GlobalAuditableEntity`), `RolePermission`, all `Platform*` staff entities, `ImpersonationLog`.

---

## 1.5 Dual Authorization Systems

The codebase maintains **two completely separate** role/permission systems:

| Aspect | System A: Identity-based (Tenant Users) | System B: Platform Staff |
|--------|----------------------------------------|--------------------------|
| **User entity** | `IdentityUser` (ASP.NET) | `PlatformUser` (custom) |
| **Role entity** | `ApplicationRole` → `IdentityRole` | `PlatformRole` (custom) |
| **Permission entity** | `Permission` (global catalog) | `PlatformPermission` (custom) |
| **User-Role junction** | `AspNetUserRoles` (Identity) | `PlatformUserRole` (custom) |
| **Role-Permission junction** | `RolePermission` (custom) | `PlatformRolePermission` (custom) |
| **DB tables** | `AspNetRoles`, `AspNetUsers`, `AspNetUserRoles` + `Platform.Permissions`, `Platform.RolePermissions` | `Platform.PlatformUsers`, `Platform.PlatformRoles`, `Platform.PlatformPermissions`, `Platform.PlatformUserRoles`, `Platform.PlatformRolePermissions` |
| **Tenant-scoped** | Users not filtered (no `IHasTenantId` on `IdentityUser`). Roles global. Permissions global. RolePermissions global. | All entities are global (no `IHasTenantId`). |
| **Audit fields** | `Permission` has audit fields. `RolePermission` has none. | None of the entities have audit fields. |
| **Seeded at startup** | Yes — via `ApplicationDbContextInitialiser` | No — created via API commands only |
