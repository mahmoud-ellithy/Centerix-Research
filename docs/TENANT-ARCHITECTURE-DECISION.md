# ARCHITECTURE DECISION: PRODUCTION-GRADE TENANT SYSTEM

> **Date:** 2026-08-19
> **Status:** Decision Required
> **Scope:** Tenant identity, lifecycle, subscription, runtime enforcement, Finbuckle integration

---

## 1. EXECUTIVE VERDICT

The current codebase has **two disconnected tenant identity systems** that create a critical security gap. A tenant suspended or cancelled via the CQRS API remains fully operational because `TenantGuardMiddleware` reads `CenterixTenantInfo.IsActive` from the Finbuckle registry, which is never updated by the domain lifecycle commands. This is not a synchronization delay — it is a **complete absence of any synchronization path**.

The correct architecture is **OPTION A**: Platform.Tenants becomes the single source of truth. Finbuckle becomes a thin runtime adapter. This eliminates dual identity, eliminates dual state, and guarantees lifecycle enforcement.

---

## 2. CURRENT ARCHITECTURE (AS-IS)

```
┌─────────────────────────────────────────────────────────────────────┐
│                         CURRENT STATE                               │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌──────────────────────────┐     ┌───────────────────────────────┐ │
│  │   Platform.Tenants       │     │   Platform.TenantRegistry     │ │
│  │   (AppDbContext)         │     │   (TenantDbContext/Finbuckle) │ │
│  │                          │     │                               │ │
│  │   Guid Id                │     │   string Id                   │ │
│  │   LifecycleStatus        │     │   bool IsActive               │ │
│  │   bool IsActive          │     │   DateTime ValidUpTo          │ │
│  │   DateTime? ValidUpTo    │     │   byte Status                 │ │
│  │   DateTime? TrialEndsAt  │     │   DateTime? TrialEndsAt       │ │
│  │   string? LastSyncedAt   │     │                               │ │
│  │                          │     │   ← TenantGuard reads this    │ │
│  │   ← CQRS writes here     │     │   ← TenantService writes here│ │
│  │   ← Domain events here   │     │   ← Legacy/dead code          │ │
│  └──────────────────────────┘     └───────────────────────────────┘ │
│            │                                │                        │
│            │    NO SYNCHRONIZATION          │                        │
│            │    NO FOREIGN KEY              │                        │
│            │    NO CROSS-REFERENCE          │                        │
│            ▼                                ▼                        │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                     SECURITY GAP                             │   │
│  │  Suspend via CQRS → Platform.TenantsLifecycleStatus=Suspended│   │
│  │  TenantGuard checks → CenterixTenantInfo.IsActive = true     │   │
│  │  Result: Suspended tenant remains fully operational           │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  TenantMembership.TenantId = string → references TenantRegistry.Id  │
│  TenantPlan.TenantId = string → inherited from AuditableEntity      │
│  Domain events carry Guid TenantId → references Platform.Tenants.Id │
│  No code path reliably creates both records.                        │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

**Current failure chain:**

1. `CreateTenantCommand` creates `Platform.Tenants` record only. No `CenterixTenantInfo` is created. Tenant cannot be resolved by Finbuckle.
2. `SuspendTenantCommand` sets `LifecycleStatus=Suspended` on `Platform.Tenants`. `CenterixTenantInfo.IsActive` remains `true`. TenantGuard allows access.
3. `CancelTenantCommand` sets `IsActive=false` on `Platform.Tenants`. `CenterixTenantInfo.IsActive` remains `true`. TenantGuard allows access.
4. Legacy `TenantService` creates `CenterixTenantInfo` only. No `Platform.Tenants` record. No domain lifecycle exists.
5. `TenantMembership` references `TenantRegistry.Id` (string). Domain `Tenant.Id` is Guid. No cross-reference exists.
6. `TenantPlan.TenantId` is `string?` inherited from `AuditableEntity<Guid>`. Its identity alignment with either registry is ambiguous.

---

## 3. OPTION A ANALYSIS — Platform.Tenants as Single Source of Truth

### Concept

`Platform.Tenants` owns all tenant identity, lifecycle, and subscription state. `Platform.TenantRegistry` (Finbuckle) becomes a **thin runtime adapter** — a materialized view of the minimum data Finbuckle needs to resolve tenants on each HTTP request. It is never the authority.

### Tenant Identity

- Domain `Tenant.Id` remains `Guid`. This is the canonical tenant identifier.
- Finbuckle `CenterixTenantInfo.Id` becomes `tenant.Id.ToString()` — the same value represented as a string. No new identity space.
- `TenantMembership.TenantId` changes from string (referencing TenantRegistry) to string representation of the domain Guid. One identity, two representations.

### Lifecycle

```
Platform.Tenants.LifecycleStatus   ←  SOURCE OF TRUTH
        │
        ├── Synchronize to ──→  CenterixTenantInfo.IsActive
        │                       CenterixTenantInfo.Status
        │
        └── TenantGuard reads ──→  CenterixTenantInfo.IsActive
                                   (which was derived from the source)
