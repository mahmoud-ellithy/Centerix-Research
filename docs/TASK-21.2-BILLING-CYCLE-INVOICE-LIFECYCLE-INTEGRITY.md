# TASK-21.2: BILLING CYCLE INVOICE LIFECYCLE INTEGRITY

## 1. Scope

Verify that `BillingCycle` invoices are derived **exclusively** from the immutable
`Offer → Contract → Subscription` snapshot, with no re-applied promotions, no double
discount, and correct invoice arithmetic — and correct the code where repository
evidence proves a defect.

| In | Out |
|----|-----|
| `Contract.GetSubscriptionSnapshot()` charge derivation | Refund calculation |
| `CreateInvoiceFromBillingCycleCommand` (cycle → invoice) | Credit lineage / payment allocation |
| Invoice arithmetic + traceability + lifecycle state | Subscription / Contract lifecycle rules |
| All 4 existing invoice-creation paths (verification only) | Promotion engine behaviour |
| New test suite (14 required + 4 extra scenarios) | Data-repair migrations (see §11.1) |
| This document | Task 22, authorization redesign |

---

## 2. Snapshot Semantics Verification (Task 21.2 §2)

Chain verified end-to-end: `PromotionCalculationService.Calculate` → `Contract.Create`
→ `Contract.GetSubscriptionSnapshot()` → `SubscriptionFactory.CreateFromSnapshotAsync`
→ `TenantPlan` → `BillingCycle` → `Invoice`.

| Snapshot field | Authoritative source | Status |
|---|---|---|
| `SnapshotPrice` | `Contract.MonthlyListPrice` (pre-discount list price, display only) | ✅ verified |
| `SnapshotMonthlyCharge` | `Contract.ContractedAmount / Contract.DurationMonths` (**post**-discount) | 🔧 **corrected** — see §3 |
| `DurationMonths` | `Contract.DurationMonths` (paid term) | ✅ verified |
| `BonusMonths` | `Contract.BonusMonths` | ✅ verified |
| `ChargedMonths` | `Contract.ChargedMonths ?? 0` (Pay-for-X) | ✅ verified |
| `SnapshotCurrency` | `Contract.CurrencyCode` (negotiated, not Plan) | ✅ verified |
| limits + features | `Contract` entitlement snapshot columns / `ContractFeature` rows | ✅ verified |
| Plan catalog reads on this path | **none** (`SubscriptionFactory` has no `Plans`/`PlanFeatures`/`Features` query) | ✅ verified |

Supporting evidence for the corrected meaning of `SnapshotMonthlyCharge`
(`src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs:61-66`):

```
/// Actual monthly charge after applying Contract-level discounts.
/// This is the authoritative monthly amount for BillingCycle invoice calculation.
/// For no-discount contracts: SnapshotMonthlyCharge = SnapshotPrice.
```

---

## 3. Confirmed Defect F-1 (fixed) — discount silently dropped from every cycle invoice

### 3.1 The defect

`src/Centerix.Domain/Platform/Contracts/Contract.cs:514` (`GetSubscriptionSnapshot`)
computed:

```csharp
var monthlyCharge = DurationMonths > 0 ? GrossAmount / DurationMonths : 0m;   // BEFORE
var monthlyCharge = DurationMonths > 0 ? ContractedAmount / DurationMonths : 0m; // AFTER (line 521)
```

`Contract.GrossAmount` is the **pre-discount** base (`ContractedAmount + DiscountAmount`,
validated in `Contract.Create`). Dividing it by the duration yields the *list* price, not
the charge — so the snapshot carried `SnapshotMonthlyCharge == SnapshotPrice` for every
discounted contract.

### 3.2 Why it was silent

`CreateInvoiceFromBillingCycleCommand.cs:50-53` derives:

```
Subtotal       = SnapshotPrice × duration
DiscountAmount = (SnapshotPrice − SnapshotMonthlyCharge) × duration     → evaluated to 0
TotalAmount    = SnapshotMonthlyCharge × duration                       → evaluated to gross
```

