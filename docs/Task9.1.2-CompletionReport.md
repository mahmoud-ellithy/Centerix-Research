# Task 9.1.2A — Finalize Installment Subscription Ownership: Completion Report

## Task: 9.1.2A
## Status: COMPLETE
## Actual Commit SHA: `dafcf5d`

(Base: `6029a1c` — Task 9.1.2)

---

## Installment Creation Paths Found

| # | Path | File | SubscriptionId Enforced? |
|---|------|------|------|
| 1 | `Installment.Create()` (domain factory) | `src/Centerix.Domain/.../Installment.cs` | **YES** — `Guid subscriptionId` required, non-nullable |
| 2 | `Installment.CreateWithStatus()` (domain factory) | `src/Centerix.Domain/.../Installment.cs` | **YES** — `Guid subscriptionId` required, non-nullable |
| 3 | `AddInstallmentCommand` handler | `src/Centerix.Application/.../AddInstallmentCommand.cs` | **YES** — command requires `Guid SubscriptionId`, validated |
| 4 | `CreateInstallmentScheduleCommand` handler | `src/Centerix.Application/.../CreateInstallmentScheduleCommand.cs` | **YES** — command requires `Guid SubscriptionId`, validated |

No other production installment creation paths exist. All 4 paths now enforce SubscriptionId.

---

## Changes Applied (Task 9.1.2A)

### 1. `Installment.cs` — Domain Factory Hardening
- `Create()`: Changed `Guid? subscriptionId = null` → `Guid subscriptionId` (required, non-nullable)
- `CreateWithStatus()`: Same change
- Private constructor: Changed `Guid? subscriptionId` → `Guid subscriptionId`
- Added validation: `if (subscriptionId == Guid.Empty) return InstallmentErrors.SubscriptionRequired;`
- **Compile-time enforcement**: Callers cannot omit subscriptionId
- **Runtime validation**: Guid.Empty is rejected

### 2. `SubscriptionReconciliationService.cs` — Expiration Error Handling
- `MarkExpired()` failure now throws `InvalidOperationException` instead of logging and returning silently
- `SaveChangesAsync()` returning 0 now throws `InvalidOperationException` instead of logging and returning silently
- Callers can observe reconciliation failures

### 3. `SubscriptionStateService.cs` — Exception Observability
- Added `ILogger<SubscriptionStateService>` to constructor
- catch block now logs the exception at Warning level instead of silently swallowing it
- Failure is observable via structured logging

---

## How SubscriptionId Is Derived

For every creation path:
```
Command receives SubscriptionId (non-nullable Guid)
  ↓
Handler validates SubscriptionId != Guid.Empty
  ↓
Handler loads Subscription from DB
  ↓
Validates: subscription.TenantId == currentTenant
Validates: subscription.ContractId == request.ContractId
  ↓
Passes subscriptionId: request.SubscriptionId to Installment.Create()
```

**Server-derived ownership**: The client must provide SubscriptionId, but the handler validates the relationship strictly. The Subscription must belong to the current tenant and the expected contract.

---

## Ownership Validation

```
SubscriptionId != Guid.Empty
Subscription exists in DB
Subscription.TenantId == CurrentTenant
Subscription.ContractId == ExpectedContractId
```

Rejects:
- `Guid.Empty` SubscriptionId
- Non-existent subscription
- Cross-tenant subscription
- Cross-contract subscription

---

## Legacy NULL Strategy

**Historical NULL SubscriptionId installments are NOT arbitrarily reassigned.**

The reconciliation query requires `i.SubscriptionId == subscription.Id`. Legacy installments with `SubscriptionId = null` are excluded from financial state derivation.

Strategy:
- **NEW DATA**: Always has explicit SubscriptionId
- **LEGACY DATA**: SubscriptionId = NULL, excluded from reconciliation
- **No silent reassignment**: Historical records left unchanged
- **Future migration**: Backfill requires explicit business decision

---

## Migration Status

**No schema change required.** `Installment.SubscriptionId` remains nullable (`Guid?`) in the DB schema for backward compatibility with historical records.

Application-level enforcement ensures:
- Historical rows with `SubscriptionId = null` are safely ignored by reconciliation
- New rows always have `SubscriptionId` set via domain factory validation
- A future migration can backfill historical data if needed

---

## Expiration Error-Handling Result

**Before (Task 9.1.2):**
```csharp
// SubscriptionReconciliationService: logged and returned silently
logger.LogWarning("Failed to mark subscription...");
return;

// SubscriptionStateService: swallowed exception silently
catch { /* Write-through convergence only */ }
```