```

All lifecycle transitions happen in a **single transaction** against `AppDbContext`:

| Operation | Transaction Boundary | Includes |
|-----------|---------------------|----------|
| Create | Single Tx: `AppDbContext` | Insert `Tenant` + Insert `CenterixTenantInfo` + Create initial `TenantMembership` |
| Suspend | Single Tx: `AppDbContext` + `TenantDbContext` | Update `Tenant.LifecycleStatus` + Update `CenterixTenantInfo.IsActive=false` |
| Activate | Single Tx: `AppDbContext` + `TenantDbContext` | Update `Tenant.LifecycleStatus` + Update `CenterixTenantInfo.IsActive=true` |
| Cancel | Single Tx: `AppDbContext` + `TenantDbContext` | Update `Tenant.LifecycleStatus` + Update `CenterixTenantInfo.IsActive=false` |
| Renew | Single Tx: `AppDbContext` | Update `TenantPlan.EndsAt` (no registry impact) |
| Expire | Background job | Read `TenantPlan.EndsAt <= UtcNow` + Update `TenantPlan.Status` + Update `Tenant.LifecycleStatus` |

**Key insight:** Since both `AppDbContext` and `TenantDbContext` target the **same SQL Server** (just different schemas), a single `IDbContextTransaction` from a shared `SqlConnection` can wrap both contexts. This is a well-established EF Core pattern.

### Finbuckle Synchronization

```
┌──────────────────────────────────────────────────────────────────┐
│                    SYNCHRONIZATION FLOW                           │
│                                                                  │
│  ┌──────────────┐  Same Transaction  ┌───────────────────────┐  │
│  │ Tenant.Create │ ─────────────────→ │ CenterixTenantInfo    │  │
│  │ (AppDbContext) │                   │ (TenantDbContext)     │  │
│  └──────────────┘                    └───────────────────────┘  │
│                                                                  │
│  ┌──────────────┐  Same Transaction  ┌───────────────────────┐  │
│  │ Tenant.Suspend│ ─────────────────→ │ CenterixTenantInfo    │  │
│  │ (AppDbContext) │                   │ .IsActive = false     │  │
│  └──────────────┘                    └───────────────────────┘  │
│                                                                  │
│  ┌──────────────┐  Same Transaction  ┌───────────────────────┐  │
│  │ Tenant.Cancel │ ─────────────────→ │ CenterixTenantInfo    │  │
│  │ (AppDbContext) │                   │ .IsActive = false     │  │
│  └──────────────┘                    └───────────────────────┘  │
│                                                                  │
│  Guarantees:                                                     │
│  - Both writes succeed or both fail                              │
│  - No runtime race condition                                     │
│  - TenantGuard always sees consistent state                      │
│  - No eventual consistency window                                │
└──────────────────────────────────────────────────────────────────┘
```

### Transaction Boundary Detail

For cross-context transactions (AppDbContext + TenantDbContext on same server):

```csharp
// Conceptual pattern (not implementation)
using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
using var transaction = await connection.BeginTransactionAsync();

// Phase 1: Domain write
appDbContext.Database.UseTransaction(transaction);
appDbContext.Tenants.Add(tenant);
await appDbContext.SaveChangesAsync();

// Phase 2: Registry write (same transaction)
tenantDbContext.Database.UseTransaction(transaction);
tenantDbContext.Set<CenterixTenantInfo>().Add(tenantInfo);
await tenantDbContext.SaveChangesAsync();

await transaction.CommitAsync();
// Both committed or both rolled back
```

### Startup Behavior

On application startup, a reconciliation job runs:

1. Query `Platform.Tenants` for all tenants.
2. Query `Platform.TenantRegistry` for all `CenterixTenantInfo`.
3. For each domain tenant missing from registry → insert.
4. For each registry entry missing from domain tenant → mark orphan for investigation.
5. For each domain tenant where `LifecycleStatus != Active` but `CenterixTenantInfo.IsActive == true` → correct the registry.

This is a **safety net**, not the primary synchronization mechanism. The primary mechanism is the same-transaction dual-write.

### Failure Recovery

| Failure | Behavior |
|---------|----------|
| Domain write succeeds, registry write fails | Transaction rolls back both. Domain tenant not created. Retry the operation. |
| Registry write succeeds, domain write fails | Transaction rolls back both. Registry not updated. Retry the operation. |
| Process crashes after commit | Both committed (transaction is atomic). No inconsistency. |
| Process crashes before commit | Both rolled back. No inconsistency. |
| Cross-context transaction not supported (separate servers) | Fall back to outbox pattern (see Option C analysis). But currently both contexts share the same server, so this is not needed. |

### Scaling

- **Horizontal scaling:** Both contexts target the same database. No additional coordination needed.
- **Microservice extraction:** If TenantDbContext is moved to a separate service, replace the same-transaction dual-write with the outbox pattern. The domain model does not change.
- **Read scaling:** Finbuckle's `IMultiTenantStore` can be cached. The cache is invalidated on the same transaction that updates the domain.

### Operational Complexity

- **Low.** Single source of truth. No reconciliation needed in normal operation. Startup reconciliation is a safety net only.
- **Monitoring:** Alert if startup reconciliation finds mismatches. This indicates a bug in the dual-write path.

---

## 4. OPTION B ANALYSIS — Finbuckle as Single Source of Truth

### Concept

`Platform.TenantRegistry` / `CenterixTenantInfo` becomes the authoritative tenant record. The domain `Tenant` entity becomes a **business projection** or is removed entirely.

### Analysis

| Aspect | Assessment |
|--------|-----------|
| **Domain purity** | Destroyed. The domain model becomes a read-optimized projection of an infrastructure concern. Business rules (lifecycle transitions, plan upgrades) would live in application services operating on `CenterixTenantInfo`, not in a domain entity. |
| **CQRS** | Broken. CQRS requires a rich domain model for the write side. If the write side operates on `CenterixTenantInfo` (an infrastructure DTO implementing `ITenantInfo`), CQRS commands become anemic data-transfer operations. The write model IS the read model. |
| **Lifecycle** | `CenterixTenantInfo` is an `ITenantInfo` — it has `IsActive` (bool) and `ValidUpTo` (DateTime). It does NOT have `LifecycleStatus` (enum with Provisioning, Active, Suspended, Trial, Cancelled). Adding it means modifying a Finbuckle abstractions type, coupling domain state to infrastructure. |
| **Subscription** | `TenantPlan` references tenant by string ID (via `IHasTenantId`). This works with Finbuckle's ID. But subscription business rules (renew, expire, cancel) need to interact with tenant lifecycle. If lifecycle lives in `CenterixTenantInfo`, subscription handlers must depend on `TenantDbContext` — mixing infrastructure and domain concerns. |
| **Billing** | Billing integration needs rich tenant data (owner info, plan history, lifecycle audit trail). `CenterixTenantInfo` is a flat DTO. You would need to rebuild a domain model for billing, which is the same problem you started with. |
| **Finbuckle** | Finbuckle's `ITenantInfo` is designed as a lightweight resolution record, not a business entity. Its `EFCoreStore` expects the entity to be simple. Adding business methods, domain events, and lifecycle guards to `CenterixTenantInfo` fights the library's design. |
| **Database boundaries** | `TenantDbContext` is Finbuckle's internal store. It uses `__TenantMigrationsHistory`. Making it the authority for business data means Finbuckle's migration system manages your business schema. This is fragile and non-standard. |
| **Testing** | Testing lifecycle transitions requires seeding `CenterixTenantInfo` records and asserting on `IsActive`/`Status` fields — there is no domain entity to test. Unit tests become integration tests. |
| **Long-term maintainability** | Poor. Every new business requirement (feature flags, billing events, audit trail, provisioning workflow) forces changes to an infrastructure DTO. The domain layer becomes irrelevant. |

**Verdict: Option B is rejected.** It sacrifices domain-driven design, CQRS, and long-term extensibility for short-term simplicity. It is appropriate for an MVP, not for a production SaaS platform.

---

## 5. OPTION C ANALYSIS — Keep Both Registries with Outbox Synchronization

### Concept

Both `Platform.Tenants` and `Platform.TenantRegistry` remain as separate tables with their own identity spaces. Synchronization is achieved via domain events → transactional outbox → background processor.

### Transactional Outbox Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                    OUTBOX SYNCHRONIZATION                        │
│                                                                  │
│  ┌──────────────┐                                                │
│  │ Tenant.Suspend │  (Platform.Tenants, same transaction)       │
│  └──────┬───────┘                                                │
│         │                                                        │
│         ▼                                                        │
│  ┌──────────────┐                                                │
│  │ Domain Event  │  TenantSuspendedEvent(Guid, reason)          │
│  │ persisted to │  written to Outbox table                       │
│  │ Outbox table │  (same DB transaction as Tenant update)        │
│  └──────┬───────┘                                                │
│         │                                                        │
│         ▼  (Background processor, poll or CDC)                   │
│  ┌──────────────┐                                                │
│  │ Process Event │  Read Outbox → Find CenterixTenantInfo        │
│  │               │  by Guid→string mapping                       │
│  │               │  Set IsActive=false                           │
│  └──────┬───────┘                                                │
│         │                                                        │
│         ▼                                                        │
│  ┌──────────────┐                                                │
│  │ Mark processed│  Outbox record marked as processed            │
│  └──────────────┘                                                │
└─────────────────────────────────────────────────────────────────┘
```

