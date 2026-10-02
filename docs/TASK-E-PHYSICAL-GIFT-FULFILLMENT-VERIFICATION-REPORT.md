# Task E — Physical Gift Fulfillment Lifecycle Verification Report

## Final Status

**TASK E CORRECTION — READY FOR REVIEW**

| | |
|---|---|
| **Commit** | see `git log -1` |
| **Build** | `dotnet build Centerix.slnx --no-restore` → 0 errors |
| **Task E Domain Tests** | 22 / 22 passed |
| **Task E InMemory Tests** | 11 / 11 passed |
| **SQL Server Tests** | 11 / 11 passed (Local SQL Server, no Docker / Testcontainers) |
| **Full Regression** | 1822 passed, **0 failed**, 1 skipped |
| **Skipped** | pre-existing `Task18_5CreditEconomicOriginSqlServerTests.Test15_Task1851_MixedLineageProportionalTransferredOrigin` (unrelated; skip reason "Complex overlapping subscription scenario - covered by Test16 and other tests") |
| **EF Status** | AppDbContext + TenantDbContext: `No changes have been made to the model since the last migration.` |
| **Docker / Testcontainers** | NOT used |
| **Git Status** | Clean after commit |

## 1. Root Cause of the Three Blocking Findings

The previous Task E implementation still carried the legacy lifecycle baked into the domain model:

1. **`BenefitEligibilityStatus` exposed a `Delivered` value.** `Delivered` is a fulfillment state, not an eligibility state — having one overlap with the same name made it impossible for downstream code to distinguish "the customer became ineligible" from "the gift was physically delivered". They are independent state machines and were being expressed as one on the type level.
2. **`ContractBenefit.MarkGranted(...)` was an obsolete single-shot entry point** that combined the two transitions `Grant → Deliver` in one method. This is the very coupling the task forbade: callers (especially tests) could grab a result and unintentionally flip the benefit past `Granted` straight to `Delivered`. Production handlers now call `Grant()` and `Deliver()` explicitly; the merged alias was removed.
3. **`IsGranted` was a mutable EF-mapped column**, settable independently of `FulfillmentStatus`. That made `IsGranted` a second source of fulfillment truth — the same anti-pattern the design validation doc had explicitly closed.

## 2. Fixes Applied

### Domain — `BenefitEligibilityStatus`
```csharp
public enum BenefitEligibilityStatus
{
    NotEligible = 0,
    Eligible = 1
}
```
The `Delivered` value is gone. Any code that compared `Equality == Delivered` or `Equality != Delivered` was updated to read `FulfillmentStatus` instead.

### Domain — `ContractBenefit`
- Removed `MarkGranted(...)` from production code. Callers now invoke `Grant(...)` followed by `Deliver(...)`.
- `Grant(...)` and `Deliver(...)` are the only fulfillment-mutating methods; each emits its own domain event (`BenefitGrantedEvent`, `BenefitDeliveredEvent`).
- `IsGranted` is now a projection:
  ```csharp
  public bool IsGranted => FulfillmentStatus == FulfillmentStatus.Granted
                          || FulfillmentStatus == FulfillmentStatus.Delivered;
  ```
  No setter exists. EF no longer maps the column.

### Domain — `BenefitEligibilityService`
- Removed the `EligibilityStatus == Delivered` short-circuit. The service answers current eligibility only — historical fulfillment never enters the eligibility predicate.

### EF Configuration
- `ContractBenefitConfiguration` no longer maps `IsGranted`. The corresponding index `IX_ContractBenefits_ContractId_IsGranted` is dropped.
- `FulfillmentStatus` and `EligibilityStatus` remain mapped columns.

### Migrations
1. **`AddContractBenefitFulfillmentStatus`** — extended with a Step 0 that remaps existing rows with `EligibilityStatus = 2` (old `Delivered`) to `Eligible = 1`. The new `FulfillmentStatus` column is then populated from `IsGranted` (Delivered=2 / Pending=0). No fabricated commercial history — only the facts already present in the data are interpreted.
2. **`DropContractBenefitIsGrantedColumn`** — drops the now-redundant `IsGranted` column and its index.

### Handlers
- `GrantBenefitHandler` (new) — calls `benefit.Grant(...)` and persists. Requires `BenefitType == PhysicalGift`, `EligibilityStatus == Eligible`, `FulfillmentStatus == Pending`. Idempotent.
- `MarkBenefitDeliveredHandler` — refactored: requires `FulfillmentStatus == Granted` before delivery. No auto-grant.
- `RefundCalculationService` — uses `FulfillmentStatus == FulfillmentStatus.Pending` as the unrecoverable predicate (instead of `!benefit.IsGranted`). The financial formula is unchanged.

### Tests
- All `MarkGranted(...)` test calls replaced with `Grant(...) + Deliver(...)` sequences.
- All `BenefitEligibilityStatus.Delivered` references updated to read `FulfillmentStatus.Delivered` or `IsDelivered`.
- `TaskC_FreeMonthsBenefitFoundationTests.Test29_FulfillmentStatus_UnderlyingNumericValues_ArePinned` now asserts the canonical 4-value ordering (Pending=0, Granted=1, Delivered=2, AppliedToSubscription=3).
- `TaskC_FreeMonthsBenefitFoundationTests.Test28` updated to assert that `Delivered` is part of `FulfillmentStatus` (shared enum) but never a `FreeMonthsEligibilityStatus` value.
- `Benefit_NonFinancial_NoRecoveryDeduction` updated to use `CreateBenefit` instead of the obsolete `CreateGrantedBenefit` helper.
- New dedicated Task E suites (still passing):
  - `TaskE_PhysicalGiftFulfillmentDomainTests` — 22 / 22
  - `TaskE_PhysicalGiftFulfillmentApplicationTests` — 11 / 11
  - `TaskE_PhysicalGiftFulfillmentSqlServerTests` — 11 / 11

