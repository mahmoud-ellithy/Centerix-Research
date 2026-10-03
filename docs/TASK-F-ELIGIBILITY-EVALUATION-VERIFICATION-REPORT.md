# Task F Final Test Correction — Eligibility Evaluation Engine Verification Report

**TASK F FINAL TEST CORRECTION — READY FOR REVIEW**

This report documents three passes on the Task F Eligibility Evaluation Engine:

1. The original correction: closed `CompletedByUtc`, reversibility of `EligibilityStatus`, elapsed `TimeSpan` for `DaysFromContractStartGte`, currency isolation.
2. The production correction: `AmountPaidAtLeast` aggregates `PaymentAllocation.AllocatedAmount` for active allocations belonging to invoices of the evaluated Contract — **NOT** `Payment.Amount`.
3. **This pass (test correction):** the previously added SQL regression tests did **not** exercise the scenario they claimed. `SQL-FINAL-02` called a helper that creates a NEW `Payment` on every invocation, so it modelled two independent payments instead of one payment split across two contracts. The multi-contract blocker therefore remained unproven. The production implementation was reviewed and found correct; only the tests were defective.

The production semantics remain: a Payment may be allocated across multiple contracts, and only the slice attributable to the evaluated Contract contributes to its eligibility sum.

---

## 1. What changed in this pass

| Item | Change |
|---|---|
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceSqlServerTests.cs` | Added `SeedCompletedPaymentWithMultipleAllocationsAsync(...)` — creates **exactly one** `Payment` and attaches every supplied allocation row to that single `Payment.Id`. Self-guards that exactly one payment row exists and that all allocations point at it. |
| Same file | Added `ReadPaymentFactsAsync(...)` — reads the authoritative `CompletedPaymentFact` list off the real database so tests can assert the per-contract slice, not just the boolean outcome. |
| Same file | `SQL-FINAL-02` **rewritten** to use ONE payment (10,000) with two allocations (4,000 → Invoice A → Contract A, 6,000 → Invoice B → Contract B). |
| Same file | Added `SQL-FINAL-11` — asymmetric proof: ONE payment (10,000) split 8,000 / 2,000 against a 7,000 threshold. |
| Same file | Added `SQL-FINAL-12` — aggregation within ONE payment: 3,000 + 2,000 across two invoices of the SAME contract, asserting the payment still surfaces as exactly ONE fact with `AllocatedAmountForThisContract = 5,000`. |
| Same file | `SQL-FINAL-06` retained unchanged in behaviour; its comment was corrected because it described a single payment while actually creating two. |
| Production code | **Unchanged.** No redesign, no migrations, no schema change, no rule-algebra change. |

### 1.1 Defect that was corrected

`SeedCompletedPaymentWithAllocationsAsync` creates a new `Payment` on every invocation, so the previous `SQL-FINAL-02` produced:

```text
Payment #1 = 10,000
    Allocation = 4,000 → Contract A

Payment #2 = 10,000
    Allocation = 6,000 → Contract B
```

instead of the required scenario:

```text
Payment #1 = 10,000
    Allocation #1 = 4,000 → Invoice A → Contract A
    Allocation #2 = 6,000 → Invoice B → Contract B
```

Only the second shape proves that a single payment is not charged against every contract it touches.

---

## 2. Authoritative production semantics (unchanged)

### `AmountPaidAtLeast` source of amount

The evaluator sums `fact.AllocatedAmountForThisContract`, never `Payment.Amount`. For a given Contract, only payment allocations satisfying **ALL** of the following contribute:

```text
Payment.TenantId                   == Contract.TenantId
Payment.Status                     == Completed
PaymentAllocation.Status           == Active
PaymentAllocation.PaymentId        == Payment.Id
PaymentAllocation.Invoice.ContractId == Contract.Id
Payment.CurrencyCode               == Contract.CurrencyCode        (checked in the evaluator)
```

The amount contributing to `AmountPaidAtLeast` is `SUM(PaymentAllocation.AllocatedAmount)` grouped per `Payment`, narrowed to the evaluated contract. `Payment.Amount` is never read by the evaluator or the fact adapter.

### Currency rule (preserved)
- `Payment.CurrencyCode == Contract.CurrencyCode` is required before an allocation contributes.
- USD contract + EGP payment ⇒ contributes 0.
- No EGP hardcode, no currency conversion, no exchange rates.

### Other rules preserved
- `CompletedByUtc` continues to use `Payment.CompletedAtUtc` with the previous boundary semantics.
- `PaymentMethodEquals` continues to use the existing fact list ("any qualifying" — no "latest wins" inference).
- `NoOverdueInstallment` continues to use the existing `Installment` lifecycle.
- `EligibilityStatus` reversibility — `FulfillmentStatus / GrantedAtUtc / GrantedBy / DeliveredAtUtc / DeliveredBy / applied-to-subscription` remain untouched.
- Tenant isolation remains mandatory. Cross-tenant payments/allocations must not contribute.

---

## 3. Tests

### SQL-FINAL-02 — one payment, two contracts (required scenario)

```text
Payment.Amount = 10,000        (ONE payment, ONE Payment.Id)

Contract A ← Invoice A ← allocation 4,000
Contract B ← Invoice B ← allocation 6,000

AmountPaidAtLeast(7,000):
  Contract A: 4,000 < 7,000  => false
  Contract B: 6,000 < 7,000  => false