### Analysis

| Aspect | Assessment |
|--------|-----------|
| **Idempotency** | Required. Outbox processor must be idempotent — processing the same event twice must not create duplicate registry updates. Achievable with event ID + processed flag. |
| **Retry** | Required. If registry update fails, the outbox processor must retry with exponential backoff. Infrastructure complexity increases. |
| **Failure recovery** | Outbox provides at-least-once delivery. Combined with idempotency, this gives effectively-once semantics. But the implementation burden is significant. |
| **Eventual consistency** | Inherent. There is a window between domain write and registry update where the two registries are inconsistent. During this window: a suspended tenant may still be operational (TenantGuard reads stale registry). |
| **Runtime race conditions** | A request arriving during the eventual consistency window will pass TenantGuard because `CenterixTenantInfo.IsActive` is still `true` even though `Tenant.LifecycleStatus` is `Suspended`. This is the exact security gap the architecture must eliminate. |
| **Suspend semantics** | When an admin suspends a tenant, they expect immediate effect. Eventual consistency violates this expectation. A suspended tenant processing requests during the sync window is a security incident, not a theoretical concern. |
| **Create semantics** | Tenant created in domain but not yet in registry → tenant cannot be resolved by Finbuckle → API calls fail until outbox processes. New tenant provisioning is broken during the sync window. |
| **Disaster recovery** | If the outbox processor is down, domain state and registry state diverge indefinitely. A reconciliation job is needed. |
| **Reconciliation jobs** | Required as a permanent safety net. Adds background processing infrastructure. Must handle orphan detection, identity mapping, and state correction. |

**Verdict: Option C is rejected for production use.** The eventual consistency window creates a security gap for lifecycle enforcement. It adds significant infrastructure complexity (outbox, processor, idempotency, reconciliation) without eliminating the fundamental problem of dual identity. It is appropriate when the two registries MUST remain separate (e.g., different databases, different services), which is not the case here.

---

## 6. DECISION MATRIX

| Criterion | Option A | Option B | Option C |
|-----------|----------|----------|----------|
| Single source of truth | **Yes** | Yes (but wrong entity) | No |
| Lifecycle enforcement | **Immediate** | Immediate | Eventual |
| Domain model richness | **Full** | Destroyed | Full |
| CQRS compatibility | **Full** | Broken | Full |
| Tenant identity unification | **Yes** | Partial | No |
| Finbuckle compatibility | **Good** | Forced | Good |
| Transaction consistency | **Strong** | Strong | Eventual |
| Infrastructure complexity | **Low** | Low | High |
| Microservice readiness | **Good** | Poor | Good |
| Subscription model | **Clean** | Coupled to infrastructure | Clean |
| Billing readiness | **Good** | Poor | Good |
| Operational overhead | **Low** | Low | High |
| Security guarantee | **Guaranteed** | Guaranteed | Window of vulnerability |
| Migration difficulty | Medium | High | Medium |
| **Overall** | **Best** | Worst | Acceptable |

---

## 7. RECOMMENDED ARCHITECTURE: OPTION A

**RECOMMENDED OPTION: A**

Platform.Tenants is the single source of truth. Finbuckle is a runtime adapter.

---

## 8. SOURCE OF TRUTH

**Platform.Tenants (AppDbContext)** is the sole authority for:

| Concern | Owner |
|---------|-------|
| Tenant identity | `Tenant.Id` (Guid) |
| Tenant lifecycle state | `Tenant.LifecycleStatus` |
| Tenant operational status | `Tenant.IsActive` |
| Tenant subscription link | `Tenant.CurrentPlanId` |
| Tenant expiration | `Tenant.ValidUpTo` (derived from subscription) |
| Tenant trial | `Tenant.TrialEndsAt` |
| Tenant ownership | `Tenant.OwnerEmail`, etc. |
| Tenant isolation config | `Tenant.IsolationMode`, `Tenant.DatabaseServer` |
| Tenant display | `Tenant.DisplayName`, `Tenant.LogoUrl`, etc. |
| Audit trail | `Tenant.CreatedAtUtc`, `Tenant.LastModifiedUtc` |

