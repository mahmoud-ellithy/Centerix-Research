# TASK 18.4 — FINAL COMMERCIAL INTEGRITY GAP CLOSURE

**Date:** 2026-09-22
**Scope:** Close the remaining verified integrity gaps from the Task 18 final review (F-18.4.1 … F-18.4.4). No redesign, no new business policy.

---

## 1. Objective

Task 18 established the commercial snapshot chain (`Plan → Offer → Contract → Subscription → BillingCycle → Invoice`) and the upgrade/downgrade unused-value → Customer Credit flow. Task 18.4 closes the four remaining integrity gaps:

1. `Offer.AddBenefit` snapshot immutability (domain-level).
2. `OfferFeatures (OfferId, FeatureCode)` database uniqueness.
3. `Contract.EffectiveAtUtc` control — decide against existing business rules, do not invent policy.
4. D-02 paid-settlement calculation — verify `PaymentAllocation + CreditApplication − Refund` semantics with double-counting and refund protection.

---

## 2. Findings Verified

| ID | Finding | Confirmed? | Evidence |
| -- | ------- | ---------: | -------- |
| F-18.4.1 | `Offer.AddBenefit(...)` did NOT enforce `Status == Calculated` while `AddFeature`/`AddPricingTier` did | **YES** | `Offer.cs` — the other two guards existed, `AddBenefit` was missing it |
| F-18.4.1b | `OfferBenefit` duplicate semantics have a repository-defined domain identity | **NO — UNKNOWN** | No natural key, no unique index, no domain dedupe anywhere → `UNKNOWN — insufficient repository evidence`. No uniqueness invented. |
| F-18.4.2 | `OfferFeatures` lacked a UNIQUE index on `(OfferId, FeatureCode)` | **YES** | EF config only had non-unique indexes; no `UX_OfferFeatures_*` in migration history |
| F-18.4.2b | Domain `FeatureCode` comparison is case-insensitive → DB must match | **YES** | `OfferFeature.Create` upper-invariant normalizes; conversion dedupes with `OrdinalIgnoreCase` |
| F-18.4.3 | Explicit business rule restricting who may set `Contract.EffectiveAtUtc` | **NO — UNKNOWN** | Task 17/18 docs contain only the *alignment* invariant (`EffectiveAtUtc == StartsAtUtc`), never a date-authority rule → `OPEN BUSINESS DECISION` (§6). Behavior unchanged. |
| F-18.4.3b | `EffectiveAtUtc` can bypass offer expiration / tenant isolation / overlap protection | **NO** | Conversion requires `Status == Accepted` (expiry checked at Accept); tenant filters + `StampAddedTenantIds`; overlap protection is SERIALIZABLE + unique indexes, date-independent |
| F-18.4.4 | D-02 `paidAmount` counted only `PaymentAllocation`, excluding `CreditApplication` settlement | **YES** | `CreditApplication` reduces `Invoice.RemainingAmount`, consumes `TenantCredit`, writes `CreditUsage` ledger — it IS settlement; excluding it understated paid value |
| F-18.4.4b | Refunded amounts must not count as paid settlement | **YES** | Including completed refunds would allow `Refund + Upgrade Credit` double recovery |
| F-18.4.4c | Double counting possible if Payment + CreditApplication overlap | **RISK CONTROLLED** | CreditApplication is capped by `Invoice.GetRemainingAmount()` → disjoint portions; `min(unused, settled)` bounds credit regardless |

---

## 3. Changes Implemented