**After (Task 9.1.2A):**
```csharp
// SubscriptionReconciliationService: throws on failure
throw new InvalidOperationException(
    $"Failed to mark subscription {subscription.Id} as expired...");

// SubscriptionStateService: logs exception at Warning level
catch (Exception ex)
{
    logger.LogWarning(ex,
        "Failed to persist lazy expiration for subscription {SubscriptionId}...",
        subscription.Id);
}
```

---

## Tests — 18/18 PASSED

### Existing Tests (from Task 9.1.2) — 12/12 PASSED

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

### New Tests (Task 9.1.2A) — 6/6 PASSED

| # | Test | Category | Status |
|---|------|----------|--------|
| 13 | `DomainFactory_EmptySubscriptionId_Rejected` | Domain Enforcement | PASSED |
| 14 | `DomainFactory_ValidSubscriptionId_Succeeds` | Domain Enforcement | PASSED |
| 15 | `Expiration_PersistenceFailure_ThrowsInvalidOperationException` | Expiration | PASSED |
| 16 | `NewInstallment_CannotBeNullOwned_ViaAddInstallmentCommand` | Ownership | PASSED |
| 17 | `CreateInstallmentSchedule_SubscriptionFromDifferentContract_Rejected` | Ownership | PASSED |
| 18 | `CreateInstallmentSchedule_SubscriptionFromDifferentTenant_Rejected` | Ownership | PASSED |

### Full Phase 9.1 Suite — 74/74 PASSED

| Test Class | Count | Status |
|------------|-------|--------|
| `Phase9_1SubscriptionStateMachineTests` | 27 | PASSED |
| `Phase9_1_1SubscriptionReconciliationHardeningTests` | 29 | PASSED |
| `Phase9_1_2SubscriptionInstallmentOwnershipTests` | 18 | PASSED |
| **Total** | **74** | **PASSED** |

### Phase 8 Tests — 159/161 PASSED

2 pre-existing SQL Server concurrency test failures (require SQL Server instance, not related to this task).

---

## Build Result

```
dotnet build Centerix.slnx → Build succeeded.
dotnet test (Phase 9.1) → 74/74 PASSED
dotnet test (Full suite) → 822/827 PASSED (5 pre-existing failures)
```

---

## Changed Files

| File | Change |
|------|--------|
| `src/Centerix.Domain/Platform/Billing/Installments/Installment.cs` | Made subscriptionId required in Create() and CreateWithStatus() |
| `src/Centerix.Infrastructure/Platform/SubscriptionReconciliationService.cs` | Throws on expiration persistence failure |
| `src/Centerix.Infrastructure/Platform/SubscriptionStateService.cs` | Logs exceptions instead of swallowing |
| `tests/.../Phase8InstallmentDomainTests.cs` | Updated 12+ calls to pass subscriptionId |
| `tests/.../Phase8InstallmentHardeningTests.cs` | Updated 6 calls to pass subscriptionId |
| `tests/.../Phase8InstallmentAllocationTests.cs` | Updated 8 calls to pass subscriptionId |
| `tests/.../Phase8_1_1FinancialIntegrityTests.cs` | Updated 14 calls to pass subscriptionId |
| `tests/.../Phase8_1_3InstallmentRehydrationTests.cs` | Updated 2 calls to pass subscriptionId |
| `tests/.../Phase8InstallmentHardeningCommandTests.cs` | Updated SeedInstallmentAsync to require subscriptionId |
| `tests/.../Phase9_1SubscriptionStateMachineTests.cs` | Updated helper methods to require subscriptionId |
| `tests/.../Phase9_1_1SubscriptionReconciliationHardeningTests.cs` | Updated helper + legacy data test |
| `tests/.../Phase9_1_2SubscriptionInstallmentOwnershipTests.cs` | Added 6 new tests, updated legacy data test |

---

## Acceptance Criteria

- [x] Every NEW Installment creation path sets SubscriptionId
- [x] Subscription ownership is validated
- [x] Cross-tenant ownership is rejected
- [x] Cross-contract ownership is rejected
- [x] Newly created NULL-owned Installments are impossible through supported application paths
- [x] Historical NULL records are not arbitrarily reassigned
- [x] Reconciliation cannot ambiguously treat future NULL records as current subscription obligations
- [x] Expiration persistence failures are observable
- [x] Tests pass (74/74 Phase 9.1, 18/18 Phase 9.1.2)
- [x] Build passes
- [x] No unrelated redesign is introduced

---

## Remaining Items

- **Backfill migration**: Historical installments with `SubscriptionId = null` need a deterministic backfill to participate in reconciliation. This is a separate task.
- **Integration test**: A Testcontainers-based test should verify cross-tenant ownership rejection against real SQL Server query plan behavior.
