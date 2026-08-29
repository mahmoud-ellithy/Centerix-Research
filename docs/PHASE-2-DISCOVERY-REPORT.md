# PHASE 2 DISCOVERY REPORT

> Scope: Tenant Onboarding, Platform Admin Approval, Plans, Features, Plan/Feature configuration,
> Tenant Subscription, Subscription lifecycle, Bonus, Expiration, Activation/Suspension,
> Feature enforcement, Plan limits.
>
> Foundation (Identity + Membership + Invitation + Tenant Authorization) is CLOSED — untouched.

## 1. Entity Inventory

### 1.1 `Tenant` — `Domain/Platform/Tenants/Tenant.cs` → table `Platform.Tenants`

| Aspect | Finding |
|---|---|
| **Fields** | Id (Guid), Slug, Subdomain, DisplayName, LogoUrl?, PrimaryColor?, Country(2), Currency(3), Timezone, Owner{First,Last,Email,Phone?}, IsolationMode, DatabaseServer?, ConnectionStringRef?, CurrentPlanId?, LifecycleStatus, SuspendedReason?, TrialEndsAt?, ValidUpTo?, IsActive + audit (CreatedAtUtc/CreatedBy/LastModified*) |
| **Relationships** | None enforced in EF (no nav props); `CurrentPlanId` is an untyped int reference to Plans; registry mirror lives in `CenterixTenantInfo` / `Platform.TenantRegistry` (separate TenantDbContext, synced via `ITenantRegistrySync`) |
| **Behavior** | `Create()` → starts in `LifecycleStatus.Provisioning`, raises `TenantCreatedEvent`; `Activate()` (from any non-Cancelled state), `Suspend(reason)`, `Cancel()`, `UpgradePlan(planId)` (requires Active), `SetValidUpTo(date)`; `Update()` profile only |
| **Migration status** | Created in `20260808221803_PendingChanges`; consistent with snapshot (verified Phase 1) |
| **Tests** | `C2TenantRegistrySyncTests` asserts `Provisioning` on create + registry sync; guard tests cover `ValidUpTo` null/expiry semantics |
| **Missing behavior** | • **No `Pending` / `Rejected` states** — enum is `Provisioning/Active/Suspended/Trial/Cancelled`. `Provisioning` conflates "awaiting platform review" with "infra being provisioned". • No approval/rejection transition recording *who/when/why*. • `ValidUpTo` is set manually (`SetValidUpTo`); **never driven by subscription lifecycle**. • `UpgradePlan` mutates only the pointer; writes no `TenantPlan` history row. |
| **Recommended change** | Add explicit `PendingApproval` + `Rejected` lifecycle values (or repurpose `Provisioning` strictly for post-approval provisioning); add `Approve/Reject(platformAdminId, reason?)` domain methods raising auditable events; wire `SetValidUpTo` to active-subscription effective expiration during approve/renew/expire/suspend. |

### 1.2 `LifecycleStatus` enum — `Tenants/Enums/LifecycleStatus.cs`
`Provisioning=0, Active=1, Suspended=2, Trial=3, Cancelled=4` (byte). **No Pending-for-approval, no
Rejected, no Expired** — expired tenants currently stay `Active` with past `ValidUpTo` and are
blocked only at the middleware (402).

### 1.3 `Plan` — `Domain/Platform/Plans/Plan.cs` → table `Platform.Plans`