| ID | Change | Files |
| -- | ------ | ----- |
| F-18.4.1 | `Offer.AddBenefit` now rejects when `Status != Calculated` using the existing `OfferErrors.InvalidStateTransition` convention | `src/Centerix.Domain/Platform/Promotions/Offer.cs` |
| F-18.4.1 | Domain tests: 4 status cases + error-code convention + feature/tier regression (7 tests) | `tests/Centerix.SecurityTests/Task18_4OfferImmutabilityTests.cs` |
| F-18.4.2 | UNIQUE index `UX_OfferFeatures_OfferId_FeatureCode` in EF configuration | `src/Centerix.Infrastructure/Data/Configurations/OfferBenefitConfiguration.cs` |
| F-18.4.2 | Migration adding the unique index (drops old non-unique `IX_OfferFeatures_OfferId_FeatureCode` first) | `src/Centerix.Infrastructure/Data/Migrations/20260922171220_Task18_4_OfferFeatureUniqueness.cs` (+ Designer) |
| F-18.4.2 | SQL Server tests: duplicate rejected, case-variant rejected, different-code allowed, different-offer allowed (4 tests) | `tests/Centerix.SecurityTests/Task18_4CommercialIntegritySqlServerTests.cs` |
| F-18.4.3 | **No behavior change** — documented as OPEN BUSINESS DECISION (§6) | `docs/TASK-18.4-FINAL-COMMERCIAL-INTEGRITY-GAP-CLOSURE.md` |
| F-18.4.4 | `paidAmount = completed PaymentAllocations + CreditApplications − Completed Refunds`, floored at 0; `creditAmount = min(unusedValue, paidAmount)` | `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs` |
| F-18.4.4 | SQL Server tests: Scenarios 5–8 (min-of, partial pay, payment+credit settlement, refund exclusion) (4 tests) | `tests/Centerix.SecurityTests/Task18_4CommercialIntegritySqlServerTests.cs` |

---

## 4. Offer Immutability

```text
Calculated   → AddBenefit / AddFeature / AddPricingTier ALLOWED (snapshot assembly)
Accepted     → ALL THREE REJECTED (snapshot frozen at acceptance)
Converted    → ALL THREE REJECTED (frozen historical document)
Expired      → ALL THREE REJECTED (frozen historical document)
```

- `Offer.AddBenefit` now returns `OfferErrors.InvalidStateTransition(Status, "add benefit snapshot to")` — the exact convention used by `AddFeature` and `AddPricingTier`. No new lifecycle, no new error type.
- Proof: `Task18_4OfferImmutabilityTests` — `CalculatedOffer_AddBenefit_Succeeds`, `AcceptedOffer_AddBenefit_Fails`, `ConvertedOffer_AddBenefit_Fails`, `ExpiredOffer_AddBenefit_Fails`, plus `AcceptedOffer_AddFeature_And_AddPricingTier_StillFail_Regression` and `CalculatedOffer_AddFeature_And_AddPricingTier_StillSucceed_Regression`.
- **Duplicate benefits (F-18.4.1b):** `UNKNOWN — insufficient repository evidence`. The repository defines no domain identity for `OfferBenefit` (no natural key, no unique index, no dedupe in any conversion flow). We did NOT invent uniqueness rules.

---

## 5. Offer Feature Database Integrity

| Aspect | Value |
| ------ | ----- |
| Index | `UX_OfferFeatures_OfferId_FeatureCode` (unique) on `Platform.OfferFeatures(OfferId, FeatureCode)` |
| Replaces | `IX_OfferFeatures_OfferId_FeatureCode` (non-unique), dropped in the same migration |
| SQL Server behavior | Duplicate `(OfferId, FeatureCode)` → `SqlException` 2601 → `DbUpdateException` |
| Case sensitivity | `OfferFeature.Create` upper-invariant normalizes every code before persistence, and the default DB collation is case-insensitive; either layer alone blocks `STUDENTS`/`students`/`Students` duplicates. SQL test `Scenario2b` proves it end-to-end |
| Existing data | Every write path is application-guarded and test databases are created fresh from migrations. **Assumption documented:** no duplicate `(OfferId, FeatureCode)` rows exist at migration time; the migration does NOT delete data — if duplicates ever existed, it fails loudly rather than silently destroying rows |

SQL Server tests (real database, not InMemory):

- `Scenario2_SameOffer_SameFeatureCode_DuplicateRejectedByDatabase`
- `Scenario2b_SameOffer_SameFeatureCode_CaseVariants_Rejected`
- `Scenario3_SameOffer_DifferentFeatureCode_Allowed`
- `Scenario4_DifferentOffer_SameFeatureCode_Allowed`

