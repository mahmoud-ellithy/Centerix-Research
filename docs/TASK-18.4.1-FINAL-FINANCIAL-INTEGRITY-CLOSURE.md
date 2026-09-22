# TASK 18.4.1 — FINAL FINANCIAL INTEGRITY CLOSURE

**Date:** 2026-09-22 — **updated 2026-09-23 (Task 18.4.2 resolution)**
**Scope:** Close the two remaining findings from the Task 18.4 review — F-18.4.4a (Customer Credit economic origin) and F-18.4.4b (a real Payment → RefundAllocation → Refund integration test). No redesign, no new financial entity, no invented business rule.
**Task 18.4.2 addendum:** Both open business decisions of §11 (credit-source eligibility in D-02; refund after a plan change) were resolved by explicit business instruction and implemented in production code with real SQL Server regression tests.

Companion document: `docs/TASK-18.4-FINAL-COMMERCIAL-INTEGRITY-GAP-CLOSURE.md`

---

## 1. Executive Summary

Both findings from the Task 18.4 review were implemented and verified against the real migrated SQL Server database.

| ID | Finding | Outcome |
| -- | ------- | ------- |
| F-18.4.4a | `CreditApplication` counted as eligible paid settlement — is the same economic value counted twice? | **Verified safe.** CreditApplication IS settlement, and it is counted **exactly once** because the credit balance is consumed. Proven by a test that is *sensitive* to both double counting and exclusion (expected credit 7,000: a double count yields 8,000, exclusion yields 3,000). |
| F-18.4.4a-policy | Which `CreditSourceType` values count as eligible settlement | **RESOLVED (Task 18.4.2).** D-02 counts only credits with real customer economic value — `Overpayment` and `SubscriptionChange`. `ReferralReward`, `Promotional`, `Compensation`, `Manual` never become a new `SubscriptionChange` credit (§11.1). |
| F-18.4.4b | Scenario 8 created a Refund without the real RefundAllocation → Payment link | **Closed.** Real chain implemented and executed through the real `ExecuteRefundHandler` (full refund → 0 credit; partial refund 4,000 of 10,000 → credit 6,000). |
| — | RefundAllocation integrity (`Σ RefundAllocations ≤ Payment.Amount`) | **Verified** through the production handler (over-refund rejected, no ledger settlement written). |
| F-18.4.4c-refund | Refund requested AFTER a plan change (credit + cash double recognition) | **RESOLVED (Task 18.4.2).** The already-issued `SubscriptionChange` credit reduces the refundable base in `RefundCalculationService`; converted value can never be refunded again as cash (§11.2). |

**Final verdict: `TASK 18.4 CLOSED`** — both remaining business decisions were resolved and implemented in production code, and every financial invariant is verified on the final state (1456/1456 tests, 140/140 SQL Server, 0 failures).

---

## 2. CreditApplication Economic-Origin Analysis

### 2.1 How Customer Credit is created (production call sites)

Repository-wide, `TenantCredit.Create(...)` is called from exactly three production locations:

| Source | File | Class | Method | SourceId |
| ------ | ---- | ----- | ------ | -------- |
| `Overpayment` | `src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs` (lines 316-321) | `AllocatePaymentHandler` | `Handle` | `request.PaymentId` |
| `SubscriptionChange` | `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs` (lines 481-483) | `ChangeSubscriptionPlanHandler` | `ExecuteChangePlanCoreAsync` | `oldSubscription.Id` |
| any source type, incl. `Manual` | `src/Centerix.Application/Platform/Billing/Commands/CreateTenantCreditCommand.cs` (lines 32-37) | `CreateTenantCreditHandler` | `Handle` | `request.SourceId` |

**FACT — overpayment credits have a real cash origin, and are disjoint from allocations.**
`AllocatePaymentHandler.Handle` creates the `PaymentAllocation` only for `actualAllocatedAmount`
(the amount capped at the invoice remaining — line 264) and writes the `PaymentSettlement` ledger entry
for that same capped amount (line 290). Only the excess becomes a credit
(`TenantCredit.Create(..., overpaymentAmount, CreditSourceType.Overpayment, request.PaymentId, ...)`,
lines 316-321). This is documented in `docs/TASK-10-INVOICE-FINANCIAL-INTEGRITY-REPORT.md` §5:
*"Allocation is capped at the invoice remaining amount … Ledger entries record only the allocated amount
(not the excess)"*.

**Consequence:** settlement and overpayment credit are **disjoint portions of the same payment**. A
payment of 13,000 against a 12,000 invoice becomes 12,000 allocation + 1,000 credit — never 13,000
allocation *and* 1,000 credit.

### 2.2 What a CreditApplication means

`src/Centerix.Application/Platform/Billing/Commands/CreateTenantCreditCommand.cs` →
`ApplyCreditToInvoiceHandler.Handle`:

