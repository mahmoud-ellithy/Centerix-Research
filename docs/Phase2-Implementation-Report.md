# Phase 2 Implementation Report

## Overview

Phase 2 implements production-ready commercial enforcement for Centerix:

- **Tenant Onboarding**: PendingApproval → Provisioning → Active → Suspended → Cancelled
- **Plans & Features**: Reusable plan definitions with feature entitlement snapshots
- **Subscriptions**: TenantPlan as immutable snapshots with calendar-month semantics
- **Limits**: Atomic slot reservation with TenantPlan snapshot limit + TenantLimitOverride precedence
- **Feature Gates**: TenantPlanFeature snapshots checked via `[RequireFeature]` attribute
- **Background Enforcement**: Lazy expiration check via `SubscriptionStateService`

## Final Test Results

| Suite | Passed | Failed | Total |
|-------|--------|--------|-------|
| Foundation (Phase 1) | 126 | 0 | 126 |
| Phase 2 (Subscriptions + Limits) | 44 | 0 | 44 |
| **Grand Total** | **170** | **0** | **170** |

### Phase 2 Test Breakdown

- `Phase2DomainTests` — 21 unit tests (domain model, factories, state machines)
- `Phase2SqlServerTests` — SQL Server integration tests (atomic reservations, concurrent writes)
- `Phase2AuthorizationHttpTests` — 23 HTTP authorization matrix tests (feature/limit/subscription gates)

## Fix Applied: `BusinessWrite_FeatureGranted_LimitExhausted_DeniedByLimit`

### Root Cause 1: Wrong Usage Count

The test seeded `studentsUsed: 1` but the plan's snapshot limit was `maxStudents: 50`.

The `LimitService` reads the limit from the TenantPlan snapshot (50), not the counter's `EffectiveMaxStudents`. So the check `1 < 50` passed (limit not hit), the student creation proceeded, and FK constraints on non-existent Branch/Stage/Year caused a 500 error.

**Fix**: Changed `studentsUsed: 1` → `studentsUsed: 50` to match the plan's snapshot limit.

### Root Cause 2: `GrantFeatureAsync` Broken on InMemory

The original helper used two approaches that failed on the InMemory provider:

1. **`ExecuteSqlRawAsync`** — throws `InvalidOperationException` ("relational-specific methods can only be used when the context is using a relational database provider")
2. **Reflection-based entity creation** — created `TenantPlanFeature` via reflection, but the RowVersion concurrency check on the tracked `TenantPlan` caused `DbUpdateConcurrencyException`

**Fix**: Replaced `GrantFeatureAsync` with `EnsureFeatureOnPlanAsync` that:

1. Creates the `Feature` entity in the database (if not exists)
2. Creates a `PlanFeature` linking the plan to the feature (enabled)
3. Runs **before** subscription creation, so `SubscriptionFactory.CreateActivatedAsync` copies the feature to the subscription automatically

This avoids direct manipulation of `TenantPlanFeature` rows entirely.

### Other Changes in Same Test

- **`StudentPayload()`**: Added `enrolledAt` field (was missing, causing 400 validation error)
- **Enum binding**: Changed `gender`/`status` from string values (`"Male"`, `"Active"`) to integer values (`1`, `0`) to ensure reliable model binding

## EF Migration Verification

### Pending Model Changes

```
No changes have been made to the model since the last migration.
```

### Migration List

| Migration | Status |
|-----------|--------|
| `20260704061951_InitialCreate` | Applied |
| `20260704185803_AuthPermissionSystem` | Applied |
| `20260725003515_AddPermissionsAndRolePermissions` | Applied |
| `20260725004023_AddRoleMetadata` | Applied |
| `20260725004605_AddAuditLog` | Applied |
| `20260725010643_AddRefreshTokens` | Applied |
| `20260725153142_AddStudentsEducationModule` | Applied |
| `20260725214300_RefineM01StudentsPerERD` | Applied |
| `20260725215535_ImplementTenantAndAuditColumns` | Applied |
| `20260808221803_PendingChanges` | Applied |
| `20260810222751_RemoveTenantIdFromRolePermission` | Applied |
| `20260818223042_AddTenantMemberships` | Applied |
| `20260820231501_RemoveLastSyncedAt` | **Pending** |
| `20260824185054_AddRoleNameToTenantMemberships` | **Pending** |
| `20260826121232_Phase2SubscriptionsAndLimits` | **Pending** |

### Clean Database Apply

All 3 pending migrations applied successfully:

```
Applying migration '20260820231501_RemoveLastSyncedAt'.
Applying migration '20260824185054_AddRoleNameToTenantMemberships'.
Applying migration '20260826121232_Phase2SubscriptionsAndLimits'.
Done.
```

### Post-Update Drift Check

```
No changes have been made to the model since the last migration.
```

## Files Modified

### `tests/Centerix.SecurityTests/Phase2AuthorizationHttpTests.cs`

1. **Replaced `GrantFeatureAsync`** with `EnsureFeatureOnPlanAsync` — creates Feature + PlanFeature in DB before subscription creation
2. **Updated `BusinessWrite_FeatureGranted_LimitExhausted_DeniedByLimit`** — uses `EnsureFeatureOnPlanAsync` + `studentsUsed: 50`
3. **Updated `BusinessWrite_ExpiredSubscription_BlockedDespitePermissionAndFeature`** — uses `EnsureFeatureOnPlanAsync`
4. **Fixed `StudentPayload()`** — added `enrolledAt` field, changed enum values to integers
