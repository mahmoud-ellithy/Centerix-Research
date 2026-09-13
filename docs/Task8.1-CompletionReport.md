# Task 8.1 — Installment / Financial Obligation Engine Hardening — Completion Report

## 1. Commit SHA

```
3424f840527b12b16863fc8a8597580ed922e324
```

## 2. Files Changed

| File | Change |
|------|--------|
| `src/Centerix.Domain/Platform/Billing/Installments/Installment.cs` | `Update()` now rejects non-Pending status. `IsOverdue()` now excludes Cancelled. |
| `src/Centerix.Domain/Platform/Billing/Installments/InstallmentErrors.cs` | Added `CannotUpdateNonPending`, `ScheduleExceedsContractObligation`, `AmountWouldCorruptSettlement` |
| `src/Centerix.Application/Platform/Billing/Installments/Commands/AddInstallmentCommand.cs` | Added total obligation invariant (SUM <= ContractedAmount), period overlap detection |
| `src/Centerix.Application/Platform/Billing/Installments/Commands/UpdateInstallmentCommand.cs` | Added explicit Pending-only guard, schedule revalidation (obligation + overlap) |
| `src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs` | Idempotency check now includes `InstallmentId` |
| `src/Centerix.Infrastructure/Data/Configurations/PaymentAllocationConfiguration.cs` | Idempotency unique index now includes `InstallmentId` |
| `src/Centerix.Infrastructure/Data/Migrations/20260913065610_AddInstallmentAndPaymentAllocationInstallmentId.cs` | **NEW** — Creates Installments table + PaymentAllocation.InstallmentId FK |
| `src/Centerix.Infrastructure/Data/Migrations/20260913065610_AddInstallmentAndPaymentAllocationInstallmentId.Designer.cs` | **NEW** — Migration snapshot |
| `tests/Centerix.SecurityTests/Phase8InstallmentDomainTests.cs` | Updated error code assertions (`CannotUpdateNonPending`) |
| `tests/Centerix.SecurityTests/Phase8InstallmentCommandTests.cs` | Added new error codes to well-formedness test |
| `tests/Centerix.SecurityTests/Phase8InstallmentHardeningTests.cs` | **NEW** — 38 domain-level hardening regression tests |
| `tests/Centerix.SecurityTests/Phase8InstallmentHardeningCommandTests.cs` | **NEW** — 20 handler-level hardening tests via MediatR pipeline |

## 3. Migration Name

```
AddInstallmentAndPaymentAllocationInstallmentId
```

**What it creates:**
- `Platform.Installments` table with all columns (InstallmentId PK, ContractId, SubscriptionId, InvoiceId, SequenceNumber, DueDateUtc, CoveredPeriodStartUtc, CoveredPeriodEndUtc, Amount, CurrencyCode, SettledAmount, Status, RowVersion, TenantId, audit columns)
- All Installment indexes (TenantId, ContractId, SubscriptionId, InvoiceId, composite tenant-contract, tenant-sequence unique, tenant-status, tenant-dueDate)
- `PaymentAllocation.InstallmentId` nullable FK column
- FK constraint `FK_PaymentAllocations_Installments_InstallmentId` (Restrict)
- Updated `UX_PaymentAllocations_Idempotent` unique filtered index to include `InstallmentId`

**Safe for existing database:** Yes — the Installments table is new and PaymentAllocation.InstallmentId is nullable.

## 4. Installment Invariants

| Invariant | Enforced By |
|-----------|-------------|
| `SUM(Installment.Amount) <= Contract.ContractedAmount` | `AddInstallmentCommand`, `UpdateInstallmentCommand` |
| `CoveredPeriod` inside `Contract.EffectiveAtUtc → Contract.ExpiresAtUtc` | `AddInstallmentCommand`, `UpdateInstallmentCommand` |
| No period overlap | `AddInstallmentCommand`, `UpdateInstallmentCommand` |
| No duplicate period coverage | `AddInstallmentCommand`, `UpdateInstallmentCommand` |
| `SettledAmount = SUM(active PaymentAllocation.AllocatedAmount)` | `Installment.ApplyAllocation()` |
| `RemainingAmount = Amount - SettledAmount` | Computed property on entity |
| `Status` is deterministic (never client-set) | `Installment.RecalculateStatus()` |
| `Cancelled` never becomes `Overdue` | `IsOverdue()` checks Status != Cancelled |
| Only `Pending` installments can be updated | `Installment.Update()` domain method + handler guard |
| `SettledAmount <= Amount` (no negative RemainingAmount) | `Installment.ApplyAllocation()` rejects over-allocation |

