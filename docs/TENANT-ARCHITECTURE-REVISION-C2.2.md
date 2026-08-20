# ARCHITECTURE REVISION — CONTROL PLANE / TENANT DATA PLANE

> **Date:** 2026-08-19  
> **Status:** Architecture Decision — REVISION  
> **Supersedes:** C2.1 Tenant Architecture Decision  
> **Scope:** Control Plane / Tenant Data Plane separation, identity model, authorization, lifecycle, Finbuckle role  
> **Constraint:** C1 is FIXED and must not be modified.

---

## 1. REVISED EXECUTIVE ARCHITECTURE

The C2.1 decision established **OPTION A**: Platform.Tenants is the single source of truth, Finbuckle is a runtime adapter.

C2.2 revises this decision to explicitly model two security domains:

- **Control Plane** — owned by the SaaS provider. Manages tenants, plans, billing, platform configuration.
- **Tenant Data Plane** — owned by each customer. Manages students, staff, invoices, CRM, and all business data.

The security boundary between them is absolute. A Platform Employee performing a Control Plane operation does NOT enter a tenant's data plane. A Tenant User performing a business operation does NOT have access to the Control Plane.

---

## 2. CONTROL PLANE

### Ownership

SaaS provider / company.

### Actors

| Actor | Examples |
|-------|----------|
| Platform Administrator | Superadmin, DevOps |
| Platform Operations | Support staff, onboarding |
| Platform Billing | Finance, accounts |
| Platform Sales | Sales representatives |

### Responsibilities

| Area | Operations |
|------|-----------|
| Tenant lifecycle | Create, activate, suspend, cancel tenants |
| Plan management | Create plans, edit plans, set prices, activate/deactivate plans |
| Feature management | Create features, assign features to plans |
| Subscription management | Assign plans, change plans, renew subscriptions, cancel subscriptions |
| Billing | View invoices, manage payments, apply credits |
| Platform RBAC | Manage platform users, platform roles, platform permissions |
| Platform configuration | Global settings, add-on catalog, referral configuration |
| Support access | Impersonate a tenant user (audit-logged, time-limited) |

### Authorization Mechanism

Platform operations are authorized via **platform permissions** embedded in the JWT. They do NOT require a tenant membership. They do NOT establish a tenant context.

```
Platform Permission -> Authorized -> Execute Operation -> Done
(No tenant context established)
```

---

## 3. TENANT DATA PLANE

### Ownership

Each customer / educational center.

### Actors

| Actor | Examples |
|-------|----------|
| Tenant Owner | Center owner, director |
| Tenant Admin | Center manager |
| Manager | Branch manager, department head |
| Teacher | Instructor, tutor |
| Accountant | Bookkeeper, financial staff |
| Employee | General staff |

### Responsibilities

| Area | Operations |
|------|-----------|
| Students | Create, view, edit, archive students |
| Staff | Manage teachers, employees |
| Branches | Create, configure branches |
| Attendance | Record attendance, view logs |
| Academic | Manage stages, years, curricula |
| Invoicing | Create invoices, payments, credits |
| CRM | Manage leads, pipeline, follow-ups |
| KPIs | View dashboards, reports |
| Settings | Tenant-specific configuration |

### Authorization Mechanism

Tenant operations are authorized via:

1. **Tenant membership** — verified by TenantGuardMiddleware.
2. **Tenant permissions** — embedded in JWT, scoped to the tenant.
3. **Tenant context** — established by `AuthorizeTenant()`, enabling EF query filter.

```
Tenant Membership (Active) -> Tenant Permission -> Authorized -> Tenant Context Established -> Execute Operation
```

---

## 4. SECURITY BOUNDARY

```
+---------------------------------------------------------------------------+
|                         SECURITY BOUNDARY                                 |
|                                                                           |
|  +---------------------------------+   +-----------------------------+   |
|  |       CONTROL PLANE             |   |     TENANT DATA PLANE       |   |
|  |       (Platform Scope)          |   |     (Tenant Scope)          |   |
|  |                                 |   |                             |   |
|  |  Platform permissions           |   |  Tenant permissions         |   |
|  |  No tenant context              |   |  Tenant context required    |   |
|  |  No TenantMembership required   |   |  TenantMembership required  |   |
|  |                                 |   |                             |   |
|  |  Operations:                    |   |  Operations:                |   |
|  |  - Create/suspend/cancel tenant |   |  - Students, Staff          |   |
|  |  - Manage plans/features        |   |  - Branches, Attendance     |   |
|  |  - Manage subscriptions         |   |  - Invoices, CRM            |   |
|  |  - Platform RBAC                |   |  - KPIs, Reports            |   |
|  |  - Billing overview             |   |  - Tenant settings          |   |
|  |                                 |   |                             |   |
|  |  Target TenantId is a           |   |  TenantId is the            |   |
|  |  TARGET RESOURCE, not context   |   |  OPERATING CONTEXT          |   |
|  +---------------------------------+   +-----------------------------+   |
|                                                                           |
|  INVARIANT: A single request cannot operate in both planes.               |
|  INVARIANT: A Platform Employee does NOT become a tenant member.          |
|  INVARIANT: A Tenant User does NOT get platform permissions.              |
+---------------------------------------------------------------------------+
```

---

## 5. PLATFORM USER MODEL

### Should Platform Employees have TenantId?

**NO.**

Platform employees are **platform-scoped identities**. They do not belong to any tenant. Their authorization is derived entirely from platform permissions and platform roles.

### Why NOT introduce a fake TenantId for Platform Employees:

| Problem | Explanation |
|---------|------------|
| **Security risk** | A fake TenantId would leak into the EF query filter, restricting the platform user's view to one tenant's data. A PlatformAdmin must see ALL tenants. |
| **Authorization confusion** | If `User.TenantId` exists, developers may use it for authorization. But a PlatformAdmin's TenantId is meaningless — their authorization comes from platform permissions. |
| **Cross-tenant operations** | Platform operations (view all tenants, manage plans) are inherently cross-tenant. A TenantId on the user would contradict this. |
| **Data integrity** | `TenantInterceptor` stamps `ICurrentTenant.TenantId` on new entities. If a PlatformAdmin has a fake TenantId, new platform entities would be incorrectly scoped. |
| **Existing design** | The current codebase correctly uses `ICurrentTenant.TenantId` (empty until `AuthorizeTenant()`) for platform-scoped requests. Introducing a user TenantId would break this. |

### The correct model:

```
ApplicationUser
    |
    +-- Platform permissions (via PlatformRole -> PlatformRolePermission -> Permission)
    |   - Stored in JWT as "Permission" claims
    |   - Checked by [HasPermission] attribute
    |   - No TenantId involved
    |
    +-- TenantMembership (optional, for users who also access tenant data)
        - Stored in TenantMemberships table
        - Checked by TenantGuardMiddleware
        - Establishes tenant context
```

---

## 6. APPLICATION USER MODEL

### Can one ApplicationUser represent both platform users and tenant users?

**YES.** A single `ApplicationUser` (ASP.NET Identity) represents all users. The distinction is in **authorization**, not identity.