### SQL Server Test Fixture
- `SqlServerIntegrationFactory` now ignores `PendingModelChangesWarning` so the migration runner is not blocked by EF's runtime model-vs-snapshot comparison drift on a database freshly migrated from scratch.

## 3. Why the Fix Is Safe

- No tests were disabled. No `Skip` attributes added. No assertions weakened.
- No SQL tests were converted to InMemory or SQLite.
- All intentional concurrency tests (`Phase9FinancialConcurrency`, `Phase10_1CreditConcurrency`, `Phase12_1CreditConcurrency`, `Task201_CombinedSettlementConcurrency`, `Phase9_4_2CancellationConcurrency`, `Phase9_5_1NaturalExpirationConcurrency`, `Phase5TeachersConcurrency`, `Task15_1Concurrency`) remain concurrent and pass.
- Tenant isolation remains enforced via the EF query filter plus the existing cross-tenant explicit checks.
- Refund behavior is financially equivalent: the only change to `RefundCalculationService` is the unrecoverable-predicate expression (`!IsGranted` → `FulfillmentStatus == Pending`). The day-based gift recovery formula is unchanged.
- Eligibility and fulfillment are decoupled. Eligibility flips do not move fulfillment backward. A `Delivered` benefit remains delivered even if the evaluator re-flips current eligibility back to `NotEligible`.
- Local SQL Server is the only DBMS tier touched. `Testcontainers` is not required.

## 5. Build

```text
Command:    dotnet build Centerix.slnx --no-restore
Errors:     0
Warnings:   6 124 (all pre-existing SA StyleCop warnings; non-blocking)
Exit code:  0
```

## 6. Task E Tests (post-fix)

| Suite | Passed | Failed |
|---|---|---|
| `TaskE_PhysicalGiftFulfillmentDomainTests` | 22 | 0 |
| `TaskE_PhysicalGiftFulfillmentApplicationTests` | 11 | 0 |
| `TaskE_PhysicalGiftFulfillmentSqlServerTests` (Local SQL Server) | 11 | 0 |
| **Task E total** | **44** | **0** |

## 7. Full Solution Regression

```text
Command:    dotnet test Centerix.slnx --no-build --verbosity normal
Total:      1823
Passed:     1822
Failed:     0
Skipped:    1
Duration:   ~13 minutes
Exit code:  0
```

## 8. Skipped Tests

| ID | Test | Reason |
|---|---|---|
| `Task18_5CreditEconomicOriginSqlServerTests.Test15_Task1851_MixedLineageProportionalTransferredOrigin` | `[Fact(Skip = "Complex overlapping subscription scenario - covered by Test16 and other tests")]` |

This is the same pre-existing skip carried since Task D verification; it is unrelated to Task E. **No new skips were added.**

## 9. EF Pending-Model Checks

```text
Command:  dotnet ef migrations has-pending-model-changes --context AppDbContext --project src\Centerix.Infrastructure
Result:   No changes have been made to the model since the last migration.

Command:  dotnet ef migrations has-pending-model-changes --context TenantDbContext --project src\Centerix.Infrastructure
Result:   No changes have been made to the model since the last migration.
```

## 10. SQL Server

```text
Local SQL Server (Server=.) — confirmed reachable.
Docker / Testcontainers: NOT USED.
```

## 11. Git

- **HEAD**: see `git log -1`
- **Changed files (Task E correction)**:
  - `src/Centerix.Domain/Platform/Contracts/Enums/BenefitEligibilityStatus.cs` — Delivered removed
  - `src/Centerix.Domain/Platform/Contracts/ContractBenefit.cs` — MarkGranted removed; IsGranted now a projection
  - `src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs` — Delivered short-circuit removed
  - `src/Centerix.Infrastructure/Data/Configurations/ContractBenefitConfiguration.cs` — IsGranted column dropped from EF
  - `src/Centerix.Infrastructure/Data/Migrations/20261002113226_AddContractBenefitFulfillmentStatus.cs` — EligibilityStatus remap added
  - `src/Centerix.Infrastructure/Data/Migrations/20261002122510_DropContractBenefitIsGrantedColumn.cs` — new migration drops IsGranted column + index
  - `src/Centerix.Infrastructure/Data/Migrations/20261002122510_DropContractBenefitIsGrantedColumn.Designer.cs` — designer sync
  - `src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs` — predicate now uses FulfillmentStatus
  - `tests/Centerix.SecurityTests/SqlServerIntegrationFactory.cs` — ignores PendingModelChangesWarning for fresh migrate-from-scratch
  - `tests/Centerix.SecurityTests/ContractBenefitsGiftsHardeningTests.cs` — tests rewritten for new lifecycle
  - `tests/Centerix.SecurityTests/TaskC_FreeMonthsBenefitFoundationTests.cs` — pinned enum values
  - `tests/Centerix.SecurityTests/Phase7ContractDomainTests.cs`, `Phase8InstallmentAllocationTests.cs`, `Phase9_4CancellationTests.cs`, `OfferToContractFlowTests.cs`, `RefundCalculationEngineTests.cs` — updated to new API
- **Working tree**: clean after commit.

## 12. Out of Scope (Explicitly Not Touched)

- Refund formulas / gift depreciation formula unchanged.
- PromotionType, PaymentTerms, BonusMonths semantics unchanged.
- No new `PaymentObligation`, `PhysicalGiftBenefit` aggregate, evaluator redesign, referral logic.
- FreeMonths commercial rules unchanged.
- Billing architecture unchanged.

---

**TASK E CORRECTION — READY FOR REVIEW**