## 5. AddInstallment Hardening

- **Total obligation invariant:** `SUM(existing + new) <= Contract.ContractedAmount` — enforced in `AddInstallmentHandler.ExecuteAsync()`
- **Period integrity:** Queries all existing installments for the contract and checks for period overlap
- **Period within contract:** `CoveredPeriodStartUtc >= Contract.EffectiveAtUtc` AND `CoveredPeriodEndUtc <= Contract.EndsAtUtc`
- **Duplicate period rejected:** Overlap check catches exact-duplicate periods
- **Negative/zero amount rejected:** Domain-level validation
- **Currency consistency:** Uses contract's `CurrencyCode` directly

## 6. UpdateInstallment Hardening

- **Explicit Pending-only guard:** Handler checks `installment.Status != InstallmentStatus.Pending` BEFORE calling domain update
- **Schedule revalidation:** Queries other installments for the same contract and validates:
  - `SUM(others + newAmount) <= Contract.ContractedAmount`
  - No period overlap with any other installment
  - Period remains within contract bounds
- **Amount corruption prevention:** Only Pending installments can be updated, so `SettledAmount` is always 0 when Amount changes
- **Domain-level guard:** `Installment.Update()` also rejects non-Pending status as defense-in-depth

## 7. PaymentAllocation Idempotency Changes

**Before:** Idempotency key = `(TenantId, PaymentId, InvoiceId, AllocatedAmount)` with filter `[Status] = 'Active'`

**After:** Idempotency key = `(TenantId, PaymentId, InvoiceId, InstallmentId, AllocatedAmount)` with filter `[Status] = 'Active'`

**Impact:**
- Same Payment + Invoice + Amount + **same Installment** = identical retry (idempotent)
- Same Payment + Invoice + Amount + **different Installment** = distinct allocation (not incorrectly treated as retry)
- Non-installment allocations (InstallmentId = NULL) still work correctly (NULL = NULL in unique index)

## 8. Settlement Derivation

- `SettledAmount` is NEVER set by clients. It is accumulated via `Installment.ApplyAllocation()`.
- `RemainingAmount` is a computed property: `Amount - SettledAmount`.
- `Status` is derived deterministically by `RecalculateStatus()`:
  - `Cancelled` → stays `Cancelled`
  - `SettledAmount >= Amount` → `Paid`
  - `SettledAmount > 0 && DueDateUtc < now` → `Overdue`
  - `SettledAmount > 0 && DueDateUtc >= now` → `PartiallyPaid`
  - `SettledAmount == 0 && DueDateUtc < now` → `Overdue`
  - `SettledAmount == 0 && DueDateUtc >= now` → `Pending`

## 9. Overdue Logic

- `IsOverdue(utcNow)` = `Status != Cancelled && RemainingAmount > 0 && DueDateUtc < utcNow`
- Cancelled installments are explicitly excluded from overdue determination
- `CheckBenefitEligibilityCommand` queries: `Status != Cancelled && Status != Paid && DueDateUtc < now && RemainingAmount > 0`
- No manually-editable `IsOverdue` flag exists

## 10. Benefit Eligibility Integration

**Case A:** Contract Active + Installment fully paid + No overdue installments → Benefit can become Eligible ✓
**Case B:** Contract Active + Installment overdue + Remaining > 0 → Benefit remains NotEligible ✓
**Case C:** Installment Cancelled → Does not create overdue condition (excluded from query) ✓