### Conceptual Model

```
ApplicationUser (single table, single identity)
    |
    +-- Authentication
    |   - JWT contains UserId, Roles, Permissions
    |   - No tenant claim in JWT (design decision, C1)
    |
    +-- Platform Authorization (for Control Plane)
    |   - PlatformRole -> PlatformRolePermission -> Permission
    |   - Permissions like "Tenants.Create", "Plans.Read"
    |   - Authorized by [HasPermission] attribute
    |   - TenantGuardMiddleware classifies as PlatformScope -> passes through
    |
    +-- Tenant Authorization (for Data Plane)
        - TenantMembership (UserId, TenantId, Status)
        - Permissions like "Students.Create", "Invoices.Read"
        - Authorized by TenantGuardMiddleware -> membership check -> context establishment
```

### Why NOT separate user tables:

| Approach | Problem |
|----------|---------|
| Separate PlatformUser and TenantUser tables | A support employee needs both platform access AND tenant impersonation. Two identities means two logins, two JWTs, two auth flows. |
| PlatformUser table + TenantMembership | The current codebase already has this. `PlatformUser` is a platform staff entity. `ApplicationUser` is the Identity user. They coexist. |
| Single ApplicationUser with roles | The recommended approach. One login, one JWT. Platform permissions and tenant memberships are orthogonal concerns on the same identity. |

### Important distinction:

The current codebase has **two separate user concepts**:

1. **`ApplicationUser`** (ASP.NET Identity) — the authentication identity. Used for login, JWT, roles.
2. **`PlatformUser`** (domain entity) — the platform staff record. Has its own ID, links to `ApplicationUser` via an implied relationship.

This dual model is acceptable. `ApplicationUser` is the authentication identity. `PlatformUser` is the business record for platform staff. `TenantMembership` links `ApplicationUser` to tenants for tenant access.

---

## 7. TENANT MEMBERSHIP

### Purpose

`TenantMembership` represents the relationship:

```
User -> Membership -> Tenant
```

It answers: "Is this user an active member of this tenant?"

### Current State

| Property | Type | Source |
|----------|------|--------|
| `UserId` | `string` | FK to `AspNetUsers.Id` |
| `TenantId` | `string` | FK to `Platform.Tenants.TenantId` (via Guid.ToString()) |
| `Status` | `TenantMembershipStatus` | Active, Invited, Suspended, Revoked |
| `JoinedAtUtc` | `DateTimeOffset` | Auto-set on creation |

### Platform Operations Do NOT Require TenantMembership

When a Platform Employee performs a Control Plane operation:

```
Platform Employee -> [HasPermission(Tenants.Create)] -> TenantGuardMiddleware
    |
    +-- IsPlatformScoped(Tenants.Create) -> TRUE
    |
    +-- Pass through WITHOUT tenant context
        - No TenantMembership check
        - No AuthorizeTenant() call
        - ICurrentTenant.TenantId remains empty
        - EF query filter returns nothing (correct for platform ops)
```

The employee does NOT need to be a member of the target tenant. The employee does NOT receive a tenant context. The operation is authorized purely by the platform permission.

### PlatformAdmin Does NOT Bypass Tenant Authorization

This is critical. `PlatformAdmin` is a platform role. It grants platform permissions. It does NOT grant automatic access to tenant-scoped business data.

| Scenario | Behavior |
|----------|----------|
| PlatformAdmin calls `GET /api/platform/tenants` | **Allowed.** `Tenants.Read` is a platform permission. PlatformScope. No tenant context needed. |
| PlatformAdmin calls `GET /api/students?tenant=A` | **Blocked.** `Students.Read` is a tenant-scoped permission. TenantGuardMiddleware requires active TenantMembership in tenant A. PlatformAdmin is NOT automatically a member. |
| PlatformAdmin wants to view tenant A's students | **Must use impersonation** (see Section 12). Creates an audit-logged, time-limited session. |

---

## 8. PLATFORM PERMISSIONS

### Control Plane Permissions

| Permission | Module | Description |
|-----------|--------|-------------|
| `Tenants.Create` | Tenant lifecycle | Create new tenants |
| `Tenants.Read` | Tenant lifecycle | View tenant list and details |
| `Tenants.Update` | Tenant lifecycle | Update tenant metadata, suspend, reactivate |
| `Tenants.Delete` | Tenant lifecycle | Cancel tenants |
| `Plans.Create` | Plan management | Create new subscription plans |
| `Plans.Read` | Plan management | View plan catalog |
| `Plans.Update` | Plan management | Edit plan details, limits |
| `Plans.Delete` | Plan management | Deactivate plans |
| `Features.Create` | Feature management | Create feature definitions |
| `Features.Read` | Feature management | View feature catalog |
| `Features.Update` | Feature management | Edit feature details |
| `Features.Delete` | Feature management | Remove features |
| `TenantPlans.Create` | Subscription management | Assign plans to tenants |
| `TenantPlans.Read` | Subscription management | View tenant subscriptions |
| `TenantPlans.Update` | Subscription management | Renew, change plans |
| `TenantPlans.Delete` | Subscription management | Cancel subscriptions |
| `PlatformUsers.Create` | Platform RBAC | Create platform staff accounts |
| `PlatformUsers.Read` | Platform RBAC | View platform staff |
| `PlatformUsers.Update` | Platform RBAC | Edit platform staff |
| `PlatformUsers.Delete` | Platform RBAC | Remove platform staff |
| `PlatformRoles.Create` | Platform RBAC | Create platform roles |
| `PlatformRoles.Read` | Platform RBAC | View platform roles |
| `PlatformRoles.Update` | Platform RBAC | Edit platform roles |
| `PlatformRoles.Delete` | Platform RBAC | Remove platform roles |
| `PlatformPermissions.Read` | Platform RBAC | View permission catalog |
| `AddOnCatalogs.Create` | Add-on management | Create add-on catalog entries |
| `AddOnCatalogs.Read` | Add-on management | View add-on catalog |
| `AddOnCatalogs.Update` | Add-on management | Edit add-on catalog |

### How Platform Permissions Differ from Tenant Permissions

| Aspect | Platform Permission | Tenant Permission |
|--------|-------------------|-------------------|
| **Scope** | Cross-tenant, platform-global | Single tenant, tenant-partitioned |
| **Requires TenantMembership** | No | Yes |
| **Establishes TenantContext** | No | Yes |
| **EF Query Filter** | Not applied (empty TenantId) | Applied (authorized TenantId) |
| **Examples** | `Tenants.Create`, `Plans.Read` | `Students.Create`, `Invoices.Read` |
| **Authorized by** | `[HasPermission]` + JWT claim | `[HasPermission]` + JWT claim + TenantGuardMiddleware membership check |
| **Data access** | Platform-level tables (unfiltered) | Tenant-scoped tables (filtered by TenantId) |

---

## 9. TENANT PERMISSIONS

### Data Plane Permissions