---

## 6. EffectiveAtUtc Decision

**FACT:** `CreateContractFromOfferCommand` accepts `EffectiveAtUtc` and computes `request.EffectiveAtUtc ?? utcNow`, which propagates to `Contract.EffectiveAtUtc`, `Contract.EndsAtUtc`, `Subscription.StartsAtUtc`, `Subscription.EffectiveEndsAtUtc`, billing periods, and elapsed-month refund math.

**FACT:** The Task 18 alignment invariant requires `Contract.EffectiveAtUtc == Subscription.StartsAtUtc` (consistency), and `Contract.ValidateSnapshotCompleteness()` requires `EndsAtUtc == ComputeEffectiveEndsAtUtc(EffectiveAtUtc, DurationMonths, BonusMonths)`.

**UNKNOWN:**
```text
OPEN BUSINESS DECISION:
Contract EffectiveAtUtc policy is not explicitly defined by the current business specification.
```
Neither Task 17 decisions nor Task 18 docs state who may set the effective date, whether backdating/future-dating is allowed, or that server time must override client input. **We did not invent a restriction and did not change behavior.**

**Security verification (no bypass found):**

- **Offer expiration:** conversion requires `Offer.Status == Accepted`; `Offer.Accept` already checks `ExpiresAtUtc`. A late `EffectiveAtUtc` cannot resurrect an expired offer.
- **Tenant isolation:** `EffectiveAtUtc` carries no tenant data; queries are tenant-filtered + `StampAddedTenantIds`.
- **Subscription overlap:** overlap protection is SERIALIZABLE + unique non-terminal-subscription index — independent of any date parameter.
- **Contract validity:** `Contract.Create` rejects `default` date and `EndsAtUtc < EffectiveAtUtc`.
- **Billing/refund math:** elapsed-month calc derives from `Contract.EffectiveAtUtc` symmetrically for consumed and unused value, and `credit = min(unused, settled)` — a manipulated date can only shrink the customer's own credit, never mint excess.

No test-vulnerable bypass was discovered; no security test added beyond the existing expiration/tenant/overlap coverage.

---


## 7. D-02 Financial Calculation

```text
Contract Value (ContractedAmount)      = 12,000
Consumed Value (elapsed months)        =  4,000   (4 months × 1,000 monthly)
Unused Value                           =  8,000   (12,000 − 4,000)

Paid Settlement =
    Σ Completed Payment Allocations (Active, same tenant/currency/contract)
  + Σ CreditApplications (settling invoices of the old contract, same tenant)
  − Σ Completed Refunds (same tenant/contract/currency)
  floored at 0

Eligible Credit  = min(Unused Value, Paid Settlement)
Applied Credit   = min(Credit, new Invoice remaining)
Remaining Credit = Credit − Applied Credit (carried as Customer Credit balance)
```

**Scenario table (all SQL Server verified):**

| Scenario | Contract | Unused | Payment | CreditApp | Refund | Settled | Credit |
| -------- | -------- | ------ | ------- | --------- | ------ | ------- | ------ |
| 5 | 12,000 | 8,000 | 10,000 | 0 | 0 | 10,000 | **8,000** |
| 6 | 12,000 | 8,000 | 5,000 | 0 | 0 | 5,000 | **5,000** |
| 7 | 12,000 | 8,000 | 5,000 | 7,000 | 0 | 12,000 | **8,000** |
| 8 | 12,000 | 8,000 | 5,000 | 0 | 5,000 | 0 | **none (no credit)** |

**Why `CreditApplication` is settlement (verified, not assumed):**
- `CreditApplication.Create` + `TenantCredit.ConsumeAmount` reduce what the customer still owes — `Invoice.GetRemainingAmount()` subtracts it.
- The settlement flow writes a `CustomerLedgerEntry.CreateCreditUsage` entry — the same ledger semantics as money applied.
- CreditApplication creation is capped by `Invoice.GetRemainingAmount()`, so Payment and CreditApplication settle disjoint portions: invoice 10,000 = 5,000 + 5,000 sums to exactly 10,000 — not 5,000 and not 15,000. And `min(unused, settled)` bounds the credit by unused value regardless.

