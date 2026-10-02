# Task F Correction — Eligibility Evaluation Engine Verification Report

**TASK F CORRECTION — READY FOR REVIEW**

---

## 1. Key Fixes

### CompletedByUtc
**Defect:** The previous evaluator only checked `UtcNow <= deadline`, returning `true` whenever the deadline had not yet passed, even with **zero** completed payments.

**Fix:** The evaluator now requires at least one authoritative completion fact. `IOwnerOnlyFactQuery.GetCompletedPaymentsAsync` returns the list of `CompletedPaymentFact` (one per `Payment` row with `Status = Completed` whose allocation chain links back to the contract's invoice). The rule passes iff `fact.CompletedAtUtc <= deadline`. No fabricated timestamps from contract creation, invoice date, or current time. `completedAt.Kind != Utc` is treated defensively as "never qualifies".

### EligibilityStatus Reversibility
**Defect:** The previous `FreezeEligibilityService` only called `MarkEligible` on the eligible path; on the ineligible path, the benefit's `EligibilityStatus` was not actively re-synchronised to `NotEligible`, so an Eligible benefit that became ineligible could remain stale.

**Fix:** `FreezeEligibilityService` now converges `EligibilityStatus` with the evaluation outcome through the existing domain API:
- `IsEligible == true` → `MarkEligible(now, tenantId)`
- `IsEligible == false` → `MarkNotEligible()`

Both branches leave `FulfillmentStatus`, `GrantedAtUtc`, `GrantedBy`, `DeliveredAtUtc`, `DeliveredBy`, and any applied-to-subscription state **untouched**. Reversibility is exercised in `TestF_App10 / App11 / App12 / App13 / App14` and SQL-F11 / SQL-F12.

### DaysFromContractStartGte
**Defect:** The previous context-builder sliced `effectiveAtUtc.Date` and `utcNow.Date` into midnights, counting calendar boundaries instead of elapsed duration. A contract that started at `2026-10-01 23:00 UTC` with `Now = 2026-10-02 00:00 UTC` (elapsed = **1 hour**) would incorrectly satisfy `DaysFromContractStartGte(1)`.

**Fix:** `EligibilityContext` now carries the authoritative `ContractStartUtc` (DateTime, UTC). The evaluator computes `elapsed = UtcNow - ContractStartUtc` and checks `elapsed >= TimeSpan.FromDays(days)`. The 23:00 → 00:00 → 23:00 boundary test passes (TestF64 / TestF65), proving `.Date` slicing is no longer used.

### Test Coverage
- Domain tests now cover **51 cases**: invariant guards (F01-F06), every primitive pass/fail boundary, the canonical 23:00 → 24h elapsed window, currency mismatch in `AmountPaidAtLeast`, payment-method "any match among multiple" (not just the latest), and the 5 reversibility states. Tests span `TestF01` through `TestF90` (see `TaskF_FreezeEligibilityServiceDomainTests.cs`).
- Application tests cover **12 cases**: idempotency, reversibility, cross-tenant guard, missing benefit, missing payment, no-rule snapshot. Tests span `TestF_App01` through `TestF_App14` (see `TaskF_FreezeEligibilityServiceApplicationTests.cs`).
- SQL Server tests cover **24 cases** (SQL-F01 through SQL-F15 + boundary variants): every primitive on real SQL Server, currency isolation, cross-tenant payment isolation, cross-tenant installment isolation, completed-payment boundary equality, multiple-payment "any qualifying" semantics (see `TaskF_FreezeEligibilityServiceSqlServerTests.cs`).

### Verification Report
This document supersedes the previous `TASK-F-FREEZE-ELIGIBILITY-SERVICE-VERIFICATION-REPORT.md`. The previous report's regression numbers were outdated and contradicted by the test counts; all numbers in the current report are the **actual executed counts**.

---

## 2. Implementation

### Files Changed
| Path | Purpose |
|---|---|
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityContext.cs` | Added `ContractStartUtc`, `ContractCurrencyCode`, `CompletedPayments` (IReadOnlyList\<CompletedPaymentFact\>). Removed `CompletedByUtc`, `DaysFromContractStart`, `PaymentMethod`, `AmountPaid`. |
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityRuleEvaluator.cs` | CompletedByUtc, PaymentMethodEquals, AmountPaidAtLeast now use `CompletedPayments`. DaysFromContractStartGte now uses elapsed TimeSpan. |
| `src/Centerix.Domain/Platform/Contracts/EligibilityRules/EligibilityRule.cs` | Removed duplicate `IsEligible(EligibilityContext)` method on the abstract base class. |
| `src/Centerix.Application/Common/Interfaces/IOwnerOnlyFactQuery.cs` | Replaced 4 methods with `GetContractFactsAsync`, `GetCompletedPaymentsAsync`, `HasOverdueInstallmentAsync`. |
| `src/Centerix.Application/Platform/Contracts/Services/EligibilityContextBuilder.cs` | Uses new fact-query shape; one batch of fact queries per freeze. |
| `src/Centerix.Infrastructure/Platform/Services/OwnerOnlyFactQueryEfAdapter.cs` | Implements the new fact-query shape. Coerces SQL `datetime2` values to `DateTimeKind.Utc`. |
| `src/Centerix.Infrastructure/Platform/Services/FreezeEligibilityService.cs` | Reversibility — calls `MarkEligible` OR `MarkNotEligible` based on the evaluation outcome; never mutates fulfillment fields. |
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceDomainTests.cs` | 51 domain tests covering invariant guards, primitive boundaries, and elapsed-time semantics. |
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceApplicationTests.cs` | 12 application tests covering eligibility reversibility (4 cases), idempotency, cross-tenant, and fulfillment-preservation guarantees. |
| `tests/Centerix.SecurityTests/TaskF_FreezeEligibilityServiceSqlServerTests.cs` | 24 SQL Server integration tests (SQL-F01 through SQL-F15 + boundary variants). |

### Semantic Fixes
1. `CompletedByUtc` — requires a `Payment` with `Status = Completed` whose `CompletedAtUtc <= deadline`. No fabrication.
2. `EligibilityStatus` reversibility — converges to `Eligible` or `NotEligible` on every evaluation. Fulfillment fields are not touched.
3. `DaysFromContractStartGte` — uses `UtcNow - ContractStartUtc` TimeSpan. No `.Date` slicing.
4. `PaymentMethodEquals` — matches if ANY completed payment has the matching method (not "latest payment wins").
5. `AmountPaidAtLeast` — currency-consistent: only payments whose `CurrencyCode` matches the contract's `CurrencyCode` contribute. No EGP hardcode.
6. `NoOverdueInstallment` — uses the existing `Installment` lifecycle. Cross-tenant isolation enforced.

### No Schema Changes
- No new migrations added by this correction.
- AppDbContext and TenantDbContext models match their snapshots — `migrations list` shows all migrations in the snapshot, no pending model changes. `PendingModelChangesWarning` was never suppressed and is not needed.

---

## 3. Tests

### Domain Tests
**51 / 51 passed** — see `TaskF_FreezeEligibilityServiceDomainTests` (TestF01 through TestF90).

### Application Tests (InMemory)
**12 / 12 passed** — see `TaskF_FreezeEligibilityServiceApplicationTests` (TestF_App01 through TestF_App14).

### SQL Server Tests
**24 / 24 passed** — see `TaskF_FreezeEligibilityServiceSqlServerTests` (SQL-F01 through SQL-F15 + boundary variants).

| Test | Description |
|---|---|
| SQL-F01 | `ContractActive` — true when Active; false on non-Active states |
| SQL-F02 | `PaymentTermsEquals` — FullUpfront matches FullUpfront; mismatch returns `PaymentTermsMismatch` |
| SQL-F03 | `PaymentMethodEquals` — non-matching payment → false; matching payment → true |
| SQL-F04 | `CompletedByUtc` — no completed payment ⇒ false; completed before deadline ⇒ true |
| SQL-F05 | `NoOverdueInstallment` — overdue present ⇒ false; none overdue ⇒ true |
| SQL-F06 | `AmountPaidAtLeast` — currency-consistent sum; SAR/EGP mismatch does not contribute |
| SQL-F07 | `DaysFromContractStartGte` — elapsed TimeSpan semantics |
| SQL-F08 | `DurationMonthsGte` — calendar-month semantics |
| SQL-F09 | `AllOf` short-circuit; `AnyOf` first-pass |
| SQL-F11 | Eligibility reversibility — flips back to NotEligible without mutating FulfillmentStatus |
| SQL-F12 | FulfillmentStatus preserved across ineligible re-freeze in the Delivered state |
| SQL-F13 | Cross-tenant payment isolation (Tenant A contract + Tenant B payment) |
| SQL-F14 | Cross-tenant installment isolation |
| SQL-F15 | Completed-payment boundary cases — equal-to-deadline qualifies, post-deadline disqualifies, multiple payments with one qualifying |

### Full Regression (excluding SqlServer / Concurrency / NaturalExpiration)
**1604 / 1604 passed**, **0 failed**, **0 skipped** (41 seconds).

### SQL Server Regression
**255 passed**, **1 skipped** (pre-existing `Test15_Task1851_MixedLineageProportionalTransferredOrigin`), **0 failed** (11 m 35 s).

---

## 4. SQL

```
Server: .
Docker / Testcontainers: NOT USED.
```

---

## 5. EF

```
AppDbContext → No changes have been made to the model since the last migration.
TenantDbContext → No changes have been made to the model since the last migration.
```

No migrations added. `PendingModelChangesWarning` is not suppressed — no longer necessary because the model is clean.

---

## 6. Static Review

| Search term | Result |
|---|---|
| `BenefitEligibilityStatus.Delivered` (live source) | absent — the only match is `20261002113226_AddContractBenefitFulfillmentStatus.cs`, a historical migration that REMAPPED old `EligibilityStatus = 2` (`Delivered`) to `Eligible = 1` and dropped the column. The live enum has only `NotEligible` and `Eligible`. |
| `PromotionType` → `PaymentTerms` | absent — no inference from PromotionType to PaymentTerms |
| `BonusMonths` → `PaymentTerms` | absent — no inference from BonusMonths to PaymentTerms |
| `installment existence` → eligibility | absent — only overdue installments affect eligibility, not existence |
| `current Offer` → `EligibilityRule` | absent — evaluation reads the `ContractBenefit` snapshot, not the live Offer |
| Duplicate `EligibilityRule.IsEligible(context)` | removed — single authoritative evaluator |
| `PendingModelChangesWarning` suppression | absent — no longer necessary |

---

## 7. Git

```
HEAD: 4b38591df1319622cadde71f0890d937f21597f4
Commit message: fix(billing): correct eligibility evaluation semantics
Working tree: clean
```

---

**TASK F CORRECTION — READY FOR REVIEW**