| Permission | Module | Description |
|-----------|--------|-------------|
| `Students.Create` | Student management | Create student records |
| `Students.Read` | Student management | View student records |
| `Students.Update` | Student management | Edit student records |
| `Students.Delete` | Student management | Archive/delete students |
| `Branches.Create` | Branch management | Create branches |
| `Branches.Read` | Branch management | View branches |
| `Branches.Update` | Branch management | Edit branches |
| `Branches.Delete` | Branch management | Delete branches |
| `AttendanceLogs.Create` | Attendance | Record attendance |
| `AttendanceLogs.Read` | Attendance | View attendance logs |
| `AcademicStages.Create` | Academic | Create academic stages |
| `AcademicStages.Read` | Academic | View academic stages |
| `AcademicStages.Update` | Academic | Edit academic stages |
| `AcademicYears.Create` | Academic | Create academic years |
| `AcademicYears.Read` | Academic | View academic years |
| `AcademicYears.Update` | Academic | Edit academic years |
| `TenantCRMLeads.Create` | CRM | Create CRM leads |
| `TenantCRMLeads.Read` | CRM | View CRM leads |
| `TenantCRMLeads.Update` | CRM | Edit CRM leads |
| `TenantCRMLeads.Delete` | CRM | Delete CRM leads |
| `Invoices.Create` | Billing | Create invoices |
| `Invoices.Read` | Billing | View invoices |
| `Invoices.Update` | Billing | Edit invoices |
| `Invoices.Delete` | Billing | Delete invoices |
| `TenantCredits.Create` | Credits | Create tenant credits |
| `TenantCredits.Read` | Credits | View tenant credits |
| `TenantAddOns.Create` | Add-ons | Subscribe to add-ons |
| `TenantAddOns.Read` | Add-ons | View subscribed add-ons |
| `TenantAddOns.Update` | Add-ons | Manage add-on settings |
| `TenantLimitOverrides.Create` | Limits | Override tenant limits |
| `TenantLimitOverrides.Read` | Limits | View limit overrides |
| `TenantReferralCodes.Create` | Referrals | Create referral codes |
| `TenantReferralCodes.Read` | Referrals | View referral codes |
| `TenantReferrals.Create` | Referrals | Create referrals |
| `TenantReferrals.Read` | Referrals | View referrals |
| `TenantProvisioningJobs.Create` | Provisioning | Create provisioning jobs |
| `TenantProvisioningJobs.Read` | Provisioning | View provisioning jobs |
| `TenantProvisioningJobs.Update` | Provisioning | Update provisioning jobs |

---

## 10. PLATFORM OPERATIONS WITH TENANTID

### Target Resource vs Operating Context

A Platform operation may contain a `TenantId` as a **target resource**. This is fundamentally different from a tenant-scoped operation where `TenantId` is the **operating context**.

### Example

```
POST /api/platform/tenants/{tenantId}/suspend
Authorization: Bearer <JWT with Tenants.Update permission>
```

In this request:

| Concept | Value | Meaning |
|---------|-------|---------|
| `TenantId` in URL | `{tenantId}` | **TARGET RESOURCE** — which tenant to suspend |
| `ICurrentTenant.TenantId` | `""` (empty) | **OPERATING CONTEXT** — no tenant context established |
| `TenantMembership` | Not checked | Platform operation, not tenant operation |
| Authorization | `Tenants.Update` permission | Platform permission, not tenant permission |

### Why this distinction matters:

```
Platform operation:
    Platform Employee -> Platform Permission -> Target TenantId -> Operation executes
    (No tenant context, no membership, no CurrentTenant)

Tenant operation:
    Tenant User -> TenantMembership -> TenantContext -> Operation executes
    (Tenant context established, EF filter active, CurrentTenant set)
```

A single request CANNOT be both. The `HasPermission` attribute on the endpoint and the `PlatformScope.IsPlatformScoped()` classification determine which path is taken.

---

## 11. PLATFORM USER OPERATING ON A TENANT

### Scenario

Platform Operations employee suspends Tenant A.

### Flow

```
1. Employee authenticates
   -> JWT contains: UserId, Roles=[PlatformAdmin], Permissions=[Tenants.Update, ...]

2. Employee calls POST /api/platform/tenants/{tenantA-id}/suspend
   -> TenantGuardMiddleware reads HasPermission(Tenants.Update)
   -> IsPlatformScoped(Tenants.Update) -> TRUE
   -> Pass through, NO tenant context established

3. Handler executes
   -> Reads Platform.Tenants where TenantId = {tenantA-id}
   -> Calls tenant.Suspend(reason)
   -> Updates Platform.Tenants.LifecycleStatus = Suspended
   -> Syncs CenterixTenantInfo.IsActive = false
   -> Writes audit log

4. Result
   -> Tenant A is suspended
   -> Employee is NOT a member of Tenant A
   -> Employee did NOT receive TenantMembership
   -> Employee did NOT change their CurrentTenant
   -> Operation was authorized through Platform permissions only
```

### What did NOT happen:

- The employee did NOT get a TenantMembership for Tenant A.
- The employee did NOT establish a tenant context.
- The employee did NOT gain access to Tenant A's students, invoices, or CRM data.
- The employee's `ICurrentTenant.TenantId` remains empty.

---

## 12. PLATFORM ACCESS TO TENANT BUSINESS DATA

### Should Platform Employees Access Tenant Business Data?

**Not directly. Not automatically.**

The current architecture correctly prevents this:

- `Students.Read` is NOT in `PlatformScope.PermissionCodes`.
- TenantGuardMiddleware requires active TenantMembership for tenant-scoped operations.
- PlatformAdmin does NOT bypass this check.

### When Platform Access IS Needed

| Scenario | Current Behavior | Recommended Approach |
|----------|-----------------|---------------------|
| Support ticket requires viewing tenant data | No mechanism | **Impersonation** (audit-logged, time-limited) |
| Billing team needs to view invoices | No mechanism | **Impersonation** or dedicated reporting endpoint |
| Onboarding team needs to configure tenant | No mechanism | **Impersonation** |
| Debugging production issue in tenant | No mechanism | **Impersonation** |

### Impersonation Model

The codebase already has `ImpersonationLog` domain entity (append-only audit log). The full model:

```
ImpersonationLog
    +-- PlatformUserId      (who is impersonating)
    +-- TenantId            (which tenant)
    +-- TargetUserId        (which user within the tenant)
    +-- StartedAt           (when started)
    +-- EndedAt             (when ended)
    +-- Reason              (business justification)
    +-- IPAddress           (caller IP)
```

#### Impersonation Requirements

| Requirement | Implementation |
|-------------|---------------|
| Explicit permission | `Support.Impersonate` permission (must be added to PlatformScope) |
| Audit reason | Required string field, logged with the session |
| Time limitation | Maximum session duration (e.g., 30 minutes), auto-expire |
| Audit logging | Append-only `ImpersonationLog` table, never deleted |
| Tenant visibility | Tenant admin can see active impersonation sessions (future) |
| Exit mechanism | Explicit end-session endpoint, auto-expire on timeout |
| No silent privilege escalation | Impersonation creates a separate JWT with limited tenant permissions, NOT the impersonator's full platform permissions |

#### Impersonation Flow