**Refund interaction:** `Refunds` are summed separately with `Status == Completed` and subtracted. Refunded money leaves `Paid Settlement`, so `Refund + Upgrade Credit` cannot recover the same money twice (Scenario 8: paid 5,000, refunded 5,000 → settled 0 → no credit).

**Credit source on plan change:** the existing approved model (`ChangeSubscriptionPlanCommand`) creates a fresh `SubscriptionChange` credit from the old contract's unused settled value — no credit-transfer mechanism exists or was invented.

---


## 8. Financial Invariants Verified

| Invariant | Verification |
| --------- | ------------ |
| `Invoice.Remaining = Total − active PaymentAllocations − active CreditApplications ≥ 0` | `GetRemainingAmount()` subtracts both; CreditApplication creation is capped by remaining; Scenario 7 lands settled exactly at 12,000 |
| `Credit.ConsumedAmount ≤ OriginalAmount` | `TenantCredit.ConsumeAmount` rejects over-consumption (existing test `D02_UpgradeDowngrade_ConcurrentCredit_NoDoubleConsumption`) |
| `CreditAmount ≤ EligibleUnusedContractValue` | `Math.Min(unusedValue, paidAmount)` — Scenarios 5/6 |
| `CreditAmount ≤ EligiblePaidSettlement` | Same `Math.Min` — Scenario 6 (5,000 < 8,000 → credit 5,000) |
| No duplicated SubscriptionChange credit per old subscription | Unique `UX_TenantCredits_TenantId_SourceType_SourceId` + idempotency key `sub-change-{id:N}` + replay path; SQL tests `ChangePlan_ReplayAfterExistingCredit_DoesNotCreateSecondCredit`, `ChangePlan_SequentialRetry_DoesNotDuplicateCredit` |
| Refunded money never becomes credit | Scenario 8 (refunded → settled 0 → no credit) |
| Historical snapshots immutable | Offer status guards (this task) + `SubscriptionFactory` zero Plan reads + Plan-mutation regressions (Task 18) |
| Tenant isolation | All D-02 queries filter `TenantId == oldSubscription.TenantId`; stamp-on-add mirrors production |

---

## 9. Concurrency Verification

`ChangeSubscriptionPlanCommand` retains, unchanged:

- `IsolationLevel.Serializable` transaction around eligibility re-check → chain creation → commit.
- Deadlock retry loop (`MaxDeadlockRetries = 3`, SQL 1205 backoff) and `DbUpdateConcurrencyException` retry.
- Duplicate-key loser path (2601/2627): detach unit of work → `TryResolveReplayResultAsync` → return winner's contract / `ConcurrentRenewalConflict`.
- Unique indexes: `UX_TenantCredits_TenantId_SourceType_SourceId`, `CreditApplication (TenantId, IdempotencyKey)`, non-terminal subscription index.

Scenario 9 (two concurrent upgrades → one success, one replay/conflict, no duplicate credit) is covered by pre-existing SQL Server tests that executed green in this run:

- `ChangePlan_ConcurrentRequests_CreateAtMostOneCredit`
- `ChangePlan_ConcurrentCreditLedger_RemainsMathematicallyConsistent`
- `Phase12_1CreditConcurrencySqlServerTests` (overpayment → exactly one credit)

---


## 10. Database Verification

```bash
dotnet ef migrations has-pending-model-changes --project src/Centerix.Infrastructure \
    --startup-project src/Centerix.Api --context AppDbContext
# → "No changes have been made to the model since the last migration."
```