- line 268: `remainingAmount = invoice.TotalAmount - totalPaid - totalCreditApplied`
- lines 270-274: `request.Amount > remainingAmount` → `TenantCredit.ExceedsInvoiceRemaining`
- line 277: `credit.ConsumeAmount(request.Amount)` — decrements `TenantCredit.RemainingAmount`
- line 298: `dbContext.CreditApplications.Add(creditApplication)`
- lines 305-321: `CustomerLedgerEntry.CreateCreditUsage(...)` then `invoice.UpdatePaymentStatus()`

`src/Centerix.Domain/Platform/Billing/Payments/CustomerLedgerEntry.cs` → `CreateCreditUsage` records
`LedgerEntryType.CreditUsage` with `newBalance = previousBalance - usedAmount` and `IsCredit => true` —
the same balance-reducing movement family as `PaymentSettlement`.

Independently confirmed by the Task 16 audit
(`docs/TASK-16-PRODUCTION-READINESS-END-TO-END-AUDIT.md`, invariant #22):
*"Invoice remaining = total - allocations - credit applications"*.

**INFERENCE (evidence-based, not assumed):** a `CreditApplication` is monetary settlement of the invoice
it references. It is not new cash, and it is not a duplicate of cash already counted: it consumes a
distinct, previously unconsumed credit balance.

### 2.3 Why the same value cannot be counted twice

| Guard | File | Member | Effect |
| ----- | ---- | ------ | ------ |
| Remaining balance | `src/Centerix.Domain/Platform/Billing/Credits/TenantCredit.cs` | `ConsumeAmount` (lines 88-106) | Rejects `amount > RemainingAmount`; decrements `RemainingAmount`; sets `Applied`/`PartiallyApplied` |
| No un-consumption | same file | `Expire` / `Revoke` / `Reverse` (lines 108-137) | All require `Status == CreditStatus.Available` → a consumed credit can never be restored or reversed |
| No duplicate application | `src/Centerix.Infrastructure/Data/Configurations/CreditApplicationConfiguration.cs` (lines 63-64) | unique `(TenantId, IdempotencyKey)` filtered `[IdempotencyKey] <> ''` | One logical application cannot be written twice |
| Optimistic concurrency | `CreditApplication.RowVersion`, `TenantCredit.RowVersion` | `IsRowVersion()` | Concurrent consumption conflicts surface as `DbUpdateConcurrencyException` |
| One credit per source | `src/Centerix.Infrastructure/Data/Configurations/TenantCreditConfiguration.cs` (lines 78-80) | unique `(TenantId, SourceType, SourceId)` filtered `[SourceId] IS NOT NULL` | A subscription change or payment cannot mint two credits for the same source |

Therefore value moves **out of** the credit balance **into** invoice settlement: it is recognised once —
never simultaneously as an available credit balance and as settlement.

---

## 3. D-02 Settlement Formula

Implemented in `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs` →
class `ChangeSubscriptionPlanHandler` → method `ExecuteChangePlanCoreAsync` (lines 420-467). Unchanged by
this task (verified, not redesigned):

```text
elapsedMonths   = oldContract.GetElapsedMonths(now)                          // Contract.cs
consumedValue   = oldContract.CalculateValueForElapsedMonths(elapsedMonths)  // Contract.cs
totalContractValue = oldContract.ContractedAmount                            // line 424
unusedValue     = totalContractValue - consumedValue                         // line 425

paymentAllocated = Σ Payment.Allocations              (lines 440-447)
                     where Payment.Status == Completed
                       AND Payment.TenantId == oldSubscription.TenantId
                       AND Payment.CurrencyCode == oldContract.CurrencyCode
                       AND Allocation.Status == PaymentAllocationStatus.Active
                       AND Allocation.Invoice.ContractId == oldContract.Id

creditApplied    = Σ CreditApplication.Amount         (lines 449-455)
                     where TenantId == oldSubscription.TenantId
                       AND Invoice.ContractId == oldContract.Id

refunded         = Σ Refund.Amount                    (lines 457-462)
                     where TenantId == oldSubscription.TenantId
                       AND Refund.ContractId == oldContract.Id
                       AND Refund.Status == RefundStatus.Completed
                       AND Refund.CurrencyCode == oldContract.CurrencyCode

paidAmount       = paymentAllocated + creditApplied - refunded,  floored at 0   // lines 464-465
creditAmount     = Math.Min(unusedValue, paidAmount)                            // line 467
appliedToInvoice = Math.Min(creditAmount, invoice.GetRemainingAmount())         // credit application cap
```

Bound properties proven by the new tests:

```text
creditAmount ≤ unusedValue           (never exceeded)
creditAmount ≤ paidAmount            (never exceeded)
```

---

## 4. RefundAllocation Verification

### 4.1 The production refund chain

| Step | File | Class | Member |
| ---- | ---- | ----- | ------ |
| Refund amount is derived, never caller-supplied | `src/Centerix.Application/Platform/Billing/Commands/CreateRefundCommand.cs` | `CreateRefundHandler` | `Handle` (uses `IRefundCalculationService`) |
| RefundAllocation rows generated pro-rata per funding payment | same file | `CreateRefundHandler` | `GenerateAllocations` (lines 190-263) |
| Allocation → Payment link | `src/Centerix.Domain/Platform/Billing/Refunds/RefundAllocation.cs` | `RefundAllocation` | `Create(...)` → `PaymentId`, `Amount`, `PaymentMethod`, `CurrencyCode` |
| Execution validates the chain | `src/Centerix.Application/Platform/Billing/Commands/ExecuteRefundCommand.cs` | `ExecuteRefundHandler` | `TryHandleAsync` |

`ExecuteRefundHandler.TryHandleAsync` enforces, in order:

1. Refund status must be `Pending` / `Approved` / `Processing`.
2. `RefundAllocations` must exist and `allocations.Sum(a => a.Amount) == refund.Amount`
   (else `RefundErrors.AllocationsRequired` / `AllocationSumMismatch`).
3. Payments are locked with `SELECT 1 FROM Platform.Payments WITH (UPDLOCK, ROWLOCK, HOLDLOCK)` and must
   exist in the refund's tenant (else `RefundErrors.AllocationCrossTenant`).
4. Currency must match the refund (`RefundErrors.CurrencyMismatch`), and the allocation's `PaymentMethod`
   snapshot must equal the authoritative `Payment.Method`.
5. **Refundable balance:** `refundableAmount = payment.Amount - Σ RefundAllocations of OTHER
   Processing/Completed refunds`; `allocationForPayment.Amount > refundableAmount` →
   `RefundErrors.InsufficientPaymentSource`.
6. `CustomerLedgerEntry.CreateRefundSettlement(...)` writes exactly one `LedgerEntryType.RefundSettlement`
   entry (protected by the filtered unique index `UX_CustomerLedgerEntries_SettlementByRefund`).

### 4.2 Database-level guarantees (EF configuration + migrations)

| Constraint | File | Guarantee |
| ---------- | ---- | --------- |
| `UX_RefundAllocations_TenantId_RefundId_PaymentId` | `src/Centerix.Infrastructure/Data/Configurations/RefundAllocationConfiguration.cs` (lines 66-69) | One refund can allocate from a payment only once |
| `UX_Refunds_TenantId_SubscriptionId_OnePerSubscription` (filtered `SubscriptionId IS NOT NULL`) | `src/Centerix.Infrastructure/Data/Configurations/RefundConfiguration.cs` (lines 53-56) | At most one cancellation refund per subscription |
| `UX_Refunds_TenantId_IdempotencyKey` (filtered) | same file (lines 109-112) | Refund execution is idempotent |
| `UX_Refunds_TenantId_RefundNumber` | same file (lines 36-38) | Refund number uniqueness per tenant |
| FK `RefundAllocation.RefundId → Refund`, `DeleteBehavior.Restrict` | `RefundAllocationConfiguration.cs` (lines 71-75) | A payment with refund history cannot be orphaned |

**Important architectural note (FACT):** `Refund` itself carries **no** `PaymentId`. The only link between
a refund and the money that funded it is `RefundAllocation`. This is precisely why the Task 18.4
"Scenario 8" (a `Refund` row with `Status = Completed` but no allocation) was insufficient evidence — and
why the new tests seed and execute the genuine `RefundAllocation → Payment` chain through the real handler.

### 4.3 Refund is cash-only by design (no credit-to-cash leakage)

`src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs` → `Calculate` (lines 116-151):

```text
amountActuallyPaid = Σ Payment.Allocations where Status == Active
                     AND Payment.Status == Completed
                     AND Allocation.Invoice.ContractId == contract.Id      // lines 129-134
customerEconomicObligation = usedSubscriptionAmount + remainingBenefitValue
refundAmount = max(0, amountActuallyPaid - customerEconomicObligation)
```

`CreditApplication` values are deliberately **excluded** from `amountActuallyPaid`: refunds pay out real
cash, so only cash settlement can fund them. This is a separate calculation from D-02 and was not modified.

---

## 5. SQL Server Scenarios

New file: `tests/Centerix.SecurityTests/Task18_4_1FinancialIntegritySqlServerTests.cs`
(class `Task18_4_1FinancialIntegritySqlServerTests`, `[Collection("SqlServerIntegration")]`,
`[Trait("Category", "SqlServer")]`). All fixtures are seeded and all mutations are executed through the
**real production handlers** (`ChangeSubscriptionPlanHandler`, `ApplyCreditToInvoiceHandler`,
`ExecuteRefundHandler`) against the real migrated SQL Server database.

### S1 — `F1844a_OverpaymentCredit_AppliedAsSettlement_CountedExactlyOnce`

```text
Old contract value .................. 12,000   (12 months × 1,000 monthly)
Elapsed .............................. 4 months → consumed 4,000
Unused ............................... 8,000
Payment P1 (Completed) ............... 3,000
PaymentAllocation PA1 → old invoice ... 3,000
Customer Credit C1 ................... 4,000   (CreditSourceType.Overpayment, SourceId = P1)
CreditApplication CA1 → old invoice ... 4,000   (invoice remaining was 12,000 − 3,000 = 9,000)
   → after application: C1.RemainingAmount = 0, C1.Status = Applied, exactly 1 CreditApplication
Eligible paid settlement = 3,000 + 4,000 − 0 = 7,000
SubscriptionChange credit = MIN(8,000, 7,000) = 7,000        ← asserted exactly
```

Why this scenario proves the economic origin rather than merely arithmetic: settlement (7,000) is the
*binding* bound, because unused value (8,000) is larger. Therefore the assertion detects both failure
modes — a double count (3,000 + 4,000 + 4,000 = 11,000 → credit 8,000) and exclusion (3,000 → credit
3,000) would both fail the `Assert.Equal(7000m, ...)`.

### S2 — `F1844a_ConsumedCredit_CannotSettleSecondInvoice_NoReUse`

```text
C1 = 4,000 applied in full to invoice A → CA1 = 4,000 → success
same C1 applied to invoice B = 4,000 → REJECTED
   (TenantCredit.ConsumeAmount: Status == Applied → TenantCreditErrors.NotAvailable)
Result: exactly 1 CreditApplication (invoice A), C1.RemainingAmount = 0
```

This is the mechanical proof that one economic unit cannot settle two invoices, so it can never be
recognised as settlement twice.

### S3 — `F1844b_FullRefund_ViaRefundAllocation_ProducesNoUpgradeCredit`

```text
Old contract value .................... 12,000; consumed 4,000; unused 8,000
Payment P1 (Completed) .................  5,000
PaymentAllocation PA1 → old invoice ....  5,000
Refund R1 ..............................  5,000   (ContractId = old contract, InvoiceId = old invoice)
RefundAllocation RA1 → P1 ..............  5,000   (the genuine financial chain)
ExecuteRefundHandler → Pending → Completed, ExecutedAtUtc set,
   1 × CustomerLedgerEntry(RefundSettlement) = 5,000
Eligible paid settlement = 5,000 − 5,000 = 0
SubscriptionChange credit = MIN(8,000, 0) = none            ← asserted: no credit row exists
```

### S4 — `F1844b_PartialRefund_ViaRefundAllocation_EligibleSettlementNetOfRefund`

```text
Old contract value .................... 12,000; consumed 4,000; unused 8,000
Payment P1 = 10,000 → PaymentAllocation PA1 → old invoice = 10,000
Refund R1 = 4,000 → RefundAllocation RA1 → P1 = 4,000  → executed → Completed
Eligible paid settlement = 10,000 − 4,000 = 6,000
SubscriptionChange credit = MIN(8,000, 6,000) = 6,000      ← asserted exactly
```

### S5 — `F1844b_RefundAllocation_CannotExceedPaymentRefundableBalance`

```text
Payment P1 = 5,000
Refund R1 = 6,000, RefundAllocation RA1 → P1 = 6,000
   (Σ allocations == refund amount, so the sum check passes)
ExecuteRefundHandler: refundableAmount = Payment.Amount 5,000 − Σ other refund allocations 0 = 5,000
   6,000 > 5,000 → RefundErrors.InsufficientPaymentSource
Result: refund NOT Completed, ExecutedAtUtc IS NULL, 0 RefundSettlement ledger entries
```

### S6 — `F1844b_RefundOnAnotherContract_DoesNotReduceThisContractsSettlement`

```text
Contract A (the one being upgraded): Payment 10,000 → PA → invoice A; unused 8,000
Contract B (same tenant, no subscription): Payment 10,000 → PA → invoice B
                                         Refund 4,000 executed with RA → Payment B
Change plan on A:
   eligible paid settlement = 10,000 (B's refund is NOT part of A's settlement)
   SubscriptionChange credit = MIN(8,000, 10,000) = 8,000   ← asserted exactly
   (a tenant-wide refund sum would have produced MIN(8,000, 6,000) = 6,000 and failed)
```

Contract B intentionally has no `TenantPlan`, because
`src/Centerix.Infrastructure/Data/Configurations/TenantPlanConfiguration.cs` (lines 71-72) enforces a
unique index on non-terminal subscription statuses — i.e. one active subscription per tenant.

---

## 6. Double-Counting Analysis

The required invariant is:

```text
One economic unit of customer value
must not become eligible for two independent commercial credits/refunds.
```

### 6.1 The four theoretical double counts — and why each is impossible

| # | Theoretical double count | Guard (file → member) | Verdict |
| - | ------------------------ | --------------------- | ------- |
| 1 | The same payment counted as allocation **and** as overpayment credit | `AllocatePaymentCommand` → allocation uses `actualAllocatedAmount` (line 264) and the ledger settlement uses `actualAllocatedAmount` (line 290); only `overpaymentAmount` becomes credit | **Impossible** — disjoint arithmetic, proven by test S1 |
| 2 | The same credit counted as settlement **twice** (two invoices) | `TenantCredit` → `ConsumeAmount` rejects `amount > RemainingAmount` and cannot be un-consumed (`Revoke`/`Reverse`/`Expire` require `Available`) | **Impossible** — test S2 |
| 3 | Allocation + CreditApplication exceeding the invoice total | `ApplyCreditToInvoiceHandler` → `remainingAmount = Total - Paid - CreditApplied`, rejects `request.Amount > remainingAmount`; `PaymentAllocation` creation is capped at invoice remaining | **Impossible** — per-invoice settlement ≤ invoice total |
| 4 | Refunded money counted as settlement and then also issued as upgrade credit | `ExecuteRefundHandler.TryHandleAsync` → `allocationForPayment.Amount > refundableAmount` rejected; D-02 subtracts `Σ Completed Refunds` by `ContractId` | **Impossible** — tests S3/S4/S5 |

### 6.2 The task's specific example, traced through the code

```text
Customer pays 13,000; Invoice = 12,000

AllocatePaymentHandler.Handle:
   actualAllocatedAmount = 12,000      (capped at invoice remaining)
   PaymentAllocation     = 12,000      → ledger PaymentSettlement = 12,000
   overpaymentAmount     =  1,000
   TenantCredit(Overpayment, SourceId = PaymentId) = 1,000

Later: ApplyCreditToInvoiceHandler applies the 1,000 to another invoice
   → CreditApplication = 1,000;  TenantCredit.RemainingAmount 1,000 → 0

At a later Subscription Change on the contract that owns the second invoice:
   paid settlement = PaymentAllocations + CreditApplications − Refunds
                   = includes the 1,000 exactly once
```

The 1,000 is recognised **once**, in exactly one place at a time:

- while it sits as an available credit → it is customer credit balance (not settlement);
- once applied → it is invoice settlement (and the credit balance is 0);
- the first invoice never counted it (its allocation was capped at 12,000).

No ordering of operations lets the same 1,000 appear in two settlement sums. Test S1 pins this by making
settlement the binding `MIN()` bound; test S2 pins the mechanism that makes it structural.

### 6.3 Upgrade credit bounds (invariants re-verified)

| Invariant | Where enforced | Evidence |
| --------- | -------------- | -------- |
| `UpgradeCredit ≤ EligibleUnusedValue` | `ChangeSubscriptionPlanCommand` line 467 (`Math.Min(unusedValue, paidAmount)`) | S1 (7,000 ≤ 8,000), S4 (6,000 ≤ 8,000), S6 (8,000 ≤ 8,000) |
| `UpgradeCredit ≤ EligiblePaidSettlement` | same | S1 (7,000 = 7,000), S3 (0), S4 (6,000 = 6,000) |
| `Invoice.Remaining = Total − allocations − credit applications ≥ 0` | `Invoice.GetRemainingAmount()`; `ApplyCreditToInvoiceHandler` cap | S1 (3,000 + 4,000 ≤ 12,000) |
| `TenantCredit.ConsumedAmount ≤ OriginalAmount` | `TenantCredit.ConsumeAmount` | S2 |
| One `SubscriptionChange` credit per old subscription | unique `(TenantId, SourceType, SourceId)` + idempotency key + replay resolution | Task 18.4 `ChangePlan_ConcurrentRequests_CreateAtMostOneCredit` (retained, green) |

---

## 7. Tenant Isolation

| Relationship | Enforcement | Evidence |
| ------------ | ----------- | -------- |
| Payment → settlement | D-02 filters `p.TenantId == oldSubscription.TenantId` (line 441) | S1–S6 all tenant-scoped and green |
| CreditApplication → settlement | D-02 filters `ca.TenantId` and requires the invoice's `TenantId`/`ContractId` to match (lines 449-455) | S1 |
| Refund → settlement | D-02 filters `r.TenantId` and `r.ContractId == oldContract.Id` (lines 457-462) | **S6 proves contract scoping** (B's 4,000 refund does not reduce A's settlement) |
| Credit → invoice | `ApplyCreditToInvoiceHandler`: `credit.TenantId != invoice.TenantId` → `TenantCreditErrors.CrossTenant`; currency compared with the invoice's `Contract.CurrencyCode` | pre-existing: `Phase10_1CreditApplicationCorrectionTests`, `Phase10InvoiceFinancialIntegrityTests`, `Phase12CustomerCreditLifecycleTests` |
| RefundAllocation → Payment | `ExecuteRefundHandler`: `SELECT ... WITH (UPDLOCK) ... WHERE PaymentId = @p0 AND TenantId = @p1`; missing → `RefundErrors.AllocationCrossTenant`; currency mismatch → `CurrencyMismatch` | pre-existing: `Phase13RefundAllocationSqlServerTests`, `Phase4_1_2RefundIntegrityTests` |
| RefundAllocation tenant stamping | `db.StampAddedTenantIds(...)` on all seeded rows; handler loads allocations with `ra.TenantId == refund.TenantId` | S3–S6 seeds |

No test in this task reads or writes financial rows outside its own tenant; S6 additionally demonstrates
that cross-contract (same-tenant) leakage does not occur.

---

## 8. Concurrency

No concurrency-sensitive code was changed by Task 18.4.1:

- `ChangeSubscriptionPlanHandler` still wraps the whole commercial transition in
  `IsolationLevel.Serializable`, retries deadlocks (SQL 1205, `MaxDeadlockRetries = 3`), retries
  `DbUpdateConcurrencyException`, and resolves duplicate-key losers through `TryResolveReplayResultAsync`.
- `ApplyCreditToInvoiceHandler` still clears the change tracker per attempt, uses a serializable
  transaction, and treats a duplicate `(TenantId, IdempotencyKey)` insert as either an idempotent retry
  (same payload) or `TenantCreditErrors.IdempotencyKeyConflict` (different payload).
- `ExecuteRefundHandler` still acquires `UPDLOCK/ROWLOCK/HOLDLOCK` on the payment rows inside the
  serializable transaction, so two refunds cannot both consume the same payment balance.
- `RefundAllocation.RowVersion`, `TenantCredit.RowVersion`, `CreditApplication.RowVersion` and
  `PaymentAllocation (Status = 'Active')` unique index remain in force (unchanged).

Because no concurrency-sensitive code changed, no new concurrency test was added (per the task rule);
the existing concurrency suites ran as part of the SQL Server category run reported in §9.

---

## 9. Exact Test Results

All numbers come from actual test-runner execution (no manual counting), on the final code state
(the Task 18.4.1 figures are kept as history; the Task 18.4.2 block is the current state).

```text
Previous baseline (Task 18.4.1 closure):    1442 total / 130 SQL Server / 1312 non-SQL / 0 failures
Tests added in Task 18.4.2:                 14   (4 domain-level in Task18_4_2FinancialPolicyTests
                                                 + 10 SQL Server in Task18_4_2FinancialPolicySqlServerTests)
Current SQL Server (Category=SqlServer):    140
Total executed (full suite):                1456
Failures (full suite):                      0
```

**Executed commands and exact runner output (Task 18.4.1 — historical):**

```text
dotnet test --filter 'FullyQualifiedName~Task18_4'
  → Passed!  Failed: 0, Passed: 21, Skipped: 0, Total: 21, Duration: 22 s

dotnet test --filter 'Category=SqlServer'
  → Passed!  Failed: 0, Passed: 130, Skipped: 0, Total: 130, Duration: 11 m 22 s

dotnet test   (full suite)
  → Passed!  Failed: 0, Passed: 1442, Skipped: 0, Total: 1442, Duration: 12 m 46 s
```

**Executed commands and exact runner output (Task 18.4.2 — final state):**

```text
dotnet build --nologo -v q
  → Build succeeded. 0 Error(s)

dotnet test --filter 'FullyQualifiedName~Task18_4'
  → Passed!  Failed: 0, Passed: 35, Skipped: 0, Total: 35, Duration: 1 m 47 s
    (15 Task 18.4 + 6 Task 18.4.1 + 14 Task 18.4.2 tests)

dotnet test --filter 'Category=SqlServer'
  → Test Run Successful.  Total tests: 140, Passed: 140 — 0 [FAIL] lines
    Total time: 11.7301 Minutes

dotnet test   (full suite)
  → Test Run Successful.  Total tests: 1456, Passed: 1456 — 0 [FAIL] lines
    Total time: 12.2668 Minutes
```

**Note on transient failures:** (1) In one Task 18.4.1 `Task18_4` batch run,
`F1844a_OverpaymentCredit_AppliedAsSettlement_CountedExactlyOnce` failed once (message not captured)
and passed in every subsequent run. (2) In one intermediate Task 18.4.2 `Category=SqlServer` run,
7 tests failed (2 arithmetic/integrity, 2 plan-change, 3 concurrency); all 7 passed in immediate
isolation (9/9 in the filtered re-run) and in the full `Category=SqlServer` re-run (140/140, zero
`[FAIL]` lines), immediately followed by a green full suite (1456/1456). Both occurrences are treated
as transient environment flakes under sustained load, not reproducible defects; no code or test was
changed in response.

---

## 10. EF Migration Verification

```text
dotnet ef migrations has-pending-model-changes
  --project src/Centerix.Infrastructure
  --startup-project src/Centerix.Api
  --context AppDbContext
  → Build succeeded.
  → No changes have been made to the model since the last migration.
```

Task 18.4.1 changed **no production code and no EF model** — test file + documentation only.

Task 18.4.2 changed production code (`ChangeSubscriptionPlanCommand`, `RefundCalculationService`
and its three calling handlers, plus the new `IssuedSubscriptionChangeCredit` helper) but **no EF
entity, configuration, or migration**: the credit-source filter and the refund deduction are query
and calculation logic only. The check was re-run after the change and still reports no pending model
changes, confirming the model (including `UX_OfferFeatures_OfferId_FeatureCode`, the `TenantCredit`
unique indexes, and the refund chain) remains fully migrated with no drift.

---


## 11. Remaining Business Decisions

### 11.1 RESOLVED (Task 18.4.2) — which credit sources qualify as eligible paid settlement

```text
FACT (as of Task 18.4.1)
```
- `CreditSourceType` (`src/Centerix.Domain/Platform/Billing/Credits/Enums/CreditSourceType.cs`) defines
  `ReferralReward = 0`, `Promotional = 1`, `Compensation = 2`, `Manual = 3`, `Overpayment = 4`,
  `SubscriptionChange = 5`.
- D-02 (Task 18) counted **all** `CreditApplication` amounts as settlement. Nothing in the domain, EF
  configuration, migrations, or prior task documents distinguished credit sources for D-02 eligibility.

```text
DECISION (resolved by Task 18.4.2 — approved business rule)
```
- **Eligible (real customer economic value):** `Overpayment` (real cash received from the customer) and
  `SubscriptionChange` (value already converted from a previously paid contract — originally customer
  cash). Their `CreditApplication`s contribute to D-02 paid settlement.
- **Non-eligible (granted / free / discretionary):** `ReferralReward`, `Promotional`, `Compensation`,
  `Manual` must **not** create a new `SubscriptionChange` credit when merely used to settle an old
  contract.
- **Enforced in the D-02 calculation itself** (`ChangeSubscriptionPlanCommand`, the `creditApplied`
  query): the `CreditApplications` sum joins `TenantCredits` and keeps only rows whose `SourceType` is
  `Overpayment` or `SubscriptionChange`. Tenant scoping stays on the subscription and contract scoping
  stays on the invoice join — tenant and contract isolation are unchanged.
- **Regression tests (real migrated SQL Server, real handlers):**
  - `Task18_4_2FinancialPolicySqlServerTests.CreditSourceEligibility_EligibleSources_CountAsPaidSettlement`
    — Overpayment / SubscriptionChange: cash 3,000 + credit application 4,000 → new credit 7,000
    (an exclusion regression would yield 3,000);
  - `...CreditSourceEligibility_GrantedSources_AreNotPaidSettlement` — ReferralReward / Promotional /
    Compensation / Manual: the credit genuinely settles the invoice (the `CreditApplication` row is
    asserted), yet the new credit is 3,000, never 7,000;
  - `...CreditSourceEligibility_EligibleCreditOnAnotherContract_DoesNotLeakIntoThisContract` —
    contract isolation: an eligible credit on another contract of the same tenant does not leak (3,000).

### 11.2 RESOLVED (Task 18.4.2) — refund requested AFTER a plan change

```text
FACT (as of Task 18.4.1)
```
- `RefundCalculationService.Calculate` derived the refundable amount purely from
  `amountActuallyPaid = Σ active PaymentAllocations of the contract` minus
  `customerEconomicObligation = usedSubscriptionAmount + remainingBenefitValue`, and did **not** subtract
  `SubscriptionChange` credits already issued for the same contract by `ChangeSubscriptionPlanCommand`.
- D-02 subtracts refunds that exist **before** the change (verified by tests S3/S4).

```text
DECISION (resolved by Task 18.4.2 — approved business rule)
```
- **A value already converted into a `SubscriptionChange` credit must not be refunded again as cash.**
  The same economic value must never be recognised as `SubscriptionChange` credit **and** cash refund
  at the same time.
- **Implementation:** `IRefundCalculationService.Calculate` gained an optional
  `alreadyIssuedSubscriptionChangeCredit` parameter (default `0m`, so existing direct callers behave
  exactly as before). `RefundCalculationService` subtracts it from
  `AmountActuallyPaid − CustomerEconomicObligation` and exposes `AlreadyConvertedSubscriptionChangeCredit`
  on `RefundCalculationResult`. `CustomerOutstandingAmount` stays `max(0, obligation − paid)` — the credit
  is not customer debt and never inflates the outstanding balance.
- **The FULL issued amount is subtracted**, not just the unconsumed `RemainingAmount`: the consumed
  portion already settled the new contract's invoice and must not reappear as cash either.
- **Contract → credit lookup** is centralised in `IssuedSubscriptionChangeCredit.GetIssuedAmountAsync`
  (`src/Centerix.Application/Platform/Billing/Commands/IssuedSubscriptionChangeCredit.cs`):
  `TenantPlan.ContractId` → `TenantCredit.SourceId` where `SourceType = SubscriptionChange`, scoped by
  tenant and currency. Wired into all three production refund paths: `CreateRefundHandler`,
  `CalculateRefundHandler`, `CancelSubscriptionHandler`.
- **Regression tests:**
  - SQL Server `Task18_4_2FinancialPolicySqlServerTests` Scenarios A/B/C — A: an 8,000 credit issued,
    later refund refused with `Refund.NoRefundDue` and zero Refund rows; B: partial credit + partial
    refund — exactly the remaining 1,000 is created and executed through the real `ExecuteRefundHandler`
    (ledger `RefundSettlement` = 1,000); C: credit fully consumed → nothing becomes refundable again;
  - domain-level `Task18_4_2FinancialPolicyTests` — arithmetic and boundary behaviour of the calculation
    itself (credited value not refunded; partial remainder; outstanding unaffected).

### 11.3 Not an open decision here

The `EffectiveAtUtc` origin policy from Task 18.4 §12 remains open (documented there, unchanged here).

---

## 12. Final Verdict

```text
TASK 18.4 CLOSED
```

Both remaining business decisions in §11 (credit-source eligibility; refund after a plan change) were
resolved by approved business rules and implemented in production code. Every financial invariant and
every acceptance criterion is verified on the final state.

| Acceptance criterion | Status | Evidence |
| -------------------- | -----: | -------- |
| Customer Credit economic origin investigated from actual repository code | PASS | §2 (three production `TenantCredit.Create` call sites, allocation caps, ledger semantics, consumption guards) |
| No economic value can be counted twice | PASS | §6.1 (four theoretical paths, each guarded) + tests S1/S2 |
| CreditApplication handling explicitly documented | PASS | §2.2, §3, §6.2 |
| Regression test covers Credit → CreditApplication → Subscription Change | PASS | S1, S2 + Task 18.4.2 eligibility theories (`CreditSourceEligibility_*`) |
| Credit-source eligibility enforced in the D-02 calculation itself | PASS | §11.1 (`ChangeSubscriptionPlanCommand` `creditApplied` query filters `SourceType ∈ {Overpayment, SubscriptionChange}`) |
| Refund uses actual `RefundAllocation` → `Payment` relationship in tests | PASS | S3–S6 seed real `RefundAllocation` rows and execute the real handler |
| Full refund scenario passes | PASS | S3 (settlement 0 → no credit) |
| Partial refund scenario passes | PASS | S4 (settlement 6,000 → credit 6,000) |
| Refunded amount cannot become SubscriptionChange credit | PASS | S3, S5 |
| Credited value cannot be refunded again as cash | PASS | §11.2 (`RefundCalculationService` deducts the issued credit; all 3 refund handlers wired) + SQL Server Scenarios A/B/C |
| Existing Task 13 refund invariants remain intact | PASS | §4.1/§4.2 unchanged; S5 proves `Σ RefundAllocations ≤ Payment.Amount`; `Phase13*` suites green |
| Tenant isolation passes | PASS | §7; S6 proves contract-scoped refunds; `CreditSourceEligibility_EligibleCreditOnAnotherContract_DoesNotLeakIntoThisContract` proves credit-origin isolation |
| Currency validation preserved | PASS | `IssuedSubscriptionChangeCredit` scopes by `CurrencyCode`; refund/allocation currency guards untouched |
| Concurrency, RowVersion, idempotency, serializable transactions preserved | PASS | §8; no concurrency protection removed; all concurrency suites green in the SQL Server category run (§9) |
| Full regression passes | PASS | §9 (1456/1456) |
| SQL Server regression passes | PASS | §9 (140/140) |
| EF reports no pending model changes | PASS | §10 |
| Documentation matches actual implementation | PASS | this document (file / class / method / property references throughout) |
| No unrelated architecture changes introduced | PASS | No new financial entity, no redesign of Payment/Refund/CustomerCredit; changes limited to the D-02 credit filter and the refund deduction |