```
1. Platform Employee has Support.Impersonate permission
2. Employee calls POST /api/platform/impersonate
   - TargetTenantId: "tenant-A"
   - TargetUserId: "user-123"
   - Reason: "Investigating billing discrepancy for ticket #456"
3. System creates ImpersonationLog entry
4. System generates short-lived impersonation JWT
   - Contains TargetUserId, TargetTenantId
   - Contains ONLY the target user's tenant permissions
   - Does NOT contain platform permissions
   - Has short expiry (30 minutes)
5. Employee uses impersonation JWT to access tenant data
   - TenantGuardMiddleware sees active TenantMembership for target user
   - Tenant context is established for target user
   - All operations are logged under target user's identity
6. Employee calls POST /api/platform/impersonate/end
   - ImpersonationLog.EndedAt is set
   - Impersonation JWT is invalidated
```

---

## 13. PLANS

### Plans Are Platform-Scoped

Plans are **global catalog entities**. They are NOT tenant-scoped.

```
Plan : GlobalAuditableEntity<int>
    +-- NOT IHasTenantId
    +-- NOT filtered by tenant query filter
    +-- Visible to ALL tenants (read) and Platform employees (write)
    +-- Managed exclusively through Control Plane
```

### Why Plans Must NOT Have TenantId:

| Reason | Explanation |
|--------|------------|
| **Shared catalog** | All tenants choose from the same plan catalog. Plans are not per-tenant. |
| **Platform pricing** | The SaaS provider sets prices. Individual tenants do not have their own plan definitions. |
| **Consistency** | If Plan had TenantId, Tenant A could have a "Professional" plan with different limits than Tenant B's "Professional" plan. This breaks the catalog model. |
| **Query simplicity** | `GlobalAuditableEntity<int>` has no tenant filter. Plans are queried globally. |
| **Current design** | `Plan` already extends `GlobalAuditableEntity<int>`. This is correct. |

### Features Are Platform-Scoped

Features are also global catalog entities.

```
Feature : GlobalAuditableEntity<int>
    +-- NOT IHasTenantId
    +-- Module, Code, Description
    +-- Linked to Plans via PlanFeature junction
```

### PlanFeature Is Platform-Scoped

```
PlanFeature : GlobalAuditableEntity<int>
    +-- FK PlanId -> Plan.Id
    +-- FK FeatureId -> Feature.Id
    +-- IsEnabled flag
    +-- Defines which features are included in which plans
```

---

## 14. PLAN PRICING MODEL

### Current State

`Plan.MonthlyPrice` is a single decimal field on the Plan entity. This supports one price per plan.

### Recommended Conceptual Model

For future flexibility, plan pricing should support multiple currencies and billing periods:

```
Plan
    |
    +-- PlanPrice (1:N)
        +-- Currency       (e.g., "USD", "EUR", "SAR")
        +-- BillingPeriod  (Monthly, Quarterly, Yearly)
        +-- Amount         (decimal)
        +-- EffectiveFrom  (datetime)
        +-- EffectiveTo    (datetime, nullable)
        +-- IsActive       (bool)
```

### How Platform Employees Manage Prices

```
Platform Employee
    |
    +-- Plans.Update permission
    |
    +-- Access: POST /api/platform/plans/{planId}/prices
    |   Body: { Currency: "USD", BillingPeriod: "Monthly", Amount: 49.00 }
    |
    +-- Result: PlanPrice created for Professional Plan, USD, Monthly, $49
```

No TenantId is required. Pricing is a platform concern.

### Current Simplification

For the current MVP, `Plan.MonthlyPrice` as a single field is acceptable. The `PlanPrice` model is the target for when multi-currency or multi-period billing is needed.

---

## 15. SUBSCRIPTION (TenantPlan)

### TenantPlan IS Tenant-Scoped

```
TenantPlan : AuditableEntity<Guid> (implements IHasTenantId)
    |
    +-- FK TenantId -> Platform.Tenants.TenantId (via Guid.ToString())
    +-- FK PlanId -> Plan.Id (global plan reference)
    +-- SnapshotPrice (price at time of subscription)
    +-- StartsAt
    +-- EndsAt
    +-- AutoRenew
    +-- Status (Active, Expired, Cancelled, Suspended)
```

### Why TenantId Belongs Here

| Reason | Explanation |
|--------|------------|
| **Per-tenant subscription** | Each tenant subscribes to exactly one plan at a time. The TenantPlan links a tenant to their plan. |
| **Tenant-scoped data** | TenantPlan is filtered by the tenant query filter. Tenant A cannot see Tenant B's subscription. |
| **Subscription is a tenant concern** | Subscriptions are owned by tenants. The platform manages plans; tenants manage their subscriptions. |
| **Billing alignment** | Invoices reference TenantPlan. Billing is per-tenant. |

### Example

```
Tenant A -> Professional Plan -> Monthly -> Active -> EndsAt: 2026-09-19
Tenant B -> Basic Plan -> Yearly -> Active -> EndsAt: 2027-08-19
Tenant C -> Enterprise Plan -> Monthly -> Suspended -> EndsAt: 2026-08-01
```

---

## 16. TENANT LIFECYCLE VS SUBSCRIPTION LIFECYCLE

### These Are INDEPENDENT State Machines

```
TENANT LIFECYCLE                    SUBSCRIPTION LIFECYCLE
(Tenant.LifecycleStatus)            (TenantPlan.Status)

+----------------+                 +----------------+
| Provisioning   |                 |     Trial      |
+-------+--------+                 +-------+--------+
        |                                    |
        v                                    v
+----------------+                 +----------------+
|     Active     |<--------------->|     Active     |
+-------+--------+                 +-------+--------+
        |                                    |
        |                             +------+--------+
        |                             |    PastDue    |
        |                             +------+--------+
        |                                    |
        v                                    v
+----------------+                 +----------------+
|   Suspended    |                 |    Expired     |
+-------+--------+                 +-------+--------+
        |                                    |
        v                                    v
+----------------+                 +----------------+
|   Cancelled    |                 |   Cancelled    |
+----------------+                 +----------------+
```

### Scenario Matrix

| Tenant Lifecycle | Subscription Status | Behavior |
|-----------------|-------------------|----------|
| Active | Active | Normal operation. TenantGuard allows. |
| Active | PastDue | Tenant is operational but payment is overdue. Grace period. TenantGuard allows. |
| Active | Expired | Subscription ended. Background job suspends tenant. TenantGuard blocks (402). |
| Active | Cancelled | Subscription cancelled but tenant still operational. Grace period or immediate suspension based on business rules. |
| Suspended | Active | Tenant suspended by admin (not billing). Subscription still valid. TenantGuard blocks (403). |
| Suspended | Expired | Both suspended and expired. TenantGuard blocks. Reactivation requires both reactivation AND renewal. |
| Cancelled | Active | Tenant cancelled. Subscription should be cancelled too. TenantGuard blocks (403). |
| Cancelled | Cancelled | Both cancelled. Terminal state. |

### Why NOT introduce Tenant.Status = Expired

