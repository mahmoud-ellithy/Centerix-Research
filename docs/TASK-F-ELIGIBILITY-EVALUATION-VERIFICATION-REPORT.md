# Task F Correction — Eligibility Evaluation Engine Verification Report

**TASK F CORRECTION — READY FOR REVIEW**

This report documents two correction passes on the Task F Eligibility Evaluation Engine:
1. The previous correction: closed `CompletedByUtc`, reversibility of `EligibilityStatus`, elapsed `TimeSpan` for `DaysFromContractStartGte`, currency isolation.
2. **This correction (FINAL blocker):** `AmountPaidAtLeast` must aggregate `PaymentAllocation.AllocatedAmount` for active allocations belonging to invoices of the evaluated Contract — **NOT** `Payment.Amount`.

A Payment may be allocated across multiple contracts; only the slice attributable to the evaluated Contract contributes to its eligibility sum.

---

## 1. Financial Correction

### `AmountPaidAtLeast` source of amount

**Before:** Evaluator summed `fact.Amount` where `fact.Amount == Payment.Amount`. A single Payment allocated across two contracts would double-count — both contracts would see the full `Payment.Amount`.

**After:** Evaluator sums `fact.AllocatedAmountForThisContract` where:
```
AllocatedAmountForThisContract = SUM(PaymentAllocation.AllocatedAmount)
                                  for allocations of this Payment
                                  where Status = Active
                                  and Invoice.ContractId == evaluatedContract.Id
```

`CompletedPaymentFact.Amount` was removed and replaced with `AllocatedAmountForThisContract`. The EF adapter computes the per-Payment allocation slice for the evaluated contract through `PaymentAllocation` and never reads `Payment.Amount` directly.

### Authoritative query rule

For a Contract, only payment allocations satisfying **ALL** of the following contribute:
```
Payment.TenantId            == Contract.TenantId
Payment.Status              == Completed
PaymentAllocation.Status    == Active
PaymentAllocation.PaymentId == Payment.Id
PaymentAllocation.Invoice.ContractId == Contract.Id
Payment.CurrencyCode         == Contract.CurrencyCode        (checked in evaluator)
```

The amount contributing to `AmountPaidAtLeast`:
```
SUM(PaymentAllocation.AllocatedAmount)   grouped per Payment
```
**Not** `SUM(Payment.Amount)` and **not** `SUM(Payment.Amount)` per allocation (which would double-count).

### Currency rule (preserved)
- `Payment.CurrencyCode == Contract.CurrencyCode` is required before an allocation contributes.
- USD contract + EGP payment ⇒ contributes 0.
- No EGP hardcode, no currency conversion, no exchange rates.

### Multi-contract payment

Case A:
```
Payment.Amount = 10,000
Contract A allocation = 4,000
Contract B allocation = 6,000

AmountPaidAtLeast(7,000):
  Contract A => 4,000 < 7,000  => false
  Contract B => 6,000 < 7,000  => false
```

Case B:
```
Payment.Amount = 10,000
Contract A allocation = 8,000
Contract B allocation = 2,000

AmountPaidAtLeast(7,000):
  Contract A => 8,000 >= 7,000 => true
  Contract B => 2,000 < 7,000  => false
```

These tests execute against real Local SQL Server (Server=.) — no Docker / Testcontainers.

### Other rules preserved
- `CompletedByUtc` continues to use `Payment.CompletedAtUtc` with the previous boundary semantics.
- `PaymentMethodEquals` continues to use the existing fact list ("any qualifying" — no "latest wins" inference).
- `NoOverdueInstallment` continues to use the existing `Installment` lifecycle.
- `EligibilityStatus` reversibility — `FulfillmentStatus / GrantedAtUtc / GrantedBy / DeliveredAtUtc / DeliveredBy / applied-to-subscription` remain untouched.
- Tenant isolation remains mandatory. Cross-tenant payments/allocations must not contribute.

---

## 2. Implementation

### Files Changed
| Path | Purpose |
|---|---|
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityContext.cs` | Renamed `CompletedPaymentFact.Amount` → `AllocatedAmountForThisContract`; documented the per-contract slice semantics. |
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityRuleEvaluator.cs` | `AmountPaidAtLeast` now sums `AllocatedAmountForThisContract`. |
| `src/Centerix.Infrastructure/Platform/Services/OwnerOnlyFactQueryEfAdapter.cs` | `GetCompletedPaymentsAsync` now derives per-Payment allocation slice for the evaluated contract through `PaymentAllocation` rows. Two-stage query: (1) group allocations by `PaymentId` summing `AllocatedAmount`; (2) join to `Payment` to fetch immutable `CompletedAtUtc / CurrencyCode / Method`. |
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceDomainTests.cs` | Tests already pass — `Fact()` helper updated to set `AllocatedAmountForThisContract`. |
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceSqlServerTests.cs` | Added SQL-FINAL-01..10; benefit creation now uses contract currency (so `AddBenefit` succeeds for non-EGP contracts); `SeedCompletedPaymentWithAllocationsAsync` helper added. |