**Platform.TenantRegistry (TenantDbContext)** is a **derived runtime cache** containing:

| Concern | Owner |
|---------|-------|
| Runtime tenant resolution | `CenterixTenantInfo.Id` (= `Tenant.Id.ToString()`) |
| Runtime IsActive check | `CenterixTenantInfo.IsActive` (= `Tenant.IsActive`) |
| Runtime ValidUpTo check | `CenterixTenantInfo.ValidUpTo` (= `Tenant.ValidUpTo`) |
| Runtime connection string | `CenterixTenantInfo.ConnectionString` (= `Tenant.ConnectionStringRef`) |
| Runtime slug/subdomain | `CenterixTenantInfo.Slug`, `.Subdomain` (duplicated for resolution) |

The registry is **never written to directly** by business operations. It is always derived from Platform.Tenants in the same transaction.

---

## 9. TENANT IDENTITY DECISION

### Recommendation: Guid everywhere, string representation at Finbuckle boundary

```
Platform.Tenants.Id = Guid               ← CANONICAL
TenantMembership.TenantId = string        ← Guid.ToString()
TenantPlan.TenantId = string              ← Guid.ToString() via IHasTenantId
CenterixTenantInfo.Id = string            ← Guid.ToString()
Domain events = Guid                      ← Canonical
```

### Why NOT each alternative:

| Alternative | Rejection Reason |
|-------------|-----------------|
| **Same ID as string everywhere** | Loses Guid's type safety, comparability, and indexing efficiency. `string` comparison is slower than `Guid` comparison. No benefit over Guid+string. |
| **Guid everywhere including Finbuckle** | Finbuckle's `ITenantInfo.Id` is `string` by interface contract. Cannot change without forking Finbuckle. |
| **Explicit external TenantKey** | Adds a third identity space. More mapping, more confusion, more failure modes. Solves a problem that does not exist. |
| **Mapping table** | Adds a join for every tenant lookup. Unnecessary complexity when the mapping is `Guid → string(Guid)`. |

### The recommended approach:

```csharp
// In Tenant domain entity:
public Guid Id { get; private set; }  // canonical

// When creating CenterixTenantInfo:
var tenantInfo = new CenterixTenantInfo
{
    Id = tenant.Id.ToString(),  // deterministic, reversible mapping
    // ...
};

// In TenantMembership:
TenantId = tenant.Id.ToString()  // same mapping

// In TenantPlan (via IHasTenantId):
TenantId = tenant.Id.ToString()  // same mapping
```

**Guarantee:** `tenant.Id.ToString()` always produces the same string. `Guid.Parse(string)` always recovers the Guid. There is exactly ONE identity with two representations. No mapping table, no ambiguity, no orphans.

---

## 10. LIFECYCLE MODEL

### Tenant Lifecycle States

```
                    ┌──────────────┐
                    │ Provisioning │
                    └──────┬───────┘
                           │ Setup complete
                           ▼
                    ┌──────────────┐
          ┌────────│    Active    │────────┐
          │        └──────┬───────┘        │
          │               │                │
    Reactivate       Suspend           Cancel
          │               │                │
          │               ▼                ▼
          │        ┌──────────────┐ ┌────────────┐
          └───────→│  Suspended   │ │ Cancelled  │
                   └──────────────┘ └────────────┘
                         │
                    Subscription
                       Expires
                         │
                         ▼
                   ┌──────────┐
                   │ Expired  │  (subscription state, not tenant state)
                   └──────────┘
```

### State Definitions

| State | Definition | Behavior |
|-------|-----------|----------|
| **Provisioning** | Tenant created, setup in progress | No user access. Admin can configure. Finbuckle resolves but Guard blocks. |
| **Active** | Tenant fully operational | Users with active membership can access. Finbuckle resolves. Guard allows. |
| **Suspended** | Tenant temporarily disabled | No user access. Admin can reactivate. Finbuckle resolves. Guard blocks (403). |
| **Cancelled** | Tenant permanently disabled | No user access. Cannot be reactivated. Finbuckle resolves. Guard blocks (403). Data retained for audit. |

### State Transition Rules

| From | To | Trigger | Guard Behavior |
|------|----|---------|----------------|
| Provisioning → Active | Setup complete | `tenant.Activate()` | Guard allows |
| Active → Suspended | Admin action or payment failure | `tenant.Suspend(reason)` | Guard blocks (403) |
| Suspended → Active | Admin action or payment | `tenant.Activate()` | Guard allows |
| Active → Cancelled | Admin action | `tenant.Cancel()` | Guard blocks (403) |
| Suspended → Cancelled | Admin action | `tenant.Cancel()` | Guard blocks (403) |
| ~~Cancelled → Active~~ | **Never** | Rejected by domain | Guard blocks (403) |

### Subscription Expiration vs Tenant Lifecycle

**These are SEPARATE concerns:**

| Concept | Belongs To | State Machine |
|---------|-----------|---------------|
| Tenant lifecycle | `Tenant.LifecycleStatus` | Provisioning → Active → Suspended → Cancelled |
| Subscription status | `TenantPlan.Status` | Active → Expired → Cancelled → Suspended → Reactivated |
| Feature entitlement | `PlanFeature` + runtime check | Derived from active subscription + plan |

**TenantGuard enforcement combines both:**

```
Request arrives
    │
    ▼
Finbuckle resolves tenant → reads CenterixTenantInfo
    │
    ▼
TenantGuard checks:
    1. Membership exists and is Active?          ← from AppDbContext
    2. CenterixTenantInfo.IsActive == true?      ← from TenantRegistry (derived)
    3. CenterixTenantInfo.ValidUpTo > now?       ← from TenantRegistry (derived)
    │
    ▼
All pass → Allow
Any fail → Block (403 or 402)
```

**Important:** `Tenant.ValidUpTo` should be **derived from the subscription**, not maintained independently. See Subscription Model below.

---

## 11. SUBSCRIPTION MODEL

### Current State

```
Tenant.ValidUpTo          ← independent field, never auto-updated
TenantPlan.EndsAt         ← subscription end date
CenterixTenantInfo.ValidUpTo ← independent field, checked by TenantGuard
```

Three independent expiry fields with no coordination.

### Recommended Model