| Problem | Explanation |
|---------|------------|
| **Confusion** | Expiration is a subscription concern. The tenant itself does not "expire" — its subscription does. |
| **State machine complexity** | Adding Expired to tenant lifecycle creates ambiguity: is the tenant expired or suspended? |
| **Recovery path** | Expired subscription -> renew subscription -> tenant becomes Active again. If Tenant.Status = Expired, you need two state transitions. |
| **Single responsibility** | Tenant lifecycle handles operational state. Subscription lifecycle handles payment/access state. |

---

## 17. TENANT SOURCE OF TRUTH

### Confirmed

```
Platform.Tenants = CANONICAL BUSINESS TENANT
```

It owns:

| Concern | Owner |
|---------|-------|
| Tenant Identity | `Tenant.Id` (Guid) |
| Tenant Lifecycle | `Tenant.LifecycleStatus` |
| Tenant Business Metadata | `Tenant.DisplayName`, `Tenant.Slug`, etc. |
| Tenant Ownership | `Tenant.OwnerEmail`, etc. |
| Tenant Configuration | `Tenant.IsolationMode`, `Tenant.DatabaseServer` |
| Tenant Operational Status | `Tenant.IsActive` |
| Tenant Expiration | `Tenant.ValidUpTo` (derived from subscription) |

### Finbuckle Does NOT Become the Business Source of Truth

`CenterixTenantInfo` is a **derived runtime cache**. It contains the minimum data needed for Finbuckle to resolve tenants on HTTP requests. It is never written to directly by business operations.

---

## 18. FINBUCKLE ROLE

### Exact Role

**Runtime tenant resolution and request context provider.**

Finbuckle answers exactly one question per request:

> "Which tenant did this HTTP request target?"

It does NOT answer:

- "Is this tenant active?" (TenantGuard reads CenterixTenantInfo.IsActive, which is derived)
- "Is this tenant's subscription valid?" (TenantGuard reads CenterixTenantInfo.ValidUpTo, which is derived)
- "Is this user a member of this tenant?" (TenantGuard checks TenantMemberships table)
- "What is this tenant's lifecycle state?" (Domain handlers read Platform.Tenants.LifecycleStatus)

### TenantRegistry Role

| Role | Yes/No | Explanation |
|------|--------|-------------|
| Runtime resolution store | **Yes** | Finbuckle's `EFCoreStore` reads from this table to resolve tenant from header/host. |
| Infrastructure adapter | **Yes** | It is a thin adapter between Finbuckle's `ITenantInfo` interface and the database. |
| Cache | **Yes** | It caches the minimum tenant data needed for resolution. Derived from Platform.Tenants. |
| Projection | **Yes** | It is a materialized view of Platform.Tenants, projected for Finbuckle's consumption. |
| Source of truth | **No** | Never. Platform.Tenants is the source of truth. |

### Allowed Fields on CenterixTenantInfo

| Field | Allowed | Source |
|-------|---------|--------|
| `Id` | **Yes** | `Tenant.Id.ToString()` — runtime tenant identity |
| `Identifier` | **Yes** | Same as Id or a slug |
| `Name` | **Yes** | `Tenant.DisplayName` — for display in Finbuckle context |
| `ConnectionString` | **Yes** | `Tenant.ConnectionStringRef` — for multi-tenant DB routing |
| `IsActive` | **Yes** | `Tenant.IsActive` — derived, for TenantGuard fast-path check |
| `ValidUpTo` | **Yes** | `Tenant.ValidUpTo` — derived, for TenantGuard expiry check |
| `Slug` | **Yes** | `Tenant.Slug` — for host-based resolution |
| `Subdomain` | **Yes** | `Tenant.Subdomain` — for host-based resolution |
| `Email` | **Optional** | Owner email, useful for display |
| `FirstName` | **Optional** | Owner name, useful for display |
| `LastName` | **Optional** | Owner name, useful for display |
| `Status` | **Yes** | `Tenant.LifecycleStatus` as byte — for debugging |
| `TrialEndsAt` | **Optional** | For display/debugging |
| `CreatedAt` | **Optional** | For display/debugging |

### Fields That Must NOT Be on CenterixTenantInfo

| Field | Reason |
|-------|--------|
| Business lifecycle logic | Lifecycle transitions happen in domain handlers, not Finbuckle |
| Subscription details | Subscription is managed through TenantPlan, not Finbuckle |
| Plan information | Plans are global catalog, not per-tenant in Finbuckle |
| Billing data | Billing is managed through Invoices, not Finbuckle |

---

## 19. TENANTID RULES

### Canonical Identity

```
Tenant.Id = Guid          <- CANONICAL, AUTHORITATIVE
```

### Boundary Representation

```
CenterixTenantInfo.Id = string   <- Guid.ToString() at infrastructure boundary
TenantMembership.TenantId = string <- Guid.ToString() at domain boundary
TenantPlan.TenantId = string?     <- Guid.ToString() via IHasTenantId
ICurrentTenant.TenantId = string  <- Guid.ToString() at runtime context
```

### Conversion Rules

| From | To | Method | Notes |
|------|----|--------|-------|
| `Guid` | `string` | `tenant.Id.ToString()` | Deterministic, reversible |
| `string` | `Guid` | `Guid.Parse(string)` | Fails if string is not a valid Guid |
| `string` | `Guid` | `Guid.TryParse(string)` | Safe conversion, returns bool |

### Guarantee

```
tenant.Id.ToString() -> string value A
Guid.Parse(A) -> tenant.Id

No mapping table. No ambiguity. No orphans.
ONE identity. TWO representations.
```

### How This Prevents Cross-Tenant Accidents

| Entity | TenantId Type | Source | Guarantee |
|--------|--------------|--------|-----------|
| `Tenant` | `Guid` | Domain entity, canonical | THE source |
| `TenantMembership` | `string` | `tenant.Id.ToString()` | Same tenant |
| `TenantPlan` | `string?` | `tenant.Id.ToString()` via `IHasTenantId` | Same tenant |
| `CenterixTenantInfo` | `string` | `tenant.Id.ToString()` | Same tenant |
| `ICurrentTenant.TenantId` | `string` | `AuthorizeTenant()` -> `ResolvedTenantId` | Same tenant (after membership check) |
| `EF Query Filter` | `string` | `ICurrentTenant.TenantId` | Same tenant |

All paths converge to the same `Guid`, represented as `string` at boundaries. There is no alternative identity space that could drift.

---

## 20. TENANT OPERATIONS — AUTHORIZATION AND EXECUTION

### Create Tenant

| Aspect | Detail |
|--------|--------|
| **Who can perform** | Platform Employee with `Tenants.Create` permission |
| **Authorization** | Platform permission (PlatformScope) |
| **Requires TenantMembership** | No |
| **Establishes CurrentTenant** | No |
| **Uses TenantId as target** | No (tenant does not exist yet) |
| **Endpoint** | `POST /api/platform/tenants` |
| **Effect** | Creates `Platform.Tenants` + `CenterixTenantInfo` + initial `TenantMembership` for owner |

### Activate Tenant