```

The test additionally asserts the fact level: both contracts resolve the SAME `PaymentId`, with `AllocatedAmountForThisContract` of exactly 4,000 and 6,000.

### SQL-FINAL-11 — asymmetric proof (same payment, one side passes)

```text
Payment.Amount = 10,000        (ONE payment, ONE Payment.Id)

Contract A ← Invoice A ← allocation 8,000
Contract B ← Invoice B ← allocation 2,000

AmountPaidAtLeast(7,000):
  Contract A: 8,000 >= 7,000 => true
  Contract B: 2,000 <  7,000 => false
```

This is the strongest proof that a contract receives only its attributable allocation: both contracts are settled by the same `Payment.Id`, so any implementation that charges `Payment.Amount` (10,000) to both makes Contract B eligible and the test fails.

### SQL-FINAL-12 — aggregation within one payment (per-payment grouping)

```text
Payment.Amount = 10,000        (ONE payment, ONE Payment.Id)

Invoice A1 → Contract A → allocation 3,000
Invoice A2 → Contract A → allocation 2,000

AmountPaidAtLeast(5,000) => true
Facts for Contract A: exactly ONE fact,
    PaymentId = the single payment,
    AllocatedAmountForThisContract = 5,000
```

### Mutation check — the tests fail against a `Payment.Amount` implementation

A temporary mutation was applied to `OwnerOnlyFactQueryEfAdapter` (projecting `Payment.Amount` into `AllocatedAmountForThisContract`) purely to prove the new tests are not vacuous, then reverted. `git status` confirms production code is unmodified.

```text
Mutation run (SqlFINAL selection): 12 total, 8 passed, 4 failed
  SqlFINAL02_OnePayment_AllocatedAcross_TwoContracts_SumPerContract            FAILED
  SqlFINAL11_SinglePayment_AsymmetricAllocations_OnlyLargerContractQualifies  FAILED
  SqlFINAL12_SinglePayment_TwoAllocations_SameContract_AggregatesToOneFact    FAILED
  SqlFINAL10_AmountPaidAtLeastBelowAndAboveAllocationBoundary                 FAILED
```

### Preserved coverage

Nothing was removed or weakened. SQL-F01..SQL-F15, SQL-FINAL-01 and SQL-FINAL-03..SQL-FINAL-10, the domain tests, the application tests, eligibility reversibility, fulfillment preservation, currency isolation, tenant isolation, and reversed-allocation behaviour are all retained. No real SQL integration test was replaced with an in-memory test.

---

## 4. Verification run

### Build

```text
dotnet build Centerix.slnx --no-restore
→ 0 Errors
```

### Task F domain / application tests

| Suite | Total | Passed | Failed | Skipped | Not Executed | Exit Code |
|---|---|---|---|---|---|---|
| `TaskF_FreezeEligibilityServiceDomainTests` | 51 | 51 | 0 | 0 | 0 | 0 |
| `TaskF_FreezeEligibilityServiceApplicationTests` | 12 | 12 | 0 | 0 | 0 | 0 |

### Task F SQL Server tests (local SQL Server)

```text
dotnet test tests\Centerix.SecurityTests\Centerix.SecurityTests.csproj --no-build
  --filter "FullyQualifiedName~TaskF_FreezeEligibilityServiceSqlServerTests"
```

| Suite | Total | Passed | Failed | Skipped | Not Executed | Exit Code |
|---|---|---|---|---|---|---|
| `TaskF_FreezeEligibilityServiceSqlServerTests` | 36 | 36 | 0 | 0 | 0 | 0 |

Breakdown: SQL-F01..SQL-F15 = 24 / 24 (preserved). SQL-FINAL-01..SQL-FINAL-12 = 12 / 12.
Task F total: **99 total, 99 passed, 0 failed, 0 skipped, 0 not executed**.

### Full regression (complete solution)

```text
dotnet test Centerix.slnx --no-build
```

| Metric | Value |
|---|---|
| Total | 1922 |
| Passed | 1921 |
| Failed | 0 |
| Skipped | 1 |
| Not Executed | 0 |
| Exit Code | 0 |

Duration: 12 m 50 s.

The single skip is pre-existing and unrelated to Task F:
`Task18_5CreditEconomicOriginSqlServerTests.Test15_Task1851_MixedLineageProportionalTransferredOrigin`.
No infrastructure failure was hidden as a skip.

---

## 5. SQL Server

```text
Server:        .            (local SQL Server, explicitly pinned via
                            CENTERIX_SQLTEST_CONNECTION="Server=.;Trusted_Connection=True;
                            TrustServerCertificate=True;Encrypt=False")
Docker:                  NOT USED
Testcontainers:          NOT USED
In-memory substitution:  NOT USED for any SQL integration test
```

Because the connection string was set explicitly, the fixture resolved it on its first branch and never entered the Testcontainers fallback path. No Docker process was started during the run.

---

## 6. EF model status

```text
dotnet ef migrations has-pending-model-changes --context AppDbContext
→ No changes have been made to the model since the last migration.

dotnet ef migrations has-pending-model-changes --context TenantDbContext
→ No changes have been made to the model since the last migration.
```

No migrations were added. `PendingModelChangesWarning` is not suppressed.

---

## 7. Static Review

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
| New entity types added by this pass | none. |
| Migrations added by this pass | none. |

---

## 8. Git

```text
Commit:  test(billing): prove multi-contract payment allocation eligibility
Files:   tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceSqlServerTests.cs
         docs/TASK-F-ELIGIBILITY-EVALUATION-VERIFICATION-REPORT.md
```

No production source file is modified by this commit.

---

**TASK F FINAL TEST CORRECTION — READY FOR REVIEW**