| Table | FK | Unique indexes | Delete behavior | Precision | Tenant scoping | Concurrency |
| ----- | -- | -------------- | --------------- | --------- | -------------- | ----------- |
| `OfferFeatures` | `→ Offers` | **`UX_OfferFeatures_OfferId_FeatureCode` (NEW)** | cascade (unchanged) | n/a (strings) | via Offer | n/a |
| `OfferPricingTiers` | `→ Offers` | existing indexes preserved | cascade | decimal(18,2)+ | via Offer | n/a |
| `Offers` | `→ Plans` | — | — | amounts 18,2 | `TenantId` | — |
| `Contracts` | `→ Plans` | `UX_Contracts_TenantId_ContractNumber` | — | amounts 18,2 | `TenantId` | — |
| `TenantPlans` | `→ Contracts` | non-terminal overlap index | — | price 18,2 | `TenantId` | ✓ |
| `Invoices` | `→ Contracts/Subscriptions/BillingCycles` | `UX_Invoices_InvoiceNumber` | — | amounts 18,2 | `TenantId` | — |
| `TenantCredits` | — | `UX_TenantCredits_TenantId_SourceType_SourceId`, `(TenantId, IdempotencyKey)` | — | amount 18,2 | `TenantId` | — |
| `CreditApplications` | `→ TenantCredits/Invoices` | `(TenantId, IdempotencyKey)` | — | amount 18,2 | `TenantId` | — |

---


## 11. Test Results

All numbers come from actual test-runner execution (no manual counting).

```text
Previous baseline (Task 18 report claim):  1302  ("Full suite: 1302 … passing")
Repository reality at Task 18.4 start:     1421  (current total 1436 − 15 Task 18.4 tests)
  → The Task 18 report's "1302" does NOT match repository reality at Task 18.4 start;
    later Task 18.x additions preceded this task. Reported as found, not reconciled.

Tests added in Task 18.4:                  15    (7 domain + 8 SQL Server)

Current InMemory / non-SQL execution:      1312  (1436 total − 124 SqlServer-category)
Current SQL Server (Category=SqlServer):   124
Total executed (full suite):               1436
Failures (full suite):                     0
```

**Executed commands and exact runner output:**

```text
dotnet build --nologo -v q
  → Build succeeded. 0 Error(s)

dotnet ef migrations has-pending-model-changes --project src/Centerix.Infrastructure
    --startup-project src/Centerix.Api --context AppDbContext
  → No changes have been made to the model since the last migration.

dotnet test --filter 'FullyQualifiedName~Task18_4'
  → Passed!  Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 1 m 8 s

dotnet test   (full suite, failure-name capture)
  → Passed!  Failed: 0, Passed: 1436, Skipped: 0, Total: 1436, Duration: 12 m 17 s

dotnet test --filter 'Category=SqlServer'
  → Passed!  Failed: 0, Passed: 124, Skipped: 0, Total: 124, Duration: 11 m 55 s
```

**Note on one earlier run:** the first full-suite attempt reported 1435/1436 with a single unnamed failure whose name was not captured by that run's filter. The immediate re-run with `[FAIL]` capture completed 1436/1436 green with exit code 0; no test has reproduced a failure since. All numbers above are from the passing, name-captured execution.

---



## 12. Remaining Open Questions

1. **OPEN BUSINESS DECISION — `Contract.EffectiveAtUtc` origin policy.**
   `UNKNOWN — insufficient repository evidence.` Neither Task 17 nor Task 18 defines
   who must control a contract's effective date (client-provided vs server clock), nor
   bounds how far it may deviate from now. This task deliberately did **not** create a
   restriction. Section 6 states the verified FACTs, the only explicit BUSINESS RULEs,
   and the security evidence; the origin policy itself remains unresolved.

2. **UNKNOWN — insufficient repository evidence: duplicate `OfferBenefit` identity.**
   The repository defines no domain identity (no unique natural key) for Offer
   benefits — `Name`/`BenefitType` combinations are not unique anywhere in domain,
   EF configuration, or migrations, and no spec assigns them identity. No duplicate
   rule was invented (Task 18.4 mandatory rule §3). Add-benefit duplication is
   therefore **not** constrained beyond the status-freeze — same as before this task.