| Aspect | Finding |
|---|---|
| **Fields** | Id (int), Code (unique, 30), DisplayName (100), MonthlyPrice decimal(10,2), MaxStudents, MaxUsers, MaxBranches, MaxTeachers, StorageGB, SMSQuota, IsActive + audit |
| **Relationships** | 1→* `PlanFeature`, 1→* `TenantPlan` |
| **Behavior** | `Create/Update` (validates non-negative price & limits), `Activate/Deactivate` with events, `AddPlanFeature/RemovePlanFeature` |
| **Migration** | Since `20260704061951_InitialCreate` |
| **Tests** | **None** |
| **Missing** | • No `Description`. • **No Currency** (price is bare decimal; tenant carries Currency but plan doesn't). • **No Duration/billing-period definition** — "Monthly" is implicit in the name `MonthlyPrice`. • **No bonus rules.** • Limits exist but see §1.7 enforcement gap. |
| **Recommended change** | Extend Plan minimally: `Description`, `CurrencyCode`, optional `BillingPeriodDays` (or keep monthly-fixed if business confirms), leave limits as-is. Do **not** add PlanVersion yet — see §3. |

### 1.4 `Feature` — `Domain/Platform/Features/Feature.cs` → `Platform.Features`
`Id, Code (unique,50), Description?(500), Module(50)`. Pure catalog. CRUD endpoints exist
(`FeaturesController`). Correctly **separate from Permissions** (`PermissionCatalog` is identity-side).
No tests. No changes needed to the entity itself.

### 1.5 `PlanFeature` — `Platform.PlanFeatures`
Junction `PlanId, FeatureId, IsEnabled` with cascade deletes both sides.
⚠️ **No unique constraint on (PlanId, FeatureId)** — duplicates possible despite the in-memory guard
in `Plan.AddPlanFeature`. Needs a unique index. No tests.

### 1.6 `TenantPlan` (= Subscription) — `Subscriptions/TenantPlan.cs` → `Platform.TenantPlans`

| Aspect | Finding |
|---|---|
| **Fields** | Id (Guid), TenantId (inherited `IHasTenantId`, nvarchar(450), **required**), PlanId FK, SnapshotPrice decimal(10,2), StartsAt, **EndsAt nullable**, AutoRenew, Status (`Active/Expired/Cancelled/Suspended`, byte) + audit |
| **Relationships** | FK → Plans (`Restrict`); TenantId is a plain string column — **no FK to TenantRegistry** (cross-context limitation documented in Phase 1) |
| **Behavior** | `Create`, `Update(endsAt, autoRenew)`, `Renew(newEndsAt)` (revives Expired/Suspended → Active, raises event), `Cancel`, `MarkExpired`, `Suspend`, `Reactivate` |
| **ERD intent (v3 docs)** | *"Historical record of each tenant's subscriptions. SnapshotPrice freezes the price at subscription time"* — i.e., **TenantPlan IS the immutable commercial snapshot; history is multiple rows per tenant** |
| **Migration** | Since InitialCreate; `IX_TenantPlans_TenantId` only |
| **Tests** | **None** |
| **Critical defects found** | • **`PlatformService.CreateTenantPlanAsync` never sets `TenantPlan.TenantId`** → violates its own required-column config; every API call through `POST /api/tenantplans` either throws or inserts an orphan row. Dead/vestigial path that must be replaced by proper commands. • **No base-vs-effective expiration** — one nullable `EndsAt` cannot represent duration + bonus provenance. • **No Bonus anywhere.** • **No approved-by/at fields** (only generic CreatedBy). • **No indexes on `(TenantId, Status)`**, no partial-unique "single Active subscription per tenant" rule, **no concurrency token** (rowversion) despite concurrent state-change requirement. • Nothing ever calls `MarkExpired` — no expiration hook/job. |
| **Recommended change** | Keep entity as the subscription aggregate; extend with `DurationDays` (base), `BonusDays`, `BonusReason?`, `BaseEndsAt`, computed/persisted `EffectiveEndsAt = StartsAt + DurationDays + BonusDays`, `ApprovedByUserId?`, `ApprovedAtUtc?`, rowversion. Replace `IPlatformService` CRUD with MediatR commands guarded by platform authorization. |

### 1.7 Limits infrastructure — exists, **zero enforcement**
- `Plan.Max{Students,Users,Branches,Teachers}, StorageGB, SMSQuota`.
- `TenantLimitOverride` (`LimitType` string, `OverrideValue`, `Reason?`) — platform-granted per-tenant bumps.
- `TenantUsageCounter` (per-tenant counts + `EffectiveMax*` + `SyncStatus`) — designed as a materialized counter cache.
- **Nothing reads these to deny operations.** E.g., `StudentsController.Create` checks permission only; no limit check. This is the biggest behavioral gap.

### 1.8 Feature enforcement — **missing**
No runtime component resolves "does this tenant's active subscription include feature X?"
`PlanFeatures.IsEnabled` is dead configuration today. Note: `TenantGuardMiddleware` already loads
permissions into `HttpContext.Items["TenantPermissions"]`; a parallel feature-resolution step belongs
there or in a dedicated attribute/service — **not in JWT** (consistent with closed foundation).

### 1.9 Platform Admin authorization — existing mechanics
- Single IdentityUser login; `PermissionAuthorizationHandler` short-circuits **any** requirement when
  `User.IsInRole("PlatformAdmin")` (JWT role claim).
- Separate `PlatformUsers/PlatformRoles/PlatformUserRoles/ImpersonationLogs` tables exist (staff
  directory) but are **not wired to authentication**.
- Implication: platform operations must be authorized by the *existing* role + permission policies
  (`Plans.*`, `Tenants.*`, `TenantPlans.*` codes already catalogued). Approval commands must
  additionally verify the actor is platform-scoped (role check) and must be **denied for
  tenant-scoped users** — currently a `TenantAdmin` holding e.g. `Tenants.Update` could call tenant
  lifecycle mutations on their own tenant; the approval workflow needs an explicit platform-boundary
  guard.

### 1.10 Auditing
`IAuditWriter.WriteAsync(action, entityType, entityId, oldValue, newValue)` +
`AuditPayload.Serialize` already used by `CreateTenantCommand`, `PlatformService`, etc.;
`AuditLog` (tenant-scoped) + `PlatformAuditLog` tables exist. **Reuse this — do not invent a second
mechanism.**

## 2. Schema Support Assessment

| Capability | Supported today? | Gap |
|---|---|---|
| Tenant onboarding | 🟡 Partial | Create + registry sync work; no review/approval stage |
| Tenant approval | 🔴 Missing | No Pending/Rejected states, no approver/timestamp, tenant self-service activation risk |
| Plan assignment | 🟡 Partial | `CurrentPlanId` + history table exist; creation path is broken (no TenantId) |
| Feature assignment | 🟡 Partial | Junction table exists; no unique key; nothing resolves features at runtime |
| Subscription lifecycle | 🟡 Partial | Enum + transitions exist; no activation/approval linkage, no concurrency token |
| Subscription expiration | 🔴 Missing | `MarkExpired` never invoked; no job/hook; `ValidUpTo` not synced; `EndsAt` nullable ambiguity |
| Bonus | 🔴 Missing | No representation whatsoever |
| Plan limits | 🟡 Partial | Data model complete (limits + overrides + counters); enforcement absent |
| Feature enforcement | 🔴 Missing | No evaluation path at all |

## 3. Plan Versioning — Recommendation: **NO PlanVersion entity**

The ERD v3 explicitly defines `TenantPlan` as the historical per-tenant record whose `SnapshotPrice`
freezes commercial terms. To make snapshots complete without versioning machinery:

1. Copy **all** commercial terms onto `TenantPlan` at creation (price, currency, duration, bonus, and
   the effective limit set — either copied scalar limits or a small `TenantPlanFeature` snapshot of
   enabled feature codes).
2. Changing a `Plan` then only affects **future** subscriptions, satisfying the stated goal
   ("changing a Plan later does not silently alter existing subscriptions").
3. A `PlanVersion` table would duplicate this guarantee with extra joins and lifecycle complexity; it
   is justified only if business requires retroactive reporting against *historical plan definitions*
   — flagged as a business decision, default recommendation is snapshot-on-subscribe.

## 4. Tenant Status Semantics — Proposed (based on existing model)

| Rule | Decision basis |
|---|---|
| Pending tenant login? | **Allowed to authenticate but blocked at guard** (403 with explicit reason) — Identity credentials may exist pre-approval; matches invitation flow which needs accounts before full access |
| Pending accept invitations? | **No** — guard blocks all tenant endpoints until Active |
| Expired tenant login? | Yes (read-oriented); guard returns **402** (already implemented for `ValidUpTo` past) |
| Expired read data? | **Yes — read allowed, writes denied** (grace/read-only window). Requires new write-blocking rule in guard. **Flagged: business decision to confirm** |
| Platform Admin on suspended/expired tenants? | **Yes** — bypass guard like today's `IsActive` handling for platform admins; must be made explicit |
| On subscription expiry | Subscription → `Expired`; tenant `ValidUpTo` already past → guard enforces 402; tenant lifecycle stays Active (renewable) |
| On renewal | New/extended subscription recomputes `EffectiveEndsAt`, updates `ValidUpTo`, subscription/reactivation events raised, all audited |

Items marked "flagged" need product sign-off; everything else derives from existing implemented
behavior (402 expiry, null = no expiration, IsActive gating).

## 5. Business Decisions Required Before Implementation

1. Rename/repurpose `Provisioning` vs adding `PendingApproval`/`Rejected` enum values (byte enum →
   additive change, migration-safe).
2. Confirm expired-tenant policy: pure block (current 402) vs read-only grace window.
3. Bonus units: days vs months (recommend **days**, integer, audited with reason).
4. Single-active-subscription invariant vs overlapping subscriptions (recommend single Active + history).
5. Auto-renew semantics: actual payment integration is out of scope — renew stays a platform-admin
   action; `AutoRenew` remains advisory metadata.
6. Limit granularity: enforce from live Plan values vs snapshot-on-subscribe (ties to §3).

---

Stopping here per instructions — no code modified.
Awaiting report review/approval before incremental implementation.