```
Tenant.ValidUpTo = MAX(TenantPlan.EndsAt) WHERE TenantPlan.Status = Active
```

`Tenant.ValidUpTo` is **derived**, not independent. It is computed:

1. **At write time:** When a TenantPlan is created, renewed, or expired, the handler updates `Tenant.ValidUpTo` to the `EndsAt` of the active plan (or `null` if no active plan).
2. **At read time:** TenantGuard reads `CenterixTenantInfo.ValidUpTo` (which is synchronized from `Tenant.ValidUpTo`).
3. **At background job time:** A periodic job checks `TenantPlan.EndsAt <= UtcNow` and marks plans as expired, then updates `Tenant.ValidUpTo`.

### When subscription expires:

```
TenantPlan.EndsAt passes
    │
    ▼
Background job: TenantPlan.MarkExpired()
    │
    ▼
Handler: Update Tenant.ValidUpTo = next active plan's EndsAt (or null)
    │
    ▼
Sync to CenterixTenantInfo.ValidUpTo (same transaction)
    │
    ▼
TenantGuard returns 402 Payment Required
```

### Why not keep Tenant.ValidUpTo independent:

- It would drift from the actual subscription state.
- An admin could manually set `Tenant.ValidUpTo` to bypass subscription enforcement.
- Billing integration would need to reconcile two independent expiry dates.
- The source of truth for "when does this tenant's access end" is the subscription, not the tenant.

---

## 12. FINBUCKLE ROLE

**Role: Runtime Resolver + Request Context Provider**

Finbuckle is responsible for exactly two things:

1. **Tenant resolution:** Given an HTTP request (header or host), resolve which tenant is being accessed.
2. **Request context:** Provide `IMultiTenantContext` with the resolved `CenterixTenantInfo` for the duration of the request.

Finbuckle is **NOT** responsible for:

- Tenant lifecycle enforcement (TenantGuard does this)
- Tenant data storage (AppDbContext does this)
- Tenant provisioning (CQRS commands do this)
- Subscription management (TenantPlan handlers do this)

### TenantRegistry Physical Table

**Yes, it remains.** It is required by Finbuckle's `EFCoreStore`. But it is:

- **Never written to directly** by application code.
- **Always derived** from `Platform.Tenants` in the same transaction.
- **Treated as a cache** — startup reconciliation corrects any drift.

---

## 13. TRANSACTION STRATEGY

### Create Tenant

```
Transaction boundary: Single transaction across AppDbContext + TenantDbContext

1. Insert Tenant (Platform.Tenants)         ← AppDbContext
2. Insert CenterixTenantInfo                 ← TenantDbContext
3. Insert TenantMembership (admin user)      ← AppDbContext
4. Dispatch TenantCreatedEvent               ← (handled in-process)

Atomic: All succeed or all fail.
No outbox needed. No eventual consistency.
```

### Suspend Tenant

```
Transaction boundary: Single transaction across AppDbContext + TenantDbContext

1. Update Tenant.LifecycleStatus = Suspended ← AppDbContext
2. Update Tenant.SuspendedReason             ← AppDbContext
3. Update CenterixTenantInfo.IsActive = false ← TenantDbContext
4. Dispatch TenantSuspendedEvent             ← (handled in-process)

Atomic: TenantGuard immediately sees IsActive=false.
No outbox needed. No eventual consistency.
```

### Activate Tenant

```
Transaction boundary: Single transaction across AppDbContext + TenantDbContext

1. Update Tenant.LifecycleStatus = Active    ← AppDbContext
2. Update CenterixTenantInfo.IsActive = true  ← TenantDbContext
3. Dispatch TenantReactivatedEvent           ← (handled in-process)

Atomic: TenantGuard immediately sees IsActive=true.
```

### Cancel Tenant

```
Transaction boundary: Single transaction across AppDbContext + TenantDbContext

1. Update Tenant.LifecycleStatus = Cancelled ← AppDbContext
2. Update Tenant.IsActive = false             ← AppDbContext
3. Update CenterixTenantInfo.IsActive = false ← TenantDbContext
4. Dispatch TenantCancelledEvent              ← (handled in-process)

Atomic: TenantGuard immediately sees IsActive=false. Cannot be reversed.
```

### Renew Subscription

```
Transaction boundary: Single transaction in AppDbContext only

1. Update TenantPlan.EndsAt                 ← AppDbContext
2. Update TenantPlan.Status = Active        ← AppDbContext
3. Update Tenant.ValidUpTo = new EndsAt     ← AppDbContext
4. Sync CenterixTenantInfo.ValidUpTo        ← TenantDbContext (same transaction)
5. Dispatch TenantPlanRenewedEvent          ← (handled in-process)

No registry IsActive change needed (tenant was already active).
```

### Expire Subscription

```
Transaction boundary: Background job, single transaction

1. Find TenantPlans where EndsAt <= UtcNow AND Status = Active
2. TenantPlan.MarkExpired()                  ← AppDbContext
3. Update Tenant.ValidUpTo = next active plan's EndsAt (or null) ← AppDbContext
4. Sync CenterixTenantInfo.ValidUpTo         ← TenantDbContext (same transaction)

If no active plan remains and tenant is not in trial:
5. Tenant.Suspend("Subscription expired")    ← triggers lifecycle sync
```

---

## 14. FAILURE RECOVERY

| # | Scenario | Behavior |
|---|----------|----------|
| 1 | Tenant created but Finbuckle registry update fails | **Transaction rolls back both.** Domain tenant not created. Retry the create operation. No orphan. |
| 2 | Tenant suspended but registry update fails | **Transaction rolls back both.** Domain tenant not suspended. Retry. No inconsistency. |
| 3 | Registry update succeeds but HTTP response fails | **Transaction already committed both.** Client gets error but state is consistent. Client retries — domain sees already-suspended, returns success (idempotent). |
| 4 | Process crashes after Tenant save, before commit | **Transaction rolls back.** Both unchanged. Safe. |
| 5 | Process crashes after commit | **Both committed.** State is consistent. Client retries if needed. |
| 6 | Database restored from backup | Startup reconciliation job detects mismatch between domain and registry. Corrects registry from domain (domain is source of truth). |
| 7 | Finbuckle registry contains orphan tenant (no domain record) | Startup reconciliation detects orphan. Flags for investigation. Does not auto-delete (may be data corruption). |
| 8 | Domain Tenant contains orphan (no registry record) | Startup reconciliation inserts missing `CenterixTenantInfo` from domain data. Tenant becomes operational. |
| 9 | Subscription expires while tenant is online | Background job marks plan expired. Updates `Tenant.ValidUpTo`. Syncs to registry. TenantGuard returns 402 on next request. No data loss. |
| 10 | Cross-context transaction fails (e.g., TenantDbContext connection issue) | Both rolled back. Application logs error. Operator investigates connectivity. No partial state. |