| Aspect | Detail |
|--------|--------|
| **Who can perform** | Platform Employee with `Tenants.Update` permission |
| **Authorization** | Platform permission (PlatformScope) |
| **Requires TenantMembership** | No |
| **Establishes CurrentTenant** | No |
| **Uses TenantId as target** | Yes — `POST /api/platform/tenants/{tenantId}/activate` |
| **Effect** | `LifecycleStatus` -> Active, `CenterixTenantInfo.IsActive` -> true |

### Suspend Tenant

| Aspect | Detail |
|--------|--------|
| **Who can perform** | Platform Employee with `Tenants.Update` permission |
| **Authorization** | Platform permission (PlatformScope) |
| **Requires TenantMembership** | No |
| **Establishes CurrentTenant** | No |
| **Uses TenantId as target** | Yes — `POST /api/platform/tenants/{tenantId}/suspend` |
| **Effect** | `LifecycleStatus` -> Suspended, `CenterixTenantInfo.IsActive` -> false |

### Cancel Tenant

| Aspect | Detail |
|--------|--------|
| **Who can perform** | Platform Employee with `Tenants.Delete` permission |
| **Authorization** | Platform permission (PlatformScope) |
| **Requires TenantMembership** | No |
| **Establishes CurrentTenant** | No |
| **Uses TenantId as target** | Yes — `DELETE /api/platform/tenants/{tenantId}` |
| **Effect** | `LifecycleStatus` -> Cancelled, `IsActive` -> false, `CenterixTenantInfo.IsActive` -> false |

### Renew Subscription

| Aspect | Detail |
|--------|--------|
| **Who can perform** | Platform Employee with `TenantPlans.Update` permission |
| **Authorization** | Tenant-scoped (NOT in PlatformScope) |
| **Requires TenantMembership** | Yes (Platform Employee must have membership OR use impersonation) |
| **Establishes CurrentTenant** | Yes |
| **Uses TenantId as target** | Yes — but via tenant context |
| **Effect** | `TenantPlan.EndsAt` updated, `Tenant.ValidUpTo` derived and synced |

### Assign Plan

| Aspect | Detail |
|--------|--------|
| **Who can perform** | Platform Employee with `TenantPlans.Create` permission |
| **Authorization** | Tenant-scoped |
| **Requires TenantMembership** | Yes (or impersonation) |
| **Establishes CurrentTenant** | Yes |
| **Uses TenantId as target** | Yes — but via tenant context |
| **Effect** | New `TenantPlan` created, `Tenant.CurrentPlanId` updated |

### Change Plan

| Aspect | Detail |
|--------|--------|
| **Who can perform** | Platform Employee with `TenantPlans.Update` permission |
| **Authorization** | Tenant-scoped |
| **Requires TenantMembership** | Yes (or impersonation) |
| **Establishes CurrentTenant** | Yes |
| **Uses TenantId as target** | Yes — but via tenant context |
| **Effect** | Current `TenantPlan` cancelled, new `TenantPlan` created |

### Change Plan Price

| Aspect | Detail |
|--------|--------|
| **Who can perform** | Platform Employee with `Plans.Update` permission |
| **Authorization** | Platform permission (PlatformScope) |
| **Requires TenantMembership** | No |
| **Establishes CurrentTenant** | No |
| **Uses TenantId as target** | No (Plans are global) |
| **Effect** | `Plan.MonthlyPrice` updated (or new `PlanPrice` record created) |

---

## 21. SECURITY INVARIANTS

### Final Architecture Guarantees

| # | Invariant | Enforcement |
|---|-----------|-------------|
| 1 | Platform users do NOT require TenantMembership for platform operations | `PlatformScope.IsPlatformScoped()` bypasses membership check in TenantGuardMiddleware |
| 2 | Platform users do NOT need TenantId as their own tenant | `ICurrentUser` has no TenantId property. Platform users are platform-scoped identities. |
| 3 | Tenant users cannot perform platform operations | Platform permissions are NOT granted to tenant roles (`TenantAdmin`, `TenantUser`) |
| 4 | PlatformAdmin does NOT automatically bypass tenant-scoped authorization | `PlatformAdmin` grants platform permissions only. Tenant-scoped operations still require TenantMembership. |
| 5 | TenantMembership controls tenant-user access | TenantGuardMiddleware checks `TenantMemberships` table for active membership before allowing tenant-scoped operations |
| 6 | Plans are global/platform-scoped | `Plan : GlobalAuditableEntity<int>` — no `IHasTenantId`, no tenant filter |
| 7 | Features are global/platform-scoped | `Feature : GlobalAuditableEntity<int>` — no `IHasTenantId`, no tenant filter |
| 8 | Subscriptions are tenant-scoped | `TenantPlan : AuditableEntity<Guid>` — implements `IHasTenantId`, filtered by tenant |
| 9 | Tenant lifecycle is independent from subscription lifecycle | `Tenant.LifecycleStatus` and `TenantPlan.Status` are separate state machines |
| 10 | Finbuckle does NOT become the business tenant Source of Truth | `CenterixTenantInfo` is derived from `Platform.Tenants` in the same transaction |
| 11 | Tenant identity is canonical and cannot drift between systems | Single `Guid` identity, string representation at boundaries, deterministic mapping |
| 12 | Tenant lifecycle enforcement cannot depend on stale duplicated state | Same-transaction dual-write ensures `CenterixTenantInfo.IsActive` is always consistent with `Tenant.LifecycleStatus` |

---

## 22. ASCII ARCHITECTURE DIAGRAM