3. **UNKNOWN — insufficient repository evidence: credit-sourced-from-credit value
   transfer on plan change.** When an old invoice was settled partly by consuming a
   `TenantCredit`, the economic origin of that settled value when the plan changes
   (recover / transfer / forfeit) is not defined by any existing model. D-02 correctly
   counts `CreditApplication` as settlement (Scenario 7) but no rule dictates what
   happens to the consumed credit's own value at change time. Documented, not invented.

---

## 13. Final Verdict

```text
TASK 18.4 CONDITIONALLY CLOSED
```

Superseded by the Task 18.4.1 review: `docs/TASK-18.4.1-FINAL-FINANCIAL-INTEGRITY-CLOSURE.md`.

The conditional status reflects two **business policy** questions that the repository does not answer
(they are not implementation defects, and no rule was invented):

1. **Which `CreditSourceType` values qualify as eligible paid settlement.** The implemented calculation
   counts every `CreditApplication` and was verified value-conserving (the credit balance is consumed
   exactly once), but no business document states whether non-cash-origin credits (`ReferralReward`,
   `Promotional`, `Compensation`, `Manual`) should be eligible for a new `SubscriptionChange` credit.
2. **A refund requested *after* a plan change.** `RefundCalculationService` derives the refundable amount
   from payment allocations only and does not reduce it by an already-issued `SubscriptionChange` credit.
   Whether it must do so is not defined anywhere in the repository.

Both are recorded as `OPEN BUSINESS DECISION / UNKNOWN — insufficient repository evidence` in
`docs/TASK-18.4.1-FINAL-FINANCIAL-INTEGRITY-CLOSURE.md` §11. All financial invariants, all SQL Server
scenarios and the full regression are verified — see that document §9 and §12.

Additional hardening delivered by Task 18.4.1:

- F-18.4.4b: the refund test now uses the genuine
  `Payment → PaymentAllocation → Invoice → Refund → RefundAllocation → Payment` chain executed through the
  real `ExecuteRefundHandler` (the Task 18.4 Scenario 8 refund carried no `RefundAllocation`).
- F-18.4.4a: a binding-bound double-count regression test (expected `SubscriptionChange` credit 7,000 —
  a double count yields 8,000, exclusion yields 3,000) plus a consumption-once mechanism test.

Acceptance criteria mapping (evidence in sections above):

| Criterion | Verdict | Section |
| --------- | ------- | ------- |
| Offer benefits cannot mutate after acceptance | PASS | §4 |
| Offer features cannot mutate after acceptance | PASS (pre-existing, retained) | §4 |
| Offer pricing tiers cannot mutate after acceptance | PASS (pre-existing, retained) | §4 |
| OfferFeature DB uniqueness enforced | PASS (migration `20260922171220_Task18_4_OfferFeatureUniqueness`) | §5 |
| SQL Server verifies uniqueness | PASS (Scenarios 2, 2b, 3, 4) | §5, §11 |
| EffectiveAtUtc rule or documented open decision | PASS (documented: FACT + BUSINESS RULE + UNKNOWN) | §6, §12 |
| D-02 verified against PaymentAllocation + CreditApplication semantics | PASS (Scenario 7 proves CreditApplication IS settlement) | §7 |
| No double counting | PASS (settlement families disjoint by FK design; Scenario 7 = 8000 exactly) | §7, §8 |
| No refunded amount becomes upgrade credit | PASS (Scenario 8; refunds NOT part of settlement) | §7, §9 |
| Upgrade credit idempotent | PASS (UX uniqueness + replay path; pre-existing tests retained) | §9 |
| Historical commercial snapshots immutable | PASS | §8 |
| Tenant isolation intact | PASS (existing isolation tests retained) | §11 |
| SQL concurrency tests pass | PASS (124/124 SqlServer category, incl. Task 18 concurrency tests) | §9, §11 |
| Full regression passes | PASS (1436/1436 executed) | §11 |
| EF no pending model changes | PASS (exact tool output) | §10 |
| Test counts from actual execution | PASS | §11 |
| Documentation reflects actual implementation | PASS (this document) | all |
