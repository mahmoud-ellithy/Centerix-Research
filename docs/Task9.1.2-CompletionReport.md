# Task 9.1.2 — Subscription–Installment Ownership Finalization: Completion Report

## Status: COMPLETE

## Commit SHA
Pending (to be committed)

## Installment Creation Paths Found

| # | Path | File | SubscriptionId Previously Set? | Now Set? |
|---|------|------|------|------|
| 1 | `AddInstallmentCommand` handler | `src/.../AddInstallmentCommand.cs` | NO | YES (required) |
| 2 | `CreateInstallmentScheduleCommand` handler | `src/.../CreateInstallmentScheduleCommand.cs` | NO | YES (required) |

No other production installment creation paths exist. `Installment.CreateWithStatus()` is unused.

---

## Changes Applied

### 1. `AddInstallmentCommand.cs`
- Added `SubscriptionId` (Guid, required) to the command record
- Handler validates:
  - `SubscriptionId != Guid.Empty`
  - Subscription exists in DB
  - `subscription.TenantId == tenantId` (tenant isolation)
  - `subscription.ContractId == request.ContractId` (contract ownership)
- Passes `subscriptionId: request.SubscriptionId` to `Installment.Create()`

### 2. `CreateInstallmentScheduleCommand.cs`
- Added `SubscriptionId` (Guid, required) to the command record
- Handler validates same rules as AddInstallment
- Passes `subscriptionId: request.SubscriptionId` to each `Installment.Create()` in the loop

### 3. `InstallmentErrors.cs`
Added 4 new error codes:
- `SubscriptionNotFound` — subscription does not exist
- `SubscriptionBelongsToDifferentContract` — subscription linked to different contract
- `SubscriptionBelongsToDifferentTenant` — subscription belongs to different tenant (safety net)
- `SubscriptionRequired` — SubscriptionId is required for new installment creation

### 4. `SubscriptionReconciliationService.cs`
- **Reconciliation query**: Removed `i.SubscriptionId == null` backward-compat fallback. Now only includes installments with `i.SubscriptionId == subscription.Id`.
- **Expiration error handling**: `MarkExpired()` failure is now logged with error details and returns early. `SaveChangesAsync()` returning 0 is also logged and returns early. No more silently swallowed exceptions.

---

## Validation Rules

For every new installment creation:
```
SubscriptionId != Guid.Empty
Subscription exists
Subscription.TenantId == current TenantId
Subscription.ContractId == target ContractId
```

Rejects:
- `Guid.Empty` SubscriptionId
- Non-existent subscription
- Cross-tenant subscription
- Cross-contract subscription

---

## Legacy Data Strategy

**Null `SubscriptionId` installments are EXCLUDED from reconciliation.**

The reconciliation query now requires `i.SubscriptionId == subscription.Id`. Legacy installments with `SubscriptionId = null` will not participate in financial state derivation. This is the minimum safe approach:

- No arbitrary ownership assignment
- No deterministic backfill required at this stage
- Legacy data requires explicit migration/backfill decision (documented as remaining item)

---

## Migration / Schema Status

**No migration required.** `Installment.SubscriptionId` remains nullable (`Guid?`) in the schema for backward compatibility with historical records. All NEW creation paths explicitly populate it.

The nullable column + explicit ownership model means:
- Historical rows with `SubscriptionId = null` are safely ignored by reconciliation
- New rows always have `SubscriptionId` set
- A future migration can backfill historical data if needed

---

## Test Results

### New Tests — 12/12 PASSED

| # | Test | Category | Status |
|---|------|----------|--------|
| 1 | `AddInstallmentCommand_SetsSubscriptionId_OnCreatedInstallment` | Ownership | PASSED |
| 2 | `CreateInstallmentScheduleCommand_SetsSubscriptionId_OnAllCreatedInstallments` | Ownership | PASSED |
| 3 | `AddInstallmentCommand_EmptySubscriptionId_Rejected` | Ownership | PASSED |
| 4 | `AddInstallmentCommand_SubscriptionFromDifferentTenant_Rejected` | Ownership | PASSED |
| 5 | `AddInstallmentCommand_SubscriptionFromDifferentContract_Rejected` | Ownership | PASSED |
| 6 | `Reconciliation_SubscriptionA_IgnoresSubscriptionB_Installments` | Reconciliation | PASSED |
| 7 | `Reconciliation_CurrentSubscriptionOverdue_CausesPastDue` | Reconciliation | PASSED |
| 8 | `Reconciliation_PaymentSettlement_AllowsRecovery` | Reconciliation | PASSED |
| 9 | `LegacyData_NullSubscriptionId_ExcludedFromReconciliation` | Legacy Data | PASSED |
| 10 | `Expiration_PersistenceFailure_IsObservable` | Expiration | PASSED |
| 11 | `AddInstallmentCommand_CrossTenantOwnership_Rejected` | Security | PASSED |
| 12 | `Reconciliation_AnotherSubscriptionInstallment_DoesNotCausePastDue` | Reconciliation | PASSED |

### Full Phase 9.1 Suite — 68/68 PASSED

| Test Class | Count | Status |
|------------|-------|--------|
| `Phase9_1SubscriptionStateMachineTests` | 27 | PASSED |
| `Phase9_1_1SubscriptionReconciliationHardeningTests` | 29 | PASSED |
| `Phase9_1_2SubscriptionInstallmentOwnershipTests` | 12 | PASSED |
| **Total** | **68** | **PASSED** |

### Phase 8 Tests — 159/159 PASSED (2 pre-existing SQL Server concurrency failures excluded)

---

## Files Changed

| File | Change |
|------|--------|
| `src/Centerix.Application/Platform/Billing/Installments/Commands/AddInstallmentCommand.cs` | Added SubscriptionId to command + validation |
| `src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs` | Added SubscriptionId to command + validation |
| `src/Centerix.Domain/Platform/Billing/Installments/InstallmentErrors.cs` | Added 4 new error codes |
| `src/Centerix.Infrastructure/Platform/SubscriptionReconciliationService.cs` | Removed null-fallback, fixed expiration error handling |
| `tests/.../Phase8InstallmentCommandTests.cs` | Updated command signatures |
| `tests/.../Phase8InstallmentHardeningCommandTests.cs` | Updated command signatures + added SeedSubscriptionAsync |
| `tests/.../Phase8InstallmentHardeningTests.cs` | Updated command signatures |
| `tests/.../Phase9_1_1SubscriptionReconciliationHardeningTests.cs` | Updated installment creation to pass subscriptionId |
| `tests/.../Phase9_1_2SubscriptionInstallmentOwnershipTests.cs` | **Created** — 12 regression tests |

---

## Remaining Items

- **Backfill migration**: Historical installments with `SubscriptionId = null` need a deterministic backfill to participate in reconciliation. This is a separate task.
- **Integration test**: A Testcontainers-based test should verify cross-tenant ownership rejection against real SQL Server query plan behavior.

---

## Acceptance Criteria

- [x] Every new installment creation path explicitly sets `SubscriptionId`
- [x] Subscription ownership is validated against Tenant and Contract
- [x] Another subscription's installments cannot affect the current subscription
- [x] `SubscriptionId == null` does not silently represent the current subscription for future data
- [x] Legacy null ownership is handled safely and explicitly (excluded from reconciliation)
- [x] Expiration persistence failures are not silently swallowed
- [x] Tenant isolation remains intact
- [x] Relevant tests pass (68/68 Phase 9.1, 12/12 new)
- [x] Build passes
- [x] Migration consistency verified (no schema change required)
- [x] No unrelated redesign is introduced