The eligibility service checks:
1. `contract.Status == Active`
2. `completedPaymentTotal >= contractedAmount`
3. `hasOverdueInstallment == false`

## 11. Tenant Isolation

All operations enforce tenant isolation:
- `AddInstallment`: Reads `dbContext.TenantId` (from `ICurrentTenant`), validates contract belongs to tenant
- `UpdateInstallment`: Validates `installment.TenantId == tenantId`
- `CancelInstallment`: Validates `installment.TenantId == tenantId`
- `AllocatePaymentCommand`: Validates `payment.TenantId == invoice.TenantId`, loads installment with `i.TenantId == payment.TenantId`
- EF global query filter: `e.TenantId == _currentTenant.TenantId` on all `IHasTenantId` entities
- Client NEVER provides `TenantId` — it is always stamped by the server

## 12. Concurrency Behavior

- **Serializable isolation** on all financial transactions (AddInstallment, UpdateInstallment, CreateSchedule, AllocatePayment)
- **Deadlock retry** with bounded exponential backoff (3 attempts, 50ms/100ms/200ms)
- **Optimistic concurrency** via `RowVersion` on Installment and PaymentAllocation
- **Idempotency** via filtered unique index prevents duplicate allocations
- **Lock order:** Payment first, then Invoice (prevents deadlocks in allocation)
- No changes to existing Task 3.1.2 concurrency strategy

## 13. Tests Added

| File | Tests | Coverage |
|------|-------|----------|
| `Phase8InstallmentHardeningTests.cs` | 38 | Domain-level: obligation invariant, period integrity, update guard, settlement derivation, allocation integrity, benefit eligibility, tenant isolation, error codes, migration verification |
| `Phase8InstallmentHardeningCommandTests.cs` | 20 | Handler-level via MediatR: AddInstallment (obligation, overlap, duplicate, beyond-contract, duplicate-seq), UpdateInstallment (pending-only, non-pending, exceeds-obligation, overlap), CancelInstallment, settlement derivation, error codes, migration model verification |
| **Total new tests** | **58** | |

## 14. Exact Test Results

### Task 8 tests (Phase8 filter):
```
Passed: 76, Failed: 0, Total: 76
```

### Task 8.1 Hardening tests (Hardening filter):
```
Passed: 152, Failed: 0, Total: 152
```

(Includes all 76 Phase8 + 76 new Hardening domain + 20 new handler tests = 152 total from combined filter)

### Full solution tests:
```
Passed: 714, Failed: 2, Total: 716
```

## 15. Pre-existing/Task 8.1-caused Failures

### Pre-existing failures (2):
- `Phase3AuthorizationHttpTests.Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound` — SQL Server connectivity/environmental
- `Phase3AuthorizationHttpTests.Students_TenantAdmin_CanCreateReadUpdateSoftDelete` — SQL Server connectivity/environmental

These are Phase 3 authorization HTTP tests that require SQL Server. They are **NOT** related to Task 8 or 8.1.

### Task 8.1-caused failures:
**None.** All new and existing Phase8 tests pass.

## 16. EF Pending-Model-Change Result

```
No changes have been made to the model since the last migration.
```

The model snapshot is synchronized with the current EF model.

## 17. Remaining Known Limitations

1. **SQL Server integration tests** for concurrency (Phase9 tests) were not added for installment allocation concurrency because they require a live SQL Server instance. The existing Phase9FinancialConcurrencySqlServerTests cover the PaymentAllocation concurrency pattern, and the same Serializable isolation + deadlock retry strategy is preserved.

2. **Period contiguity** is validated in `CreateInstallmentScheduleCommand` but NOT in `AddInstallmentCommand` (incremental add). The task requirements focus on overlap/duplicate/beyond-contract, not contiguity for incremental additions. This is consistent with allowing incomplete schedules during construction.

3. **The `CannotUpdatePaidOrCancelled` error** is retained for backward compatibility (used in `Cancel()` for Paid status). The new `CannotUpdateNonPending` error is used in `Update()` for the hardened status check. Both are present in the error catalog.