### No Schema Changes
- No migrations added.
- AppDbContext and TenantDbContext remain clean (`has-pending-model-changes` returns "No changes").

---

## 3. Tests

### Domain Tests
**51 / 51 passed** — `TaskF_FreezeEligibilityServiceDomainTests`.

### Application Tests (InMemory)
**12 / 12 passed** — `TaskF_FreezeEligibilityServiceApplicationTests`.

### SQL Server Tests
**34 / 34 passed** — `TaskF_FreezeEligibilityServiceSqlServerTests`.

Original SQL-F01 through SQL-F15: **24 / 24 passed** (preserved).
New SQL-FINAL-01 through SQL-FINAL-10: **10 / 10 passed**.

| Test | Description |
|---|---|
| SQL-FINAL-01 | One payment allocated entirely to one contract; uses allocation amount. |
| SQL-FINAL-02 | One payment split across two contracts (4,000 + 6,000); each contract sees only its slice. |
| SQL-FINAL-03 | `Payment.Amount = 100,000` but allocation = 5,000; only 5,000 contributes. |
| SQL-FINAL-04 | Multiple payments summed for one contract. |
| SQL-FINAL-05 | A `Reversed` allocation must not contribute. |
| SQL-FINAL-06 | Allocation to another contract must not contribute to this contract. |
| SQL-FINAL-07 | Different currency payment (USD contract + EGP payment) must contribute 0. |
| SQL-FINAL-08 | Cross-tenant payment/allocation must not contribute. |
| SQL-FINAL-09 | Exact allocation boundary (5,000 >= 5,000). |
| SQL-FINAL-10 | Below (4,999 < 5,000) and above (5,001 > 5,000) allocation boundary. |

### Full Regression
- non-SQL / non-concurrency / non-NaturalExpiration: **1609 / 1609 passed**, **0 failed** (41 s).
- Task F suite total: **97 / 97 passed** (Domain 51 + Application 12 + SQL Server 34).
- SQL Server regression: **265 / 265 passed**, **1 skipped** (pre-existing `Task18_5.Test15`), **0 failed** (11 m 32 s).

---

## 4. SQL

```
Server: .
Docker / Testcontainers: NOT USED.
```

---

## 5. EF

```
AppDbContext    → No changes have been made to the model since the last migration.
TenantDbContext → No changes have been made to the model since the last migration.
```

No migrations added. `PendingModelChangesWarning` is not suppressed.

---

## 6. Static Review

| Search term | Result |
|---|---|
| `BenefitEligibilityStatus.Delivered` (live source) | absent — historical migration reference only. |
| `PromotionType` → `PaymentTerms` | absent |
| `BonusMonths` → `PaymentTerms` | absent |
| `installment existence` → eligibility | absent |
| `current Offer` → `EligibilityRule` | absent |
| Duplicate `EligibilityRule.IsEligible(context)` | absent — single evaluator. |
| `PendingModelChangesWarning` suppression | absent. |
| `Payment.Amount` read by evaluator | absent — evaluator only reads `AllocatedAmountForThisContract`. |

---

## 7. Run Results

### Test Counts (executed)
| Suite | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Domain | 51 | 51 | 0 | 0 |
| Application (InMemory) | 12 | 12 | 0 | 0 |
| SQL Server (Local SQL Server) | 34 | 34 | 0 | 0 |
| Task F total | **97** | **97** | **0** | **0** |
| Full non-SQL regression | 1609 | 1609 | 0 | 0 |
| SQL Server regression | — | — | 0 | 1 (pre-existing `Task18_5.Test15`) |

### Known Skips
- `Task18_5.Test15_Task1851_MixedLineageProportionalTransferredOrigin` — pre-existing, not added or removed by Task F.

### Build
`dotnet build Centerix.slnx --no-restore` — 0 errors, 0 new warnings introduced.

---

## 8. Git

```
HEAD: see `git log -1`
Commit: one focused commit (fix(billing): use contract allocations for eligibility payment facts)
Working tree: clean after commit
```

---

**TASK F CORRECTION — READY FOR REVIEW**