```
+-----------------------------------------------------------------------------+
|                          CENTERIX SaaS ARCHITECTURE                         |
+-----------------------------------------------------------------------------+
|                                                                             |
|  +-----------------------------------------------------------------------+  |
|  |                        HTTP REQUEST                                    |  |
|  |   Header: tenant: tenant-A-id  (or Host: tenant-a.centerix.com)      |  |
|  +--------------------------------+--------------------------------------+  |
|                                   |                                         |
|                                   v                                         |
|  +-----------------------------------------------------------------------+  |
|  |                    FINBUCKLE RESOLUTION                               |  |
|  |   Reads Platform.TenantRegistry (derived cache)                       |  |
|  |   Resolves CenterixTenantInfo from header/host                        |  |
|  |   Sets IMultiTenantContext                                            |  |
|  +--------------------------------+--------------------------------------+  |
|                                   |                                         |
|                                   v                                         |
|  +-----------------------------------------------------------------------+  |
|  |                    AUTHENTICATION                                      |  |
|  |   JWT Bearer token validated                                          |  |
|  |   Claims: UserId, Roles, Permissions                                  |  |
|  |   NO tenant claim (design decision, C1)                               |  |
|  +--------------------------------+--------------------------------------+  |
|                                   |                                         |
|                                   v                                         |
|  +-----------------------------------------------------------------------+  |
|  |                    AUTHORIZATION (UseAuthorization)                    |  |
|  |   PermissionPolicyProvider resolves [HasPermission] to claim check    |  |
|  |   Requires authenticated user (fallback policy)                        |  |
|  +--------------------------------+--------------------------------------+  |
|                                   |                                         |
|                                   v                                         |
|  +-----------------------------------------------------------------------+  |
|  |                    TENANT GUARD MIDDLEWARE                              |  |
|  |                                                                       |  |
|  |   IsPlatformScoped?                                                   |  |
|  |   +-- YES --> Pass through (no tenant context)                        |  |
|  |   |           Platform operations execute without tenant context       |  |
|  |   |           ICurrentTenant.TenantId remains empty                   |  |
|  |   |           EF query filter returns nothing (correct)               |  |
|  |   |                                                                   |  |
|  |   +-- NO --> Tenant-scoped path:                                      |  |
|  |              1. Is tenant resolved? (Finbuckle)                        |  |
|  |              2. Is user an ACTIVE member? (TenantMemberships)          |  |
|  |              3. AuthorizeTenant() -> establish verified context        |  |
|  |              4. Is tenant IsActive? (CenterixTenantInfo, derived)      |  |
|  |              5. Is tenant not expired? (CenterixTenantInfo, derived)   |  |
|  +--------------------------------+--------------------------------------+  |
|                                   |                                         |
|                    +--------------+--------------+                          |
|                    |                             |                           |
|                    v                             v                           |
|  +--------------------------+   +------------------------------------+     |
|  |    CONTROL PLANE         |   |      TENANT DATA PLANE             |     |
|  |    (Platform Scope)      |   |      (Tenant Scope)                |     |
|  |                          |   |                                    |     |
|  |  No tenant context       |   |  Tenant context established       |     |
|  |  EF filter: empty        |   |  EF filter: active TenantId       |     |
|  |  Reads: Platform tables  |   |  Reads: Tenant-scoped tables      |     |
|  |  Writes: Platform tables |   |  Writes: Tenant-scoped tables     |     |
|  +--------------------------+   +------------------------------------+     |
|                                                                             |
+-----------------------------------------------------------------------------+

DATA FLOW:

  Platform.Tenants (SOURCE OF TRUTH)
         |
         | Same-transaction dual-write
         v
  Platform.TenantRegistry (DERIVED CACHE)
         |
         | Read by Finbuckle
         v
  HTTP Request Resolution
         |
         | Read by TenantGuardMiddleware
         v
  Runtime Enforcement (IsActive, ValidUpTo)
```

---

## 23. ASCII ERD

```
+---------------------------------------------------------------------------+
|                           TARGET ERD                                       |
+---------------------------------------------------------------------------+
|                                                                           |
|  PLATFORM CONTROL PLANE TABLES (not tenant-scoped):                       |
|                                                                           |
|  +---------------------------+     +---------------------------+         |
|  | Platform.Tenants          |     | Platform.Plans            |         |
|  | (SOURCE OF TRUTH)         |     | (Global Catalog)          |         |
|  |                           |     |                           |         |
|  | PK TenantId  uniqueidentifier| PK Id        int             |         |
|  |    Slug       nvarchar(60) |    |    Code       nvarchar(50) |         |
|  |    Subdomain  nvarchar(100)|    |    DisplayName nvarchar(100|         |
|  |    DisplayName nvarchar(200|    |    MonthlyPrice decimal    |         |
|  |    ...                     |    |    MaxStudents int         |         |
|  |    LifecycleStatus tinyint |    |    MaxUsers    int         |         |
|  |    IsActive     bit        |    |    ...                     |         |
|  |    ValidUpTo    datetime2  |    |    IsActive    bit         |         |
|  |    (derived)               |    +---------------------------+         |
|  +---------------------------+              |                             |
|         |                                   | 1:N                         |
|         | 1:N                               v                             |
|         |                        +---------------------------+           |
|         v                        | Platform.PlanFeatures     |           |
|  +---------------------------+   | (Global Junction)         |           |
|  | Platform.TenantRegistry   |   |                           |           |
|  | (DERIVED - Finbuckle)     |   | FK PlanId    int          |           |
|  |                           |   | FK FeatureId int          |           |
|  | PK Id       nvarchar(64) |   |    IsEnabled  bit         |           |
|  |    Name     nvarchar(100)|   +---------------------------+           |
|  |    IsActive bit (derived)|              |                             |
|  |    ValidUpTo datetime2   |              | N:1                         |
|  |    (derived)             |              v                             |
|  |    Slug      nvarchar(60)|   +---------------------------+           |
|  |    Subdomain nvarchar(100|   | Platform.Features         |           |
|  |    ConnectionString ...  |   | (Global Catalog)          |           |
|  +---------------------------+   |                           |           |
|                                  | PK Id        int          |           |
|  +---------------------------+   |    Code      nvarchar(50) |           |
|  | Platform.Permissions      |   |    Name      nvarchar(100)|           |
|  | (Global Catalog)          |   |    Module    nvarchar(50) |           |
|  |                           |   +---------------------------+           |
|  | PK Id        int          |                                           |
|  |    Module    nvarchar(50) |   +---------------------------+           |
|  |    Action    nvarchar(50) |   | Platform.Roles            |           |
|  |    Code      nvarchar(100)|   | (ASP.NET Identity)        |           |
|  |    Description nvarchar.. |   |                           |           |
|  +---------------------------+   | PK Id       nvarchar(450)|           |
|                                  |    Name     nvarchar(256)|           |
|  +---------------------------+   |    Code     nvarchar(100)|           |
|  | Platform.RolePermissions  |   +---------------------------+           |
|  | (Global Junction)         |                                           |
|  | FK RoleId     nvarchar..  |   +---------------------------+           |
|  | FK PermissionId int       |   | Platform.Users            |           |
|  +---------------------------+   | (Platform Staff)          |           |
|                                  |                           |           |
|  +---------------------------+   | PK Id       uniqueidentifier          |
|  | Platform.AuditLogs        |   |    Email    nvarchar(200)|           |
|  | (Platform-scoped)         |   |    ...                   |           |
|  +---------------------------+   +---------------------------+           |
|                                                                           |
|  +--------------------------------------------------------------------+  |
|  | Platform.ImpersonationLogs (append-only, cross-tenant)             |  |
|  |                                                                     |  |
|  | PK Id            uniqueidentifier                                   |  |
|  | FK PlatformUserId uniqueidentifier                                  |  |
|  |    TenantId      nvarchar(64)   (TARGET tenant, not context)       |  |
|  | FK TargetUserId  uniqueidentifier                                  |  |
|  |    StartedAt     datetime2                                          |  |
|  |    EndedAt       datetime2                                          |  |
|  |    Reason        nvarchar(500)                                      |  |
|  |    IPAddress     nvarchar(45)                                       |  |
|  +--------------------------------------------------------------------+  |
|                                                                           |
|  TENANT DATA PLANE TABLES (tenant-scoped via IHasTenantId):               |
|                                                                           |
|  +---------------------------+     +---------------------------+         |
|  | Platform.TenantMemberships|    | Platform.TenantPlans      |         |
|  | (NOT IHasTenantId)        |     | (IHasTenantId)            |         |
|  |                           |     |                           |         |
|  | PK UserId   nvarchar(450)|    | PK Id       uniqueidentifier         |
|  | PK TenantId nvarchar(64) |    | FK TenantId nvarchar(450) |         |
|  |    Status   tinyint      |    | FK PlanId   int            |         |
|  |    JoinedAt datetimeoffset|   |    SnapshotPrice decimal   |         |
|  |                           |    |    StartsAt  datetime2    |         |
|  | FK UserId -> AspNetUsers  |    |    EndsAt    datetime2    |         |
|  | FK TenantId -> Tenants    |    |    AutoRenew bit          |         |
|  |    (via Guid.ToString())  |    |    Status    tinyint      |         |
|  +---------------------------+    +---------------------------+         |
|                                                                           |
|  +---------------------------+     +---------------------------+         |
|  | Platform.Students         |     | Platform.Branches         |         |
|  | (IHasTenantId)            |     | (IHasTenantId)            |         |
|  +---------------------------+     +---------------------------+         |
|                                                                           |
|  +---------------------------+     +---------------------------+         |
|  | Platform.Invoices         |     | Platform.TenantCRMLeads   |         |
|  | (IHasTenantId)            |     | (IHasTenantId)            |         |
|  +---------------------------+     +---------------------------+         |
|                                                                           |
|  +---------------------------+     +---------------------------+         |
|  | Platform.AttendanceLogs   |     | Platform.AcademicStages   |         |
|  | (IHasTenantId)            |     | (IHasTenantId)            |         |
|  +---------------------------+     +---------------------------+         |
|                                                                           |
|  +---------------------------+     +---------------------------+         |
|  | Platform.AcademicYears    |     | Platform.TenantAddOns     |         |
|  | (IHasTenantId)            |     | (IHasTenantId)            |         |
|  +---------------------------+     +---------------------------+         |
|                                                                           |
|  +---------------------------+     +---------------------------+         |
|  | Platform.TenantCredits    |     | Platform.TenantSettings   |         |
|  | (IHasTenantId)            |     | (IHasTenantId)            |         |
|  +---------------------------+     +---------------------------+         |
|                                                                           |
+---------------------------------------------------------------------------+

IDENTITY FLOW:

  Tenant.Id (Guid) --Guid.ToString()--> TenantMembership.TenantId (string)
                         |
                         +--> TenantPlan.TenantId (string, via IHasTenantId)
                         |
                         +--> CenterixTenantInfo.Id (string, Finbuckle)
                         |
                         +--> ICurrentTenant.TenantId (string, runtime context)
                         |
                         +--> EF Query Filter (string, tenant partitioning)

  ONE identity. TWO representations. ZERO ambiguity.
```