---

## 15. TARGET ERD

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         TARGET ARCHITECTURE                             │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌─────────────────────────────────────────────┐                       │
│  │          Platform.Tenants                    │                       │
│  │          (SOURCE OF TRUTH)                   │                       │
│  │                                              │                       │
│  │  PK  TenantId        uniqueidentifier        │                       │
│  │      Slug            nvarchar(60)   UQ       │                       │
│  │      Subdomain       nvarchar(100)  UQ       │                       │
│  │      DisplayName     nvarchar(200)           │                       │
│  │      LogoUrl         nvarchar(500)           │                       │
│  │      PrimaryColor    nvarchar(7)             │                       │
│  │      Country         nchar(2)                │                       │
│  │      Currency        nchar(3)                │                       │
│  │      Timezone        nvarchar(50)            │                       │
│  │      OwnerFirstName  nvarchar(100)           │                       │
│  │      OwnerLastName   nvarchar(100)           │                       │
│  │      OwnerEmail      nvarchar(200)           │                       │
│  │      OwnerPhone      nvarchar(20)            │                       │
│  │      IsolationMode   tinyint                 │                       │
│  │      DatabaseServer  nvarchar(200)           │                       │
│  │      ConnectionStringRef nvarchar(500)       │                       │
│  │  FK  CurrentPlanId   int → Plans.Id          │                       │
│  │      LifecycleStatus tinyint    ← AUTHORITY  │                       │
│  │      SuspendedReason nvarchar(500)           │                       │
│  │      TrialEndsAt     datetime2               │                       │
│  │      ValidUpTo       datetime2  ← DERIVED    │                       │
│  │      IsActive        bit        ← AUTHORITY  │                       │
│  │      CreatedAtUtc    datetime2               │                       │
│  │      CreatedBy       nvarchar(450)           │                       │
│  │      LastModifiedUtc datetime2               │                       │
│  │      LastModifiedBy  nvarchar(450)           │                       │
│  └──────────────┬──────────────────────────────┘                       │
│                 │                                                        │
│                 │ 1:N (TenantId = Guid.ToString())                      │
│                 ▼                                                        │
│  ┌──────────────────────────────────────────────┐                      │
│  │        Platform.TenantMemberships            │                      │
│  │        (NOT IHasTenantId — cross-tenant)      │                      │
│  │                                               │                      │
│  │  PK  UserId          nvarchar(450)           │                      │
│  │  PK  TenantId        nvarchar(64)            │                      │
│  │      Status          tinyint                  │                      │
│  │      JoinedAtUtc     datetimeoffset           │                      │
│  │                                               │                      │
│  │  FK UserId → AspNetUsers.Id (CASCADE)         │                      │
│  │  FK TenantId → Platform.Tenants.TenantId      │                      │
│  │       (via Guid.ToString(), NO ACTION)         │                      │
│  └──────────────────────────────────────────────┘                      │
│                                                                         │
│  ┌──────────────────────────────────────────────┐                      │
│  │        Platform.TenantPlans                   │                      │
│  │        (IHasTenantId — tenant-scoped)         │                      │
│  │                                               │                      │
│  │  PK  Id              uniqueidentifier         │                      │
│  │  FK  TenantId        nvarchar(450)            │                      │
│  │  FK  PlanId          int → Plans.Id           │                      │
│  │      SnapshotPrice   decimal(18,2)            │                      │
│  │      StartsAt        datetime2                │                      │
│  │      EndsAt          datetime2    ← DERIVED   │                      │
│  │      AutoRenew       bit                      │                      │
│  │      Status          tinyint                  │                      │
│  │      CreatedAtUtc    datetime2                │                      │
│  │      CreatedBy       nvarchar(450)            │                      │
│  │      LastModifiedUtc datetime2                │                      │
│  │      LastModifiedBy  nvarchar(450)            │                      │
│  │                                               │                      │
│  │  FK TenantId → Platform.Tenants.TenantId      │                      │
│  │       (via Guid.ToString())                    │                      │
│  └──────────────────────────────────────────────┘                      │
│                                                                         │
│  ┌──────────────────────────────────────────────┐  ┌────────────────┐  │
│  │        Platform.Plans                         │  │Platform.Features│ │
│  │        (GlobalAuditableEntity — shared)       │  │  (shared)       │ │
│  │                                               │  │                │ │
│  │  PK  Id              int                      │  │ PK Id          │ │
│  │      Code            nvarchar(50)  UQ         │  │    Code        │ │
│  │      DisplayName     nvarchar(100)            │  │    Name        │ │
│  │      MonthlyPrice    decimal(18,2)            │  └───────┬────────┘  │
│  │      MaxStudents     int                      │          │           │
│  │      MaxUsers        int                      │          │           │
│  │      MaxBranches     int                      │          │           │
│  │      MaxTeachers     int                      │          │           │
│  │      StorageGB       int                      │          │           │
│  │      SMSQuota        int                      │          │           │
│  │      IsActive        bit                      │          │           │
│  └──────────────┬───────────────────────────────┘          │           │
│                 │                                           │           │
│                 │ 1:N                                       │           │
│                 ▼                                           │           │
│  ┌──────────────────────────────────────────────┐          │           │
│  │        Platform.PlanFeatures                  │          │           │
│  │        (junction — shared)                     │          │           │
│  │                                               │          │           │
│  │  FK  PlanId          int → Plans.Id           │          │           │
│  │  FK  FeatureId       int → Features.Id  ──────┘──────────┘          │
│  │      IsEnabled       bit                                       │     │
│  │      Value           nvarchar(100)                              │     │
│  └──────────────────────────────────────────────┘                   │     │
│                                                                      │     │
│  ┌──────────────────────────────────────────────┐                   │     │
│  │   Platform.TenantRegistry (DERIVED)           │                   │     │
│  │   (TenantDbContext / Finbuckle)                │                   │     │
│  │                                                │                   │     │
│  │   PK  Id            nvarchar(64)              │                   │     │
│  │       Identifier    nvarchar(64)              │                   │     │
│  │       Name          nvarchar(100)             │                   │     │
│  │       ConnectionString nvarchar(500)          │                   │     │
│  │       Email         nvarchar(200)             │                   │     │
│  │       FirstName     nvarchar(100)             │                   │     │
│  │       LastName      nvarchar(100)             │                   │     │
│  │       Slug          nvarchar(60)              │                   │     │
│  │       Subdomain     nvarchar(100)             │                   │     │
│  │       DisplayName   nvarchar(200)             │                   │     │
│  │       LogoUrl       nvarchar(500)             │                   │     │
│  │       PrimaryColor  nvarchar(7)               │                   │     │
│  │       Country       nchar(2)                  │                   │     │
│  │       Currency      nchar(3)                  │                   │     │
│  │       Timezone      nvarchar(50)              │                   │     │
│  │       Status        tinyint                   │                   │     │
│  │       IsActive      bit    ← DERIVED          │                   │     │
│  │       ValidUpTo     datetime2 ← DERIVED       │                   │     │
│  │       TrialEndsAt   datetime2                 │                   │     │
│  │       CreatedAt     datetime2                 │                   │     │
│  │                                                │                   │     │
│  │   Id = Tenant.Id.ToString()                    │                   │     │
│  │   ← NEVER written to directly                  │                   │     │
│  │   ← ALWAYS derived from Platform.Tenants       │                   │     │
│  └────────────────────────────────────────────────┘                   │     │
│                                                                       │     │
│  ══════════════════════════════════════════════════════════════════════════
│                                                                       │     │
│  IDENTITY FLOW:                                                       │     │
│                                                                       │     │
│  Tenant.Id (Guid) ──→ Guid.ToString() ──→ TenantMembership.TenantId │     │
│                              │                 (string)               │     │
│                              │                                        │     │
│                              ├──→ TenantPlan.TenantId                │     │
│                              │     (string, via IHasTenantId)         │     │
│                              │                                        │     │
│                              └──→ CenterixTenantInfo.Id              │     │
│                                    (string, Finbuckle runtime)        │     │
│                                                                       │     │
│  ONE identity. TWO representations. ZERO ambiguity.                   │     │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 16. MIGRATION STRATEGY

### Phase 0: Assessment (Before Any Code Changes)

**1. Identify orphan records:**

```sql
-- Tenants in Platform.Tenants but NOT in TenantRegistry
SELECT t.TenantId, t.Slug
FROM Platform.Tenants t
LEFT JOIN Platform.TenantRegistry r ON t.TenantId = TRY_CAST(r.Id AS uniqueidentifier)
WHERE r.Id IS NULL;

-- Tenants in TenantRegistry but NOT in Platform.Tenants
SELECT r.Id, r.Slug, r.Name
FROM Platform.TenantRegistry r
LEFT JOIN Platform.Tenants t ON TRY_CAST(r.Id AS uniqueidentifier) = t.TenantId
WHERE t.TenantId IS NULL;
```

**2. Map identities:**

```sql
-- For each TenantRegistry entry, find matching Platform.Tenants entry
SELECT r.Id AS RegistryId, t.TenantId AS DomainId, r.Slug, t.Slug AS DomainSlug
FROM Platform.TenantRegistry r
INNER JOIN Platform.Tenants t ON TRY_CAST(r.Id AS uniqueidentifier) = t.TenantId;
```

**3. Handle ambiguous mappings:**

- If a `TenantRegistry` entry has `Id` that is NOT a valid Guid string → it cannot map to `Platform.Tenants` directly. These must be resolved manually.
- If multiple `Platform.Tenants` entries could map to one `TenantRegistry` entry → investigate and merge manually.

### Phase 1: Clean Up Platform.TenantRegistry

For each tenant in `Platform.TenantRegistry` that does NOT have a corresponding `Platform.Tenants` record:

- **If this is a real tenant that should exist:** Create the corresponding `Platform.Tenants` record with data from `TenantRegistry`.
- **If this is an orphan/test record:** Delete it from `TenantRegistry`.
- **If this is the root tenant:** Ensure it exists in `Platform.Tenants` with `LifecycleStatus = Active`.

### Phase 2: Update TenantMembership Foreign Keys

Currently `TenantMembership.TenantId` references `TenantRegistry.Id` (string). After migration, it should reference `Platform.Tenants.TenantId` via `Guid.ToString()`.

```sql
-- Verify all TenantMembership.TenantId values map to a valid Tenant
SELECT m.UserId, m.TenantId
FROM Platform.TenantMemberships m
LEFT JOIN Platform.Tenants t ON m.TenantId = t.TenantId.ToString()
WHERE t.TenantId IS NULL;
```

If any memberships reference a TenantRegistry ID that does NOT correspond to a Platform.Tenants Guid, those memberships must be corrected or deleted before migration.

### Phase 3: Update TenantPlan TenantId

`TenantPlan.TenantId` (inherited from `AuditableEntity<Guid>`) should reference `Platform.Tenants.TenantId` via `Guid.ToString()`.

```sql
SELECT tp.Id, tp.TenantId
FROM Platform.TenantPlans tp
LEFT JOIN Platform.Tenants t ON tp.TenantId = t.TenantId.ToString()
WHERE t.TenantId IS NULL;
```

### Phase 4: Implement Same-Transaction Dual-Write

Once identities are aligned:

1. Modify `CreateTenantCommand` to also create `CenterixTenantInfo` in the same transaction.
2. Modify `SuspendTenantCommand` to also update `CenterixTenantInfo.IsActive = false`.
3. Modify `ReactivateTenantCommand` to also update `CenterixTenantInfo.IsActive = true`.
4. Modify `CancelTenantCommand` to also update `CenterixTenantInfo.IsActive = false`.
5. Remove `Tenant.MarkSynced()` and `Tenant.LastSyncedAt` (no longer needed — synchronization is inline, not deferred).

### Phase 5: Add Startup Reconciliation

Implement a startup job that:

1. Compares `Platform.Tenants` with `Platform.TenantRegistry`.
2. Inserts missing registry entries.
3. Corrects `IsActive` / `ValidUpTo` mismatches.
4. Logs all corrections for audit.

### Phase 6: Remove Legacy TenantService

Delete:

- `ITenantService`
- `TenantService`
- `TenantDto` (legacy)
- `CreateTenantRequest`
- DI registration

### Phase 7: Verification

Run the existing security tests (`TenantGuardMiddlewareTests`, `C1CrossTenantIsolationTests`). Add new tests:

- Create tenant via CQRS → verify both Platform.Tenants and TenantRegistry have the record.
- Suspend tenant → verify `CenterixTenantInfo.IsActive == false`.
- Attempt API call to suspended tenant → verify 403.
- Cancel tenant → verify `CenterixTenantInfo.IsActive == false`.
- Verify TenantMembership references Platform.Tenants.TenantId via string.

---

## 17. RISKS

| Risk | Mitigation |
|------|-----------|
| Cross-context transaction fails on different DB servers | Currently both contexts share the same SQL Server. Document this as an architectural constraint. If microservice extraction is needed later, introduce outbox at that point. |
| Startup reconciliation is slow with many tenants | Use indexed queries. Run asynchronously. Consider event-driven sync as an optimization. |
| Existing production data has orphan records | Phase 0 assessment identifies all orphans. Manual resolution before Phase 4. No automatic deletion. |
| Finbuckle upgrade breaks `EFCoreStore` usage | Finbuckle's store interface is stable. Pin version. Test upgrade in staging. |
| TenantGuard reads stale registry after concurrent request | Same-transaction dual-write eliminates this. If Finbuckle caches aggressively, verify cache invalidation. |
| Background job for subscription expiration has downtime | Use a resilient scheduler (e.g., Hangfire, Quartz). Implement idempotent processing. |

---

## 18. EXACT FILES LIKELY TO CHANGE

### Must Change

| File | Change |
|------|--------|
| `Centerix.Application\Platform\Tenants\Commands\CreateTenantCommand.cs` | Add CenterixTenantInfo creation in same transaction |
| `Centerix.Application\Platform\Tenants\Commands\SuspendTenantCommand.cs` | Add CenterixTenantInfo.IsActive = false update |
| `Centerix.Application\Platform\Tenants\Commands\ReactivateTenantCommand.cs` | Add CenterixTenantInfo.IsActive = true update |
| `Centerix.Application\Platform\Tenants\Commands\CancelTenantCommand.cs` | Add CenterixTenantInfo.IsActive = false update |
| `Centerix.Domain\Platform\Tenants\Tenant.cs` | Remove LastSyncedAt, remove MarkSynced(). Add derived ValidUpTo setter. |
| `Centerix.Infrastructure\Data\AppDbContext.cs` | Register TenantDbContext for cross-context transaction |
| `Centerix.API\Infrastructure\TenantGuardMiddleware.cs` | Verify reads from TenantRegistry are consistent with domain |
| `Centerix.Infrastructure\Tenancy\TenantDbSeeder.cs` | Update to sync with Platform.Tenants |
| `Centerix.Infrastructure\Data\ApplicationDbContextInitialiser.cs` | Update TenantMembership creation to use Guid.ToString() |
| `Centerix.Infrastructure\DependencyInjection.cs` | Register cross-context transaction support |

### Must Create

| File | Purpose |
|------|---------|
| Startup reconciliation service | Compares and corrects domain vs registry |
| Subscription expiration background job | Marks expired plans, updates Tenant.ValidUpTo |
| TenantLifecycleSyncService | Coordinates same-transaction dual-write |

### Should Remove

| File | Reason |
|------|--------|
| `Centerix.Application\Tenants\ITenantService.cs` | Legacy, superseded by CQRS |
| `Centerix.Infrastructure\Tenancy\TenantService.cs` | Legacy, superseded by CQRS |
| `Centerix.Application\Tenants\TenantDto.cs` | Legacy DTO |
| `Centerix.Application\Tenants\CreateTenantRequest.cs` | Legacy request |

### May Change

| File | Change |
|------|--------|
| `Centerix.Domain\Platform\Tenants\TenantMembership.cs` | Update XML comment to reference Platform.Tenants instead of TenantRegistry |
| `Centerix.Infrastructure\Data\Configurations\TenantMembershipConfiguration.cs` | Verify FK constraint references Platform.Tenants |
| `Centerix.Infrastructure\Data\Configurations\TenantPlanConfiguration.cs` | Verify TenantId maps to Guid.ToString() |
| `Centerix.Infrastructure\Common\CurrentTenant.cs` | No change needed (reads from Finbuckle which is now derived) |

---

## SUMMARY

```
RECOMMENDED OPTION: A

Source of Truth:         Platform.Tenants (AppDbContext)
Tenant Identity:         Guid (canonical), string (Guid.ToString() at boundary)
Finbuckle Role:          Runtime resolver + request context provider
Lifecycle Ownership:     Platform.Tenants.LifecycleStatus
Subscription Ownership:  TenantPlan.EndsAt (derives Tenant.ValidUpTo)
Runtime Enforcement:     TenantGuard reads CenterixTenantInfo (derived from domain)
Synchronization:         Same-transaction dual-write (no outbox, no eventual consistency)
Transaction Boundaries:  Single transaction across AppDbContext + TenantDbContext
Failure Recovery:        Transaction rollback (normal) + startup reconciliation (safety net)
Target ERD:              See Section 15
Migration:               7-phase process (assess → clean → align → implement → reconcile → remove → verify)
```

This architecture guarantees that `TenantMembership.TenantId`, `TenantPlan.TenantId`, `EF TenantId`, `Finbuckle Tenant Id`, and `Tenant identity` all refer to the **same tenant** through a single canonical `Guid` with a deterministic string representation. Suspended and cancelled tenants **cannot** remain operational because TenantGuard reads the derived `CenterixTenantInfo.IsActive` which is updated in the same transaction as the domain lifecycle change.