With `SnapshotMonthlyCharge == SnapshotPrice`, `DiscountAmount` became **0** and
`TotalAmount` became the **gross** amount. `Invoice.Create`'s arithmetic guard
(`|Total − (Subtotal − Discount + Tax)| ≤ 0.01`) still passed, because
`12000 − 0 == 12000`. The defect therefore produced a *mathematically consistent but
commercially wrong* invoice.

### 3.3 Concrete evidence (10 % off, 1 000/month × 12 months)

| Quantity | Before fix | After fix |
|---|---|---|
| `Contract.GrossAmount` | 12 000 | 12 000 |
| `Contract.DiscountAmount` | 1 200 | 1 200 |
| `Contract.ContractedAmount` | 10 800 | 10 800 |
| `Subscription.SnapshotPrice` | 1 000 | 1 000 |
| `Subscription.SnapshotMonthlyCharge` | **1 000 (wrong)** | **900** |
| Invoice `Subtotal` | 12 000 | 12 000 |
| Invoice `DiscountAmount` | **0 (wrong)** | **1 200** |
| Invoice `TotalAmount` | **12 000 (wrong, +1 200 billed)** | **10 800** |
| `TotalAmount == Contract.ContractedAmount` | ❌ | ✅ |

### 3.4 Supporting evidence chain

1. `TenantPlan.SnapshotMonthlyCharge` XML docs (post-discount, "authoritative monthly
   amount for BillingCycle invoice calculation", equals `SnapshotPrice` when no discount).
2. `CreateInvoiceFromBillingCycleCommand` discount formula only produces a non-zero
   discount if `SnapshotMonthlyCharge < SnapshotPrice`.
3. `docs/TASK-21.1.2-…CONSISTENCY.md` §3.3 worked example: `SnapshotPrice 1000`,
   discount 500/month → `SnapshotMonthlyCharge 500` → 3-month invoice total 1500.
   (Its §4.2/§.4.4 formula text `GrossAmount / DurationMonths` contradicts its own
   example and is superseded by this task.)
4. Repo-wide invariant asserted by existing tests (e.g. `Phase11PlanChangeTests`,
   `Task18CommercialIntegrityTests`): `Invoice.TotalAmount == Contract.ContractedAmount`.

### 3.5 Red → green evidence

The new suite was executed against the reverted (original) derivation:

```
Before fix: Failed: 14, Passed: 4, Total: 18
  …discount = contract discount: expected 1200, actual 0
  …discount = the pay-for-10 discount: expected 2000, actual 0
After fix:  Failed:  0, Passed: 18, Total: 18
```

The 4 tests that pass without the fix are the no-discount / traceability / duplicate-cycle
cases (`Scenario01`, `11`, `15`, `16`) — precisely the cases where
`GrossAmount == ContractedAmount`.

**Impact on other paths:** `RenewSubscriptionOfferCommand` and
`ChangeSubscriptionPlanCommand` build invoices from `calc.BaseAmount/DiscountAmount/
FinalAmount`, *not* from `SnapshotMonthlyCharge`, so their invoice amounts were already
correct; only the stored snapshot value (and therefore the BillingCycle path) was wrong.
No existing test asserted `snapshot.MonthlyCharge` for a discounted contract, so the
correction is regression-free (verified — §10).

---

## 4. Invoice Arithmetic Verification (Task 21.2 §3 / §4)

All figures produced by the new suite against real Offer→Contract→Subscription fixtures.

| # | Cycle fixture | `SnapshotPrice` | `SnapshotMonthlyCharge` | cycle months | Subtotal | Discount | **Total** | `Contract.ContractedAmount` |
|---|---|---|---|---|---|---|---|---|
| 1 | no discount, full term | 1 000 | 1 000 | 12 | 12 000 | 0 | **12 000** | 12 000 |
| 2 | 10 % percentage | 1 000 | 900 | 12 | 12 000 | 1 200 | **10 800** | 10 800 |
| 3 | fixed −1 500 | 1 000 | 875 | 12 | 12 000 | 1 500 | **10 500** | 10 500 |
| 4 | Pay 10 of 12 | 1 000 | 833.3333… | 12 | 12 000 | 2 000 | **10 000** | 10 000 |
| 5 | promotional price 9 000 | 1 000 | 750 | 12 | 12 000 | 3 000 | **9 000** | 9 000 |
| 6 | single-month partial (10 %) | 1 000 | 900 | 1 | 1 000 | 100 | **900** | — |
| 7 | three-month partial (10 %) | 1 000 | 900 | 3 | 3 000 | 300 | **2 700** | — |

Identities asserted for every fixture:

* `TotalAmount = Subtotal − DiscountAmount + TaxAmount` (tolerance 0.01 — `Invoice.Create` INV-01)
* `TotalAmount = Subscription.SnapshotMonthlyCharge × cycle months`
* `Subtotal = Subscription.SnapshotPrice × cycle months`
* `DiscountAmount = Contract.DiscountAmount` (only when cycle covers the whole term)
* `TotalAmount = Contract.ContractedAmount` — **no double discount, no missing discount**

Decimal note (Task 21.2 §12): `10 000 / 12 = 833.3333…` is exact in .NET `decimal`;
`charge × 12 == 10 000` holds exactly under the InMemory provider, and `Invoice.Create`
tolerates the 4×10⁻²⁵ residual. On SQL Server the column precision
`TenantPlan.SnapshotMonthlyCharge = (10,2)` (`TenantPlanConfiguration`) rounds the charge
to 833.33, so a *whole-term* Pay-for-X invoice would total 9 999.96 instead of 10 000
(4-cent drift). This is a pre-existing precision decision, not changed here (§11.3).

---

## 5. Additional findings

### 5.1 F-2 (fixed) — invoice-number collision on the cycle path

`InvoiceConfiguration.cs:25-27` declares a **global** unique index
`UX_Invoices_InvoiceNumber`. The BillingCycle handler generated `INV-yyyyMMdd-HHmmss`
(second resolution, no entropy) with **no** duplicate check, so two cycles invoiced in the
same second violated the index with an unhandled `DbUpdateException`.

*Fix* (`CreateInvoiceFromBillingCycleCommand.cs:55`): append a 6-hex suffix — the exact
convention already used by `RenewSubscriptionOfferCommand` /
`ChangeSubscriptionPlanCommand`.
*Test:* `Scenario16_SameTimestampTwoCycles_ProduceDistinctInvoiceNumbers`.

### 5.2 F-3 (fixed) — orphan invoice on rejected cycle

The handler called `dbContext.Invoices.Add(...)` **before** `billingCycle.MarkInvoiced()`.
`MarkInvoiced()` only accepts `Draft`, so an already-invoiced cycle returned an error
*after* an `Invoice` was already in the `Added` state — the next `SaveChangesAsync` on the
same unit of work would have persisted a ghost invoice (duplicate per cycle).

*Fix* (`…Command.cs:73-82`): transition the cycle first, then track the invoice.
Guarantees at most one invoice per `BillingCycle`.
*Test:* `Scenario15_SecondInvoiceForSameCycle_Rejected_NoDuplicateRow`.

### 5.3 F-4 (observed, deliberately **not** changed) — bonus months vs. cycle duration

`RenewSubscriptionOfferCommand.cs:382` / `ChangeSubscriptionPlanCommand.cs:376` create the
cycle with `periodEnd: subscription.EffectiveEndsAtUtc` (= duration **+ bonus**), while
`CreateInvoiceFromBillingCycleCommand.cs:42-46` derives `cycleDurationMonths` from the
calendar difference between `PeriodStart` and `PeriodEnd`. For `BonusMonths > 0` the cycle
handler would therefore invoice `SnapshotMonthlyCharge × (duration + bonus)`, which is
greater than `Contract.ContractedAmount`.

Evidence for **not** patching it in this task:

1. Task 21.2 §3/§4 define invoice amounts in terms of the **billing-cycle duration**.
2. Task 21.2 §5: *"Do not change duration semantics without evidence."*
3. **Zero production exposure**: every `BillingCycle` the system creates is invoiced
   *inline* at creation (`Draft → Invoiced` in the same `SaveChanges`), and
   `CreateInvoiceFromBillingCycleCommand` has **no dispatcher anywhere in `src`** —
   no controller, no hosted service (grep-verified: `rg CreateInvoiceFromBillingCycle src`
   returns only its own definition; `rg BackgroundService|IHostedService src` returns
   nothing). A `Draft` cycle is therefore never produced by production code today.
4. No existing test asserts the period of a handler-created cycle (the tests asserting
   `bc.PeriodEnd == sub.EffectiveEndsAtUtc` construct cycles manually).

**Decision:** documented as a boundary that must be resolved (billing period vs. paid
term) *before* the cycle handler is wired to a job or endpoint. Reported, not silently
repaired.

### 5.4 F-5 (observed, not changed) — cross-tenant number collision on `POST /api/invoices`

`CreateInvoiceCommand.cs:66-75` auto-generates `INV-yyyyMMdd-HHmmss` and checks duplicates
**per tenant** (`INV-03`), but `UX_Invoices_InvoiceNumber` is global — two tenants creating
an invoice in the same second would collide at the database. This path belongs to Task
21.1 (its duplicate check and error code are already covered by
`TASK21_InvoiceTrustBoundaryTests`), so it is reported only.

### 5.5 F-6 (documented nuance) — tiered plans shift tier discount into the Discount column

For a plan with a pricing tier, `Contract.GrossAmount` = tier price while
`Subscription.SnapshotPrice` = list price. Task 21.2 §3 mandates
`Subtotal = SnapshotPrice × duration`, so on the cycle path the tier reduction is reported
inside `DiscountAmount` (list−contracted) instead of inside `Subtotal`. `TotalAmount`
remains exactly `Contract.ContractedAmount`, and the contract-derived path
(`CreateInvoiceCommand`) still reports `Subtotal = Contract.GrossAmount`. Spec-conformant
as written; recorded for downstream reporting/BI consumers.

---

## 6. Every current invoice-creation path (Task 21.2 §8)

`rg "Invoices.Add" src` → exactly four sites.

| # | Path | Amount source | Entry point → authorization | Cycle handling | Links written |
|---|---|---|---|---|---|
| 1 | `CreateInvoiceCommand` (`CreateInvoiceCommand.cs:80-111`) | **Contract**: `GrossAmount` / `DiscountAmount` / `ContractedAmount`; client-supplied values must equal server-derived or are rejected (`Invoice.ClientAmountMismatch`) | `POST /api/invoices` → `Permissions.Invoices.Create` | none (`BillingCycleId = null`) | `ContractId` (tenant-checked), optional `SubscriptionId` (must belong to contract + tenant) |
| 2 | `RenewSubscriptionOfferCommand.cs:394-428` | **Offer engine**: `calc.BaseAmount` / `calc.DiscountAmount` / `calc.FinalAmount` | `POST /api/tenantplans/{id}/renew-commercial` → `Permissions.Subscriptions.Manage` | cycle created **and** marked `Invoiced` inline, one `SaveChanges` | `ContractId` + `SubscriptionId` + `BillingCycleId` |
| 3 | `ChangeSubscriptionPlanCommand.cs:390-417` | **Offer engine** (same as #2) | `POST /api/tenantplans/{id}/change-plan` → `Permissions.Subscriptions.Manage` | cycle created **and** marked `Invoiced` inline | `ContractId` + `SubscriptionId` + `BillingCycleId` |
| 4 | `CreateInvoiceFromBillingCycleCommand.cs:50-87` | **Subscription snapshot**: `SnapshotPrice` / `SnapshotMonthlyCharge` × cycle duration (§3/§4 formulas) | **no dispatcher in `src`** (no controller, no hosted service) — reachable only from tests today | `Draft → Invoiced`, exactly one invoice (§5.2) | `ContractId` (via `Subscription.ContractId`, nullable) + `SubscriptionId` + `BillingCycleId` |

Cross-cutting assertions for all four:

* No path accepts an unvalidated client amount (path 1 validates, paths 2–4 take no amount
  parameters at all).
* No path re-reads `Promotion` at invoice time — promotions are evaluated **once** at
  offer/contract creation (paths 2/3) or pre-frozen into the snapshot (paths 1/4).
* `Invoice.TotalAmount == Contract.ContractedAmount` holds on paths 1–4 (asserted for 1 by
  Task 21.1.1 tests, for 2/3 by `Scenario13`/`Scenario14`, for 4 by `Scenario01-05`).
* Refunds never read `Invoice.PeriodStart`/`PeriodEnd` (grep-verified), so period
  semantics changes are refund-safe.

---

## 7. Invoice lifecycle & immutability (Task 21.2 §7)

| Operation | Guard | Effect on amounts |
|---|---|---|
| `Invoice.Create` | INV-01 arithmetic identity (`Total = Subtotal − Discount + Tax`, tol. 0.01); rejects negative values | sets all four amounts once |
| `IssueInvoiceCommand` | `Invoice.Issue` → **Draft only** | none (status/`IssuedAt`/`DueAt`) |
| `CancelInvoiceCommand` (+ `DELETE /api/invoices/{id}`) | `Invoice.Cancel` → **Draft only** | none (status only) |
| `MarkInvoicePaidCommand` / `AllocatePaymentCommand` / `CreateTenantCreditCommand` | `Invoice.UpdatePaymentStatus` → requires `Issued/Sent/PartiallyPaid`, settles from allocations + credit applications | none (status only) |
| `AddInvoiceLineCommand` / `RemoveInvoiceLineCommand` | **Draft only**; command never references `Subtotal`/`DiscountAmount`/`TaxAmount`/`TotalAmount` (grep-verified) | none |
| Entity | every amount property is `{ get; private set; }` (`Invoice.cs:15-18`); no public setter anywhere | — |

`DELETE /api/invoices/{id}` dispatches `CancelInvoiceCommand` — there is **no hard delete**
of an invoice anywhere in the system.

Traceability verified (`Scenario11`): `ContractId`, `SubscriptionId`, `BillingCycleId`,
invoice period == cycle period, cycle state `Invoiced`, `InvoiceNumber` prefixed `INV-`.

---

## 8. Authorization surface (Task 21.2 §10)

| Endpoint | Command | Required permission |
|---|---|---|
| `POST /api/invoices` | `CreateInvoiceCommand` | `Permissions.Invoices.Create` |
| `POST /api/invoices/{id}/issue`, `/lines`, `/pay`, `/cancel`, `DELETE …/lines/{lineId}` | issue/line/payment/cancel commands | `Permissions.Invoices.Update` |
| `DELETE /api/invoices/{id}` | `CancelInvoiceCommand` (soft) | `Permissions.Invoices.Delete` |
| `GET /api/invoices`, `GET /api/invoices/{id}`, `GET …/lines` | queries | `Permissions.Invoices.Read` |
| `POST /api/tenantplans/{id}/renew-commercial` | `RenewSubscriptionOfferCommand` | `Permissions.Subscriptions.Manage` |
| `POST /api/tenantplans/{id}/change-plan` | `ChangeSubscriptionPlanCommand` | `Permissions.Subscriptions.Manage` |

Renewal / plan-change handlers additionally run `IPlatformAdminGuard.EnsurePlatformAdmin()`.
No authority was widened by this task.

---

## 9. Test scenarios (Task 21.2 §11)

New file: `tests/Centerix.SecurityTests/TASK21_2_BillingCycleInvoiceLifecycleTests.cs`
(18 tests: the 14 required scenarios + 4 hardening extras).

| Required scenario | Test |
|---|---|
| 1. No-discount invoice | `Scenario01_NoDiscount_FullTermCycle_InvoiceEqualsContractedAmount` |
| 2. Percentage discount, discount once | `Scenario02_PercentageDiscount_DiscountReachesInvoiceExactlyOnce` |
| 3. Fixed-amount discount | `Scenario03_FixedAmountDiscount_UsesDiscountedMonthlyCharge` |
| 4. Pay-for-X (12 entitlement / 10 charged) | `Scenario04_PayForXMonths_12Entitlement10Charged_InvoiceChargesTenMonths` |
| 5. Promotional price | `Scenario05_PromotionalPrice_UsesPromotionalMonthlyCharge` |
| 6. One-month cycle | `Scenario06_SingleMonthCycle_PartialPeriodUsesSnapshotCharge` |
| 7. Multi-month cycle | `Scenario07_ThreeMonthCycle_ScalesSnapshotCharge` |
| 8. Plan price mutation after subscription | `Scenario08_PlanPriceMutation_AfterSubscriptionCreation_InvoiceUnaffected` |
| 9. Promotion mutation after contract | `Scenario09_PromotionMutation_AfterContractCreation_InvoiceUnaffected` |
| 10. Invoice arithmetic | `Scenario10_InvoiceArithmeticIdentity_HoldsForDiscountedFixture` |
| 11. Traceability | `Scenario11_Traceability_ContractSubscriptionBillingCycleLinked` |
| 12. No double discount | `Scenario12_NoDoubleDiscount_TotalIsNeitherDoubleNorZeroDiscounted` |
| 13. Upgrade/downgrade invoice | `Scenario13_ChangePlan_InvoiceDerivedFromNewContractSnapshot` |
| 14. Renewal invoice | `Scenario14_Renewal_InvoiceDerivedFromNewContractSnapshot` |
| extra | `Scenario15_…NoDuplicateRow`, `Scenario16_…DistinctInvoiceNumbers`, `Scenario17_IssuedInvoice_AmountsAreImmutable`, `Scenario18_SnapshotMonthlyCharge_IsAlwaysThePostDiscountMonthlyCharge` |

Scenarios 13/14 run the **real** `ChangeSubscriptionPlanHandler` /
`RenewSubscriptionOfferHandler` end-to-end (InMemory provider, `SERIALIZABLE` transaction
branch skipped because `IsRelational == false`) and assert the produced invoice against
the new contract snapshot.

---

## 10. Verification (Task 21.2 §12)

### 10.1 Build

```
dotnet build Centerix.slnx
0 Error(s)
```

No compiler (CS) warnings are introduced by this task. The new test file carries 23
StyleCop formatting/ordering warnings (SA1117/SA1116/SA1201/SA1202) — the same analyzer
noise class that already exists across the suite (~11 400 warnings at HEAD, e.g.
`Task18CommercialIntegrityTests` alone has 142 SA1117). No assertion, logic or production
code is affected by them.

### 10.2 EF Core model

```
dotnet ef migrations has-pending-model-changes --project src/Centerix.Infrastructure \
  --startup-project src/Centerix.API --context AppDbContext
No changes have been made to the model since the last migration.
```

No migration required: the task corrects a derivation only — no schema change.

### 10.3 SQL Server / Testcontainers subset

```
dotnet test --filter "Category=SqlServer"
Passed! - Failed: 0, Passed: 176, Skipped: 1, Total: 177, Duration: 11 m 58 s
```

(The 18 new tests are InMemory tests and carry no `Category=SqlServer` trait.)

### 10.4 Full regression

```
dotnet test tests/Centerix.SecurityTests/Centerix.SecurityTests.csproj
Passed! - Failed: 0, Passed: 1553, Skipped: 1, Total: 1554, Duration: 12 m 38 s
```

Run on the final state of the working tree (all three source changes + the new suite).

Baseline recorded by `TASK-21.1.2` / `TASK-21.1.1` was `1535 passed / 1536 total`;
`1536 + 18 new = 1554` ✅ — no pre-existing test was weakened, changed or removed.

### 10.5 New suite

```
--filter "FullyQualifiedName~TASK21_2_BillingCycleInvoiceLifecycleTests"
Passed! - Failed: 0, Passed: 18, Skipped: 0, Total: 18
(reverted derivation: Failed 14 / Passed 4 — see §3.5)
```

---

## 11. Known issues, boundaries & non-goals

### 11.1 Pre-existing `TenantPlan.SnapshotMonthlyCharge` rows (not repaired here)

`docs/TASK-21.1.2-…` §10.1 records that rows created *before* that task's migration have
`SnapshotMonthlyCharge = 0` (direct-assignment subscriptions), and rows created through
the contract flow before **this** task carry the gross-based (list) charge for discounted
contracts. Both are only observable through path #4, which is currently unreachable in
production (§5.3), so no invoice can be produced from stale rows today. A deterministic
data-repair/backfill (contract-linked rows: `ContractedAmount / DurationMonths`; direct
rows: `Plan.MonthlyPrice`) remains an explicitly separate task — data repair is out of
scope for 21.2, exactly as it was for 21.1.2.

### 11.2 Boundary F-4 (bonus months vs. cycle duration)

Reported with evidence and a decision in §5.3. Must be resolved before the cycle handler
is exposed to any job or endpoint.

### 11.3 Precision boundary

`SnapshotMonthlyCharge` is stored as `decimal(10,2)`; whole-term invoices of a contract
whose `ContractedAmount` is not divisible by `DurationMonths` (e.g. Pay-for-X 10 000/12)
round to the cent and can differ from `Contract.ContractedAmount` by ≤ 0.04 on SQL Server.
Pre-existing storage precision; unchanged (changing it would be a schema change and is
outside this task's boundary).

### 11.4 Explicitly untouched

Refund calculation, credit lineage, payment allocation, subscription/contract lifecycle,
promotion engine, tenant authority, Task 22 deliverables.

---

## 12. Deliverables

| Deliverable | Status |
|---|---|
| Code correction where a defect is proven | ✅ `Contract.GetSubscriptionSnapshot()` (F-1) + cycle handler (F-2, F-3) |
| 14 required test scenarios (+4 hardening) | ✅ `TASK21_2_BillingCycleInvoiceLifecycleTests.cs`, 18/18 |
| EF verification | ✅ no pending model changes |
| SQL Server / Testcontainers verification | ✅ 176/177 (0 failed) |
| Full regression with exact counts | ✅ 1553 passed / 1554 total / 0 failed / 1 skipped |
| This document | ✅ `docs/TASK-21.2-BILLING-CYCLE-INVOICE-LIFECYCLE-INTEGRITY.md` |

---

## 13. Closure criteria

| Criterion (Task 21.2) | Status |
|---|---|
| Snapshot is authoritative and plan/promotion mutations cannot move an invoice | ✅ `Scenario08`, `Scenario09`, §2 |
| Discount appears exactly once on the invoice | ✅ `Scenario02`, `Scenario12` |
| `Invoice.TotalAmount == Contract.ContractedAmount` on every active path | ✅ `Scenario01-05`, `Scenario13`, `Scenario14`, §6 |
| No promotion re-evaluation at invoice time | ✅ §6 (paths 1–4), `Scenario09` |
| Arithmetic correct for 1-month / multi-month / no-discount / discounted / promotional cycles | ✅ §4 table |
| Commercial traceability Contract → Subscription → BillingCycle → Invoice | ✅ `Scenario11` |
| All current invoice-creation paths traced | ✅ §6 (4 paths, entry points + permissions) |
| Issued invoices cannot have their amounts changed | ✅ §7, `Scenario17` |
| Exactly one invoice per BillingCycle | ✅ §5.2, `Scenario15` |
| Build clean (0 errors) | ✅ §10.1 |
| EF: no pending model changes | ✅ §10.2 |
| SQL Server tests execute and pass | ✅ §10.3 |
| Full regression, no weakened assertions | ✅ §10.4 (1553/1554, baseline +18) |
| Findings outside the safe fix boundary reported with evidence | ✅ §5.3, §5.4, §5.5, §11 |

---

## TASK 21.2 STATUS

**COMPLETE** — 3 source files touched (2 production, 1 test), 0 failures in 1554 tests,
no schema change, 3 defects fixed with red→green evidence, 3 boundary findings reported
without silent repair.