---

## 24. EXACT FILES LIKELY TO CHANGE

### Must Change

| File | Change |
|------|--------|
| `Centerix.Infrastructure\Auth\Permissions.cs` | Add `Support.Impersonate` to PlatformScope. Verify all platform vs tenant classifications. |
| `Centerix.API\Infrastructure\TenantGuardMiddleware.cs` | Add impersonation session detection. Handle impersonation JWT context. |
| `Centerix.Application\Platform\Tenants\Commands\CreateTenantCommand.cs` | Add CenterixTenantInfo creation in same transaction. |
| `Centerix.Application\Platform\Tenants\Commands\SuspendTenantCommand.cs` | Add CenterixTenantInfo.IsActive = false update. |
| `Centerix.Application\Platform\Tenants\Commands\ReactivateTenantCommand.cs` | Add CenterixTenantInfo.IsActive = true update. |
| `Centerix.Application\Platform\Tenants\Commands\CancelTenantCommand.cs` | Add CenterixTenantInfo.IsActive = false update. |
| `Centerix.Domain\Platform\Tenants\Tenant.cs` | Remove LastSyncedAt, remove MarkSynced(). Add derived ValidUpTo setter. |
| `Centerix.Infrastructure\Data\AppDbContext.cs` | Expose ImpersonationLogs on IAppDbContext. Register TenantDbContext for cross-context transaction. |
| `Centerix.Infrastructure\Common\CurrentTenant.cs` | No change needed for impersonation (reads from Finbuckle which is now derived). |
| `Centerix.Infrastructure\Tenancy\TenantDbSeeder.cs` | Update to sync with Platform.Tenants. |
| `Centerix.Infrastructure\Data\ApplicationDbContextInitialiser.cs` | Update TenantMembership creation to use Guid.ToString(). |
| `Centerix.Infrastructure\DependencyInjection.cs` | Register cross-context transaction support. Register impersonation service. |

### Must Create

| File | Purpose |
|------|---------|
| `ImpersonationService` | Generates impersonation JWT, manages sessions, enforces time limits. |
| `ImpersonationMiddleware` | Detects impersonation JWT, establishes impersonated tenant context. |
| `ImpersonationController` | Endpoints for start/end impersonation sessions. |
| Startup reconciliation service | Compares and corrects domain vs registry. |
| Subscription expiration background job | Marks expired plans, updates Tenant.ValidUpTo. |
| TenantLifecycleSyncService | Coordinates same-transaction dual-write. |

### Should Remove

| File | Reason |
|------|--------|
| `Centerix.Application\Tenants\ITenantService.cs` | Legacy, superseded by CQRS. |
| `Centerix.Infrastructure\Tenancy\TenantService.cs` | Legacy, superseded by CQRS. |
| `Centerix.Application\Tenants\TenantDto.cs` | Legacy DTO. |
| `Centerix.Application\Tenants\CreateTenantRequest.cs` | Legacy request. |

### May Change

| File | Change |
|------|--------|
| `Centerix.Domain\Platform\Tenants\TenantMembership.cs` | Update XML comment to reference Platform.Tenants instead of TenantRegistry. |
| `Centerix.Infrastructure\Data\Configurations\TenantMembershipConfiguration.cs` | Verify FK constraint references Platform.Tenants. |
| `Centerix.Infrastructure\Data\Configurations\TenantPlanConfiguration.cs` | Verify TenantId maps to Guid.ToString(). |
| `Centerix.Domain\Platform\Staff\ImpersonationLog.cs` | Already exists. Verify properties match impersonation requirements. |
| `Centerix.Infrastructure\Data\Configurations\ImpersonationLogConfiguration.cs` | Verify configuration is complete. |

---

## 25. RECOMMENDED ARCHITECTURE: APPROVED

**RECOMMENDED ARCHITECTURE: APPROVED**

The architecture satisfies all 12 security invariants:

1. Platform users do NOT require TenantMembership for platform operations.
2. Platform users do NOT need TenantId as their own tenant.
3. Tenant users cannot perform platform operations.
4. PlatformAdmin does NOT automatically bypass tenant-scoped authorization.
5. TenantMembership controls tenant-user access.
6. Plans are global/platform-scoped.
7. Features are global/platform-scoped.
8. Subscriptions are tenant-scoped.
9. Tenant lifecycle is independent from subscription lifecycle.
10. Finbuckle does NOT become the business tenant Source of Truth.
11. Tenant identity is canonical and cannot drift between systems.
12. Tenant lifecycle enforcement cannot depend on stale duplicated state.

The Control Plane / Tenant Data Plane separation is explicit, enforced, and auditable. The architecture is production-grade and ready for implementation.

---

**END OF ARCHITECTURE REVISION — C2.2**