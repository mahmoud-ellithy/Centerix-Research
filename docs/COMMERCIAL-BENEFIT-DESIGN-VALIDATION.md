# CENTERIX — COMMERCIAL BENEFIT DESIGN VALIDATION & FINAL BASELINE

**Type:** Final design baseline for Task A onward. Supersedes the initial validation (`996237d`).
**Scope:** Validate the `CommercialBenefit` / `PaymentTerms` / Benefit-Lifecycle model against the actual repository state, apply the four corrections from the architecture review, and lock the final approved design.
**Posture:** Documentation only. **No code, migrations, entities, controllers, or workflows were modified.**
**Repository state at audit time:** `mahmoud-ellithy/Centerix-Research`, HEAD `6c6ed34` (audit commit `c92e170` already merged; initial validation `996237d` superseded by this document).

---

## A. Executive Summary

The previous Deep Financial Domain Audit (`DEEP-FINANCIAL-DOMAIN-AUDIT-REPORT.md`, `c92e170`) and the initial validation (`996237d`) reached four core conclusions that this final baseline **confirms and refines**:

1. **`Installment` is the authoritative payment obligation / entitlement period model.** No new `PaymentObligation` aggregate is required.
2. **`PaymentTerms` does not exist anywhere** in the codebase; full-tree grep confirms zero matches.
3. **`BenefitEligibilityService` is a single global rule** used for every `ContractBenefit`. It conflates *eligibility* with *delivery*.
4. **`OfferBenefit` rows are never populated in production today** — the promotion pipeline carries price only.

This document applies **four corrections** to the initial validation, then locks the final baseline:

* **C1.** `PaymentTerms` is the **commercial contractual decision**; `Installment` is the **financial execution consequence**. The invariant is directional: `PaymentTerms = Installments → installment schedule is required` and `PaymentTerms = FullUpfront → no installment schedule`. The previous bidirectional "≡" invariant is removed.
* **C2.** `Contract.BonusMonths` is **not** `Σ FreeMonthsBenefits.GrantedMonths`. The commercial entitlement row carries `EntitlementMonths`; the grant event carries `GrantedMonths`; the subscription carries an `AppliedMonths` counter that is the authoritative extension of `EffectiveEndsAtUtc`. The three are independent and must not be coupled by a sum invariant.
* **C3.** Bonus Months and Physical Gifts are unified under the conceptual model `Commercial Benefit → {Pricing Benefit, Entitlement Benefit → {FreeMonths, PhysicalGift}}`. They share eligibility semantics but remain **separate persistence aggregates** because their fulfillment lifecycles genuinely diverge.
* **C4.** Lifecycle is split into two orthogonal concerns: **Eligibility** (`NotEligible / Eligible`, reversible) and **Fulfillment** (`Pending / Granted / Delivered|Applied`, irreversible). No `Earned`, `Consumed`, or `Withdrawn` states.

---

## B. Current Domain Reality (evidence summary)

### B.1 `ContractBenefit` — existing single benefit model

[ContractBenefit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/ContractBenefit.cs#L29-L222) is a row entity carrying `BenefitType` (PhysicalGift / Service / FinancialCredit / ExtendedTerm / Other), `ContractualValue`, `CurrencyCode`, `EligibilityStatus`, `EligibleAtUtc`, `IsGranted`, `GrantedAtUtc`, `DeliveredBy`. Only `PhysicalGift` may be delivered ([MarkBenefitDeliveredCommand.cs#L119-L120](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Contracts/Commands/MarkBenefitDeliveredCommand.cs#L119-L120)). `Contract.AddBenefit` enforces the 3-month-value cap ([Contract.cs#L413-L429](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L413-L429)).

### B.2 Bonus Months — scalar today, not a row

`Contract.BonusMonths` ([Contract.cs#L101-L105](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L101-L105)) and `TenantPlan.BonusMonths` ([TenantPlan.cs#L70](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs#L70)) are scalars set at creation from `Offer.BonusMonths` ← `Plan.BonusMonths` ([CreateContractFromOfferCommand.cs#L92-L119](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Promotions/Commands/CreateContractFromOfferCommand.cs#L92-L119), [SubscriptionFactory.cs#L61-L110](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Subscriptions/SubscriptionFactory.cs#L61-L110)). There is no benefit row representing the bonus; there is no eligibility check before the bonus is granted; there is no way to withhold it because the customer later pays by installments.

### B.3 Eligibility — single global rule

[BenefitEligibilityService.cs#L28-L61](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L28-L61):

```text
if (contract.Status != Active)            return false;
if (contractedAmount > 0
 && completedPaymentTotal < contractedAmount) return false;
if (hasOverdueInstallment)                return false;
return true;
```

Invoked from a single handler [CheckBenefitEligibilityCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Contracts/Commands/CheckBenefitEligibilityCommand.cs#L67-L78) for every benefit, with one global `completedPaymentTotal` and one `hasOverdueInstallment` flag.

### B.4 Installments — already authoritatively modeled

Every prompt §13 requirement is present in [Installment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L21-L345) and [CreateInstallmentScheduleCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L25-L270). The only gap is the missing `PaymentTerms` source-of-truth on Contract.

### B.5 Offers — promotion service carries price only

`PromotionCalculationService` ([PromotionCalculationService.cs#L17-L146](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs#L17-L146)) returns only price/discount fields. `CalculateAndPersistOfferCommand` ([CalculateAndPersistOfferCommand.cs#L75-L132](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Promotions/Commands/CalculateAndPersistOfferCommand.cs#L75-L132)) snapshots PricingTiers and Features but never `OfferBenefit`. All benefit tests construct rows directly via `Contract.AddBenefit(...)`.

### B.6 Promotion type taxonomy

`PromotionType`: `PercentageDiscount / FixedAmountDiscount / PayForXMonths / PromotionalPrice`. Today:

* `PayForXMonths` collapses to `ContractedAmount` + `Contract.ChargedMonths` — customer pays less over the same period.
* `PromotionalPrice` lowers total without extending term — Example C "12 for the price of 10".
* `Plan.BonusMonths` is the only source of true "free months" — Example A "6 for the price of 5".

### B.7 Greenfield on benefits

No seed data file, no production fixtures. Migrations end at `20260926221850_Task21_InvoiceMoneyPrecisionAlignment`. No historical `ContractBenefit` / `OfferBenefit` rows exist.

---

## C. Corrected Commercial Model

```text
┌────────────────────────────────┐
│ Plan        (catalog)          │
│   - BonusMonths (catalog hint) │
│   - PricingTiers               │
│   - PlanFeatures               │
└──────────────┬─────────────────┘
               │  (read at calculation time only)
               ▼
┌────────────────────────────────┐
│ Offer        (immutable snapshot) │
│   - DurationMonths             │
│   - PaymentTerms ◄── NEW       │
│   - BonusMonths (catalog hint) │
│   - BaseAmount / FinalAmount   │
│   - PromotionType / ChargedMonths │
│   - OfferBenefits[]            │
│     └ BenefitType=PhysicalGift │
│       + EligibilityRule ◄── NEW│
└──────────────┬─────────────────┘
               │  (only path to Contract)
               ▼
┌────────────────────────────────┐
│ Contract        (immutable snapshot) │
│   - DurationMonths             │
│   - PaymentTerms ◄── NEW       │
│   - BonusMonths (scalar; deprecated after FreeMonthsBenefit rollout)│
│   - ContractedAmount           │
│   - Benefits[]                 │
│     └ ContractBenefit {        │
│         BenefitType,           │
│         ContractualValue,      │
│         EligibilityRule ◄── NEW}│
│   - FreeMonthsBenefits[] ◄── NEW navigation│
│     └ EntitlementMonths (commercial entitlement)│
│       + EligibilityRule ◄── NEW│
└──────────────┬─────────────────┘
               │  (Contract.GetSubscriptionSnapshot — schedule creation is a SEPARATE step)
               ▼
┌────────────────────────────────┐
│ TenantPlan / Subscription      │
│   - DurationMonths             │
│   - BonusMonths (applied counter; authoritative for EffectiveEndsAtUtc)│
│   - StartsAtUtc, BaseEndsAtUtc │
│   - EffectiveEndsAtUtc = Base + BonusMonths (calendar math)│
│   - AppliedFreeMonthsBenefits[] ◄── NEW: which FreeMonthsBenefit rows have already been applied│
└──────────────┬─────────────────┘
               │  (Installment schedule is a separate lifecycle step driven by Contract.PaymentTerms == Installments)
               ▼
┌────────────────────────────────┐
│ Installment / Invoice / Payment / Refund / TenantCredit │
└────────────────────────────────┘
```

**Direction-of-truth correction:** `PaymentTerms` is the commercial decision on `Offer` and is snapshotted onto `Contract`. Installments are a *consequence* of `PaymentTerms == Installments` and are created by an explicit schedule-creation step at the appropriate lifecycle point (e.g. upon contract activation or first invoice issuance — to be decided in Task A based on activation flow).

---

## D. Benefit Types — unified conceptual model, two persistence types

### D.1 Conceptual taxonomy

```text
Commercial Benefit
│
├── Pricing Benefit                 (NOT a row; price-side adjustment on Offer/Contract)
│   ├── PayForXMonths
│   └── PromotionalPrice
│
└── Entitlement Benefit             (a row on Contract)
    ├── FreeMonths
    └── PhysicalGift
```

### D.2 Persistence split

| Conceptual kind | Persistence type | Why separate |
|---|---|---|
| **Pricing Benefit** | No row. Lives as `Offer.{PromotionType, FinalAmount, DiscountAmount, ChargedMonths}` snapshotted to `Contract.{PromotionType, ContractedAmount, ChargedMonths}`. | Pure price math; no lifecycle, no eligibility, no fulfillment. |
| **FreeMonths (Entitlement Benefit)** | New `FreeMonthsBenefit` row on `Contract` with its own navigation `Contract.FreeMonthsBenefits`. | Time-credit whose fulfillment is "applied to subscription", which mutates `TenantPlan`. |
| **PhysicalGift (Entitlement Benefit)** | Existing `ContractBenefit` row on `Contract` with `BenefitType = PhysicalGift`. | Deliverable object whose fulfillment is `Delivered`. |

A single `ContractBenefit` aggregate is **not** polymorphic enough to carry FreeMonths correctly: FreeMonths must call `TenantPlan.ApplyBonusMonths(...)` on fulfillment, while PhysicalGift must record `DeliveredBy` and stop. We therefore keep `FreeMonthsBenefit` and `ContractBenefit` as separate aggregates that share the `EligibilityRule` evaluation pipeline.

### D.3 Shared semantics across Entitlement Benefits

Every Entitlement Benefit row — whether `FreeMonthsBenefit` or `ContractBenefit` — satisfies all five:

1. Granted by an Offer/Contract (Offer carries a snapshot, Contract snapshots it from the Offer).
2. Commercial definition is **immutable** after Contract creation.
3. Eligibility conditions are **explicit** and per-row.
4. Historical state is **auditable** via the fulfillment lifecycle.
5. Fulfillment is **idempotent**.

---

## E. Payment Terms — directional, not bidirectional

### E.1 Final placement

* `enum PaymentTerms { FullUpfront = 0, Installments = 1 }`.
* `Offer.PaymentTerms` — set at calculation time.
* `Contract.PaymentTerms` — snapshotted from Offer at Contract creation. Immutable thereafter.

Not on `TenantPlan`, `Installment`, `Payment`, `BillingCycle`, `Invoice`.

### E.2 Directional invariant (replaces the previous bidirectional one)

```text
Contract.PaymentTerms == Installments
    ⇒
    an installment schedule MUST be created before the contract is fully operationally live
    (i.e. before the first invoice for that contract can be issued)

Contract.PaymentTerms == FullUpfront
    ⇒
    NO installment schedule is created for this contract
    (any attempt to create one is rejected)
```

The reverse direction is **not** a domain invariant: an existing installment schedule does NOT mean `PaymentTerms == Installments` — `PaymentTerms` is set at Offer time and snapshotted onto Contract, never inferred from installment rows.

This is a one-way consequence. The PaymentTerms value is the **commercial decision**; the schedule is the **financial execution**. Inferring one from the other would let post-contract operational events rewrite the commercial agreement.

### E.3 Default assignment at Offer calculation time

* If `PromotionType ∈ {PayForXMonths, PromotionalPrice}` ⇒ `FullUpfront`.
* If `Plan.BonusMonths > 0` AND the promotion grants bonus months ⇒ `FullUpfront` (upfront is required for bonus eligibility).
* Else ⇒ `Installments`.

This default is overridden by an explicit `PaymentTerms` parameter on `CalculateAndPersistOfferCommand` when the platform operator wants to express non-default terms.

### E.4 Migration of existing data

* The repository is greenfield on benefits and on the new schema. No historical `Contract` row carries `PaymentTerms` yet.
* The first migration that introduces `Contract.PaymentTerms` (T1 below) adds the column as **NOT NULL with a non-arbitrary default** (`Installments`) only if production data exists; for greenfield, the column is **NOT NULL** with the default `Installments` chosen by the safe-side rule "unknown → safer to allow installment behavior".
* For each existing `Contract` row whose `BonusMonths > 0`, the migration also backfills a `FreeMonthsBenefit` row (see §F.4 and §K-T3) so the audit trail is not lost.
* Tests and dev fixtures are updated to set `PaymentTerms` explicitly where commercial meaning is intended.

---

## F. FreeMonths Benefit — final model

### F.1 The four distinctions

The four counters / states that were conflated by the previous report are now **independent**:

| Concept | Storage | Meaning | Mutability |
|---|---|---|---|
| **Commercial Entitlement** | `FreeMonthsBenefit.EntitlementMonths` (row on Contract) | "The contract grants N free months as a commercial benefit." Configured at Offer/Contract creation. | Immutable after Contract creation. |
| **Eligibility** | `FreeMonthsBenefit.EligibilityStatus ∈ {NotEligible, Eligible}` | "Conditions currently allow grant." Re-evaluated from `EligibilityRule`. | Reversible. |
| **Grant** | `FreeMonthsBenefit.FulfillmentStatus = Granted` + `FreeMonthsBenefit.GrantedAtUtc` | "The grant decision has been recorded." Set once when eligibility becomes true. | Irreversible. |
| **Application** | `TenantPlan.AppliedFreeMonthsBenefitIds[]` + `TenantPlan.BonusMonths` counter | "The grant has been added to the subscription's entitlement period." The authoritative extension of `EffectiveEndsAtUtc`. | Append-only; idempotent by `FreeMonthsBenefitId`. |

### F.2 Critical invariants (corrected)

* `Contract.BonusMonths` is **NOT** `Σ FreeMonthsBenefits.GrantedMonths`. The scalar represents *commercial entitlement* (sum of `EntitlementMonths` across rows), not the count of grants.
* A `FreeMonthsBenefit` row is **not** required to have `FulfillmentStatus = Granted` for the customer to be entitled — the contract can carry `EntitlementMonths = 1` while eligibility remains `NotEligible` (e.g. customer has not yet paid).
* A grant, once recorded, **must not** double-apply to the subscription. The application step checks `TenantPlan.AppliedFreeMonthsBenefitIds` for the `FreeMonthsBenefit.Id` before incrementing `BonusMonths`. Idempotent.
* Bonus months never increase the **billable contractual amount** — `ContractedAmount` is unchanged by `EntitlementMonths`. The price was settled at Offer calculation time.
* Bonus months **may** increase service entitlement — `TenantPlan.EffectiveEndsAtUtc` extends when the bonus is applied.
* Eligibility checking must not itself cause duplicate subscription extension — checking eligibility transitions `NotEligible → Eligible` only; the extension happens only on the explicit grant-and-apply step, which is idempotent.
* The historical commercial entitlement remains auditable — the `FreeMonthsBenefit` row is immutable; the `GrantedAtUtc` / `AppliedAtUtc` timestamps are immutable.

### F.3 Lifecycle

```text
FreeMonthsBenefit.EligibilityStatus     FreeMonthsBenefit.FulfillmentStatus
─────────────────────────────           ───────────────────────────────────
                                       Pending
NotEligible  ──────► Eligible   ─────► Granted ─────► AppliedToSubscription (terminal)
     ▲                    │                │
     └────────────────────┘                │
                                          ▼
                                    (terminal)
```

* `Eligible → NotEligible` is allowed (re-evaluation of rule fails).
* `NotEligible → AppliedToSubscription` is rejected.
* `Pending → Granted` is the only grant transition; it is set when the eligibility evaluator first returns true and the benefit is processed by the grant handler.
* `Granted → AppliedToSubscription` is the only application transition; it mutates `TenantPlan.BonusMonths` and `EffectiveEndsAtUtc` exactly once.
* `AppliedToSubscription` is terminal.

### F.4 Migration of existing `BonusMonths` data

* Each existing `Contract` row with `BonusMonths > 0` gets **one** backfilled `FreeMonthsBenefit` row with `EntitlementMonths = Contract.BonusMonths`, `EligibilityRule = DefaultFreeMonthsRule (PaymentTermsEq(FullUpfront))`, `EligibilityStatus = NotEligible`, `FulfillmentStatus = Pending`.
* `Contract.BonusMonths` is preserved unchanged (zero risk to existing tests / downstream consumers). It becomes a denormalized projection of `Σ FreeMonthsBenefits.EntitlementMonths` only **after** T3 ships and the next major version migration; before that, the two are independent fields and `Contract.ValidateSnapshotCompleteness` does NOT enforce a sum invariant.
* When T3 ships, the canonical read path becomes: `BonusMonths` is read from `Σ FreeMonthsBenefits.EntitlementMonths`. The scalar column is retained on Contract as a historical-only field but is no longer written to after the migration.

---

## G. Eligibility + Fulfillment Lifecycle

### G.1 Eligibility (reversible)

```text
EligibilityStatus : { NotEligible, Eligible }
```

* Re-derivable from `EligibilityRule`.
* Can move `NotEligible ⇄ Eligible` freely as financial state changes.
* Does **not** imply any grant has occurred.

### G.2 Fulfillment (irreversible)

```text
FulfillmentStatus : { Pending, Granted, Delivered | AppliedToSubscription }
```

* `Pending` — initial state on row creation.
* `Granted` — recorded exactly once, when the eligibility evaluator first returns true AND the grant step is executed.
* `Delivered` (for `ContractBenefit` with `BenefitType = PhysicalGift`) — recorded exactly once when physical handover is acknowledged.
* `AppliedToSubscription` (for `FreeMonthsBenefit`) — recorded exactly once when the bonus is added to `TenantPlan.EffectiveEndsAtUtc`.

State transitions are **monotone**: `Pending → Granted → (Delivered | AppliedToSubscription)`. There is no `Earned`, no `Withdrawn`, no `Consumed` — those terms add noise without a corresponding business action.

### G.3 Per benefit-type transitions

#### PhysicalGift (`ContractBenefit.BenefitType = PhysicalGift`)

```text
EligibilityStatus:    NotEligible  ⇄  Eligible
FulfillmentStatus:    Pending  →  Granted  →  Delivered  (terminal)
```

* `Eligible` does NOT imply `Granted`. The grant is an explicit handler invocation (`MarkBenefitDeliveredCommand` after eligibility check).
* Once `Delivered`, the row is historically delivered forever. A later overdue installment may flip `Eligible → NotEligible` but must NOT touch `FulfillmentStatus`.

#### FreeMonths (`FreeMonthsBenefit`)

```text
EligibilityStatus:    NotEligible  ⇄  Eligible
FulfillmentStatus:    Pending  →  Granted  →  AppliedToSubscription  (terminal)
```

* `Eligible` does NOT imply `Granted`. The grant is the explicit step that records `GrantedAtUtc`.
* `Granted → AppliedToSubscription` is the explicit application step that increments `TenantPlan.BonusMonths` and extends `EffectiveEndsAtUtc`. Idempotent by `FreeMonthsBenefit.Id`.

### G.4 `EligibilityRule` — closed algebraic expression

```text
EligibilityRule
  ::= AllOf(EligibilityRule[])
    | AnyOf(EligibilityRule[])
    | ContractActive                              // Contract.Status == Active
    | PaymentTermsEq(PaymentTerms)                // Contract.PaymentTerms == X
    | PaymentMethodEq(string)                     // dominant method from active allocations
    | CompletedByUtc(DateTime)                    // ∃ Completed payment with CompletedAtUtc <= X
    | NoOverdueInstallment                        // ∄ Active installment in Overdue state
    | AmountPaidAtLeast(decimal)                  // Σ active completed allocations ≥ X
    | DaysFromContractStartGte(int)               // AsOfUtc - EffectiveAtUtc >= X days
    | DurationMonthsGte(int)                      // Contract.DurationMonths >= X
```

* Evaluated by `BenefitEligibilityEvaluator` (pure function over `(rule, contract, financialState, clock)`).
* Per-row: each `ContractBenefit` and each `FreeMonthsBenefit` carries exactly one `EligibilityRule`.

### G.5 Default rules for legacy data

* Legacy `ContractBenefit` rows with `BenefitType = PhysicalGift` are backfilled with `DefaultPhysicalGiftRule = AllOf([ContractActive, AmountPaidAtLeast(ContractedAmount), NoOverdueInstallment])` — **bit-equivalent** to today's `BenefitEligibilityService.CanBecomeEligible`.
* Legacy `FreeMonthsBenefit` rows backfilled from `Contract.BonusMonths > 0` carry `DefaultFreeMonthsRule = AllOf([ContractActive, PaymentTermsEq(FullUpfront), AmountPaidAtLeast(ContractedAmount)])`. This preserves the spirit of the existing upfront-only bonus semantics.

---

## H. Installment Relationship (corrected)

`Installment` remains the authoritative payment obligation / entitlement-period model. The **only** correction vs. the previous report is that `PaymentTerms` is **directional**, not bidirectional.

```text
Contract.PaymentTerms = Installments
    → CreateInstallmentScheduleCommand must succeed before first invoice issuance

Contract.PaymentTerms = FullUpfront
    → CreateInstallmentScheduleCommand is rejected for this contract
```

Schedule creation is an explicit lifecycle step (Task A decides the precise trigger: at contract activation vs. at first invoice issuance vs. at first customer payment). It is NOT inferred from `PaymentTerms` for any other purpose. Conversely, `PaymentTerms` is NOT inferred from installment rows.

No `Installment` schema, validation, or lifecycle change is required.

---

## I. Refund Interaction

[RefundCalculationService.cs#L47-L199](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs#L47-L199) is preserved. The minimum changes required:

* `RefundCalculationService.Calculate` gains an explicit `IReadOnlyList<FreeMonthsBenefit>` argument (separate from `IReadOnlyList<ContractBenefit>`). The handler `CalculateRefundCommand` loads both and the engine loops only over `ContractBenefit` for `BenefitContributions`; `FreeMonthsBenefit` rows are explicitly excluded with a comment justifying why (a bonus time-credit is not a recoverable monetary value).
* The `IsGranted`-style check is replaced with `FulfillmentStatus ∈ { Granted, Delivered }`. For the migrated bit-equivalent default rule + the migration backfill (`IsGranted = true ⇒ FulfillmentStatus = Delivered`), every existing security test refund expectation remains valid.
* `DiscountedMonths` is unchanged — already expressed as `ContractedAmount < BaseAmount`; no refund logic shift.

Bonus Months are never refunded as cash. PhysicalGift recovery continues to be day-based. No additional money math change.

---

## J. Required Changes

### Required (blocking the prompt scenarios)

1. `enum PaymentTerms` + `Offer.PaymentTerms` + `Contract.PaymentTerms`.
2. `EligibilityRule` value-object + per-row field on `ContractBenefit` and `FreeMonthsBenefit`.
3. `BenefitEligibilityEvaluator` domain service replacing the global rule in [BenefitEligibilityService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs).
4. `FreeMonthsBenefit` aggregate + `Contract.FreeMonthsBenefits[]` navigation.
5. `FulfillmentStatus` enum on `ContractBenefit` and `FreeMonthsBenefit`; `EligibilityStatus` retained for reversible rule evaluation.
6. `TenantPlan.AppliedFreeMonthsBenefitIds[]` to enforce idempotent application.
7. Migration backfilling `Contract.PaymentTerms` (default `Installments` for greenfield) and one `FreeMonthsBenefit` row per existing `Contract` with `BonusMonths > 0`.

### Recommended (improves auditability)

8. `PromotionType = BonusMonths` so the Offer chain can authoritatively create a `FreeMonthsBenefit` row from a promotion.
9. `PromotionType = PhysicalGift` (analogous) so gift rows also originate in the promotion pipeline.
10. `OfferBenefit` population in `CalculateAndPersistOfferCommand` from the new PromotionTypes.
11. Test coverage for scenarios A–D (see §M).

### Not Required

* No change to `Installment` schema, validation, or lifecycle.
* No change to `RefundCalculationService` beyond (a) explicit exclusion of `FreeMonthsBenefit` from the benefit loop and (b) `FulfillmentStatus` lookup replacing `IsGranted`.
* No change to `Invoice`, `Payment`, `PaymentAllocation`, `PaymentReceipt`, `BillingCycle`, `TenantCredit`, `Promotion` (other than adding the two new types), `Plan`, `SubscriptionPolicy`.
* No change to tenancy, audit logging, or concurrency tokens.
* No new aggregate named `CommercialBenefit`.

---

## K. Implementation Task Breakdown (smallest safe sequence)

**T1. `PaymentTerms` enum + Offer/Contract field + migration.**
* `Platform/Promotions/Enums/PaymentTerms.cs` (new).
* `Offer.PaymentTerms`, `Contract.PaymentTerms` (nullable column on both; Contract field becomes `required` after T3).
* Default rule at Offer calculation: `FullUpfront` if `PayForXMonths|PromotionalPrice|BonusMonths>0`, else `Installments`.
* Migration backfills existing contracts with `Installments` (greenfield-safe default) plus a backfilled `FreeMonthsBenefit` row for each with `BonusMonths > 0` (per §F.4).

**T2. `EligibilityRule` value-object + default-rule factory.**
* `Platform/Contracts/EligibilityRules/*.cs` (new sealed-class hierarchy).
* `ContractBenefit.EligibilityRule` and `OfferBenefit.EligibilityRule` columns added nullable.
* Backfill existing rows with `DefaultPhysicalGiftRule`.

**T3. `FreeMonthsBenefit` aggregate + `Contract.FreeMonthsBenefits[]` + migration.**
* `Platform/Contracts/FreeMonthsBenefit.cs` (new) carrying `EntitlementMonths`, `EligibilityStatus`, `EligibilityRule`, `FulfillmentStatus`, `GrantedAtUtc`, `AppliedAtUtc`, `RuleSnapshot` (serialized rule for audit).
* `Contract.AddFreeMonthsBenefit(...)` enforcing `EntitlementMonths > 0` and currency match.
* `Contract.ValidateSnapshotCompleteness` becomes the place to verify the `FreeMonthsBenefits` snapshot is well-formed. The `BonusMonths == Σ EntitlementMonths` invariant is **not** enforced until T11 (when the scalar column becomes derived); before T11, the scalar is preserved independently.

**T4. `BenefitEligibilityEvaluator` domain service.**
* Pure evaluator. `IBenefitEligibilityService` interface unchanged so DI does not break. Internal implementation is replaced.

**T5. `CheckBenefitEligibilityCommand` refactor.**
* Reads the benefit's `EligibilityRule`, calls the evaluator. Eligibility → `MarkEligible`. No grant step here.

**T6. Fulfillment lifecycle split.**
* `Platform/Contracts/Enums/FulfillmentStatus.cs` (new): `Pending / Granted / Delivered / AppliedToSubscription`.
* `ContractBenefit.FulfillmentStatus` and `FreeMonthsBenefit.FulfillmentStatus` columns.
* Migration backfill: `IsGranted = true ⇒ Delivered` (ContractBenefit), no backfill on FreeMonthsBenefit (greenfield).
* `RefundCalculationService` switches to `FulfillmentStatus ∈ { Granted, Delivered }`.

**T7. `GrantBenefitCommand` (new).**
* Explicit grant step. Reads the benefit's row, verifies `EligibilityStatus == Eligible` and `FulfillmentStatus == Pending`, sets `FulfillmentStatus = Granted` and `GrantedAtUtc`. Idempotent (returns success if already Granted).
* Called from the same flow that today calls `CheckBenefitEligibilityCommand` + `MarkBenefitDeliveredCommand`, but as a separate explicit step.

**T8. `ApplyFreeMonthsToSubscriptionCommand` (new).**
* Reads the `FreeMonthsBenefit` row, verifies `FulfillmentStatus == Granted` and that `TenantPlan.AppliedFreeMonthsBenefitIds` does not already contain its Id, then mutates `TenantPlan.BonusMonths += EntitlementMonths` and `EffectiveEndsAtUtc = AddCalendarMonths(EffectiveEndsAtUtc, EntitlementMonths)`. Sets `FulfillmentStatus = AppliedToSubscription`. Idempotent.
* Triggers `SubscriptionReconciliationService` to recompute lifecycle.

**T9. PromotionType `BonusMonths` + `PhysicalGift`.**
* Extend `PromotionCalculationService` to produce `OfferBenefits[]` rows for these two types.
* `CalculateAndPersistOfferCommand` writes those rows.

**T10. Refund explicit exclusion of `FreeMonthsBenefit`.**
* `RefundCalculationService.Calculate` signature change (new `IReadOnlyList<FreeMonthsBenefit>` argument, loop ignored). Handler `CalculateRefundCommand` loads both.

**T11. (Long-term) `Contract.BonusMonths` becomes derived.**
* Drop the scalar column; the canonical source becomes `Σ FreeMonthsBenefits.EntitlementMonths`.
* Out of scope for Task A; documented for the future cleanup task.

**T12. Test coverage for scenarios A–D** (see §M).

---

## L. Final Domain Invariants

In addition to the 20 invariants listed in the original prompt §16, this final baseline mandates:

21. `Contract.PaymentTerms` MUST equal the `PaymentTerms` value snapshotted from the accepted Offer.
22. **Directional**: `Contract.PaymentTerms == Installments ⇒ an installment schedule MUST exist before the first invoice for that contract can be issued`. The reverse is NOT an invariant.
23. `Contract.PaymentTerms == FullUpfront ⇒ NO installment schedule is created for this contract; any attempt to create one is rejected`.
24. Every benefit row (`ContractBenefit` and `FreeMonthsBenefit`) MUST carry exactly one `EligibilityRule`.
25. The default eligibility rule for legacy `PhysicalGift` rows is `AllOf([ContractActive, AmountPaidAtLeast(ContractedAmount), NoOverdueInstallment])` — bit-equivalent to today.
26. `FulfillmentStatus` is monotone: `Pending → Granted → (Delivered | AppliedToSubscription)`. No reverse transitions, no skip transitions.
27. `EligibilityStatus` is reversible: `NotEligible ⇄ Eligible` based on rule evaluation.
28. A `Delivered` PhysicalGift is historical and never reverts, regardless of later overdue state.
29. A `FreeMonthsBenefit.Id` MUST appear at most once in `TenantPlan.AppliedFreeMonthsBenefitIds[]`; the application step is idempotent.
30. `ContractedAmount` is independent of `FreeMonthsBenefits.EntitlementMonths` — bonus time credit does not increase the billable amount.
31. The refund calculation never treats `FreeMonthsBenefit` rows as recoverable monetary value.
32. The Offer/Contract snapshot of benefits is immutable after `Contract.Create(...)`; only `EligibilityStatus`, `FulfillmentStatus`, and `GrantedAtUtc`/`AppliedAtUtc` mutate thereafter.

---

## M. Final Scenario Validation

### Scenario A — 6 months / FullUpfront / 6 for price of 5

| Step | Expected behavior |
|---|---|
| Offer calculated with `PaymentTerms = FullUpfront`, `PromotionType = BonusMonths`, `BonusQuantity = 1`. | `Offer.Benefits[]` gets one `FreeMonthsBenefit` snapshot (`EntitlementMonths = 1`, `EligibilityRule = PaymentTermsEq(FullUpfront)`). |
| Contract created from accepted Offer. | `Contract.FreeMonthsBenefits` mirrors the Offer row. `Contract.PaymentTerms = FullUpfront`. No installment schedule created. `ContractedAmount = 5 × MonthlyListPrice`. |
| Customer pays `ContractedAmount` immediately. | `BenefitEligibilityEvaluator` returns `Eligible`. |
| `GrantBenefitCommand` runs for the bonus row. | `FulfillmentStatus: Pending → Granted`. |
| `ApplyFreeMonthsToSubscriptionCommand` runs for the bonus row. | `TenantPlan.BonusMonths += 1`, `EffectiveEndsAtUtc += 1 month` (calendar math). `TenantPlan.AppliedFreeMonthsBenefitIds += row.Id`. `FulfillmentStatus: Granted → AppliedToSubscription`. |
| Idempotency re-check. | Second invocation of either command is a no-op. |
| Billable amount? | `ContractedAmount` unchanged from `5 × MonthlyListPrice`. No accidental over-charge. |

### Scenario B — 12 months / FullUpfront / Cash within promotional deadline

| Step | Expected behavior |
|---|---|
| Offer calculated with `PaymentTerms = FullUpfront`, `PromotionType = PhysicalGift`, gift = "Barcode Printer", `PromotionalDeadline = T`. | `Offer.Benefits[]` gets one `ContractBenefit` snapshot (`BenefitType = PhysicalGift`, `EligibilityRule = AllOf([ContractActive, DurationMonthsGte(12), PaymentMethodEq(Cash), CompletedByUtc(T)])`). |
| Contract created. | `Contract.Benefits` mirrors the Offer row. `Contract.PaymentTerms = FullUpfront`. No installments. |
| Customer pays in cash before `T`. | Eligibility evaluator returns `Eligible` (`Cash` method verified from dominant payment method; `CompletedByUtc(T)` verified; `DurationMonths = 12 ≥ 12`). |
| `GrantBenefitCommand` runs. | `FulfillmentStatus: Pending → Granted`. |
| `MarkBenefitDeliveredCommand` runs (existing handler). | `FulfillmentStatus: Granted → Delivered`. |
| Customer later misses a payment obligation. | `EligibilityStatus` may flip to `NotEligible` if the rule re-evaluates against new state, but `FulfillmentStatus = Delivered` is **never** touched. Refund calculation continues to treat the gift as `Delivered`. |

### Scenario C — 12 months / PromotionalPrice / 12 for the price of 10

| Step | Expected behavior |
|---|---|
| Offer calculated with `PromotionType = PromotionalPrice`, `FinalAmount = 10 × MonthlyPrice`. | No `OfferBenefit` rows created. No `FreeMonthsBenefits` row. |
| Contract created. | `Contract.ContractedAmount = FinalAmount` (10 × MonthlyPrice). `Contract.BonusMonths = 0`. No installment schedule (FullUpfront). |
| Customer pays `ContractedAmount`. | No benefit eligibility check fires (no rows). No bonus months. No gift. |
| Refund on early cancellation. | Refundable surplus = `Paid - Used - Benefits = Paid - Used - 0`; the discount is implicitly returned as cash because `ContractedAmount` is the lower amount. |

### Scenario D — 6 months / Installments / customer pays everything early

| Step | Expected behavior |
|---|---|
| Offer calculated with `PaymentTerms = Installments`, `PromotionType = BonusMonths`, `BonusQuantity = 1`. | `Offer.Benefits[]` gets one `FreeMonthsBenefit` snapshot (`EntitlementMonths = 1`, `EligibilityRule = AllOf([ContractActive, PaymentTermsEq(FullUpfront)])`). |
| Contract created. | `Contract.PaymentTerms = Installments`. Installment schedule created. The FreeMonths row exists but `EligibilityRule` includes `PaymentTermsEq(FullUpfront)` which FAILS for this contract. |
| Customer pays all installments very early. | Installments move to `Paid`. No `Contract.PaymentTerms` mutation. The benefit remains `NotEligible` because `PaymentTerms` is unchanged. |
| Refund on early cancellation. | No `Delivered` / `Applied` benefit to recover. Customer's refundable balance is `Paid - Used - 0`. No accidental bonus application. |

---

# FINAL DESIGN BASELINE

> This section is the binding specification for Task A and subsequent implementation work.
> It supersedes any contradictory recommendation in §A–§M above (none should exist).

## 1. Final Domain Model

```text
Plan (catalog) → Offer (immutable snapshot)
                     │  PaymentTerms, DurationMonths, PromotionType, ChargedMonths
                     │  BonusMonths (catalog hint snapshot), OfferBenefits[]
                     ▼
                  Contract (immutable snapshot)
                     │  PaymentTerms, DurationMonths, ContractedAmount, ChargedMonths
                     │  BonusMonths (scalar, becomes derived in T11)
                     │  Benefits[] = ContractBenefit {BenefitType=PhysicalGift,
                     │      ContractualValue, EligibilityRule, EligibilityStatus,
                     │      FulfillmentStatus}
                     │  FreeMonthsBenefits[] = FreeMonthsBenefit {EntitlementMonths,
                     │      EligibilityRule, EligibilityStatus, FulfillmentStatus}
                     ▼
                  TenantPlan / Subscription
                     │  DurationMonths, BonusMonths (applied counter)
                     │  EffectiveEndsAtUtc = Base + BonusMonths (calendar math)
                     │  AppliedFreeMonthsBenefitIds[] (idempotency)
                     ▼
                  Installment Schedule (ONLY if Contract.PaymentTerms == Installments)
                     │
                     ▼
                  BillingCycle → Invoice → Payment → PaymentAllocation → Refund
```

## 2. Final PaymentTerms Semantics

* `enum PaymentTerms { FullUpfront = 0, Installments = 1 }`.
* `Offer.PaymentTerms` set at calculation time. Default: `FullUpfront` if `PromotionType ∈ {PayForXMonths, PromotionalPrice}` or `Plan.BonusMonths > 0`; else `Installments`.
* `Contract.PaymentTerms` snapshotted from Offer at Contract creation; immutable thereafter.
* **Directional invariant**: `PaymentTerms == Installments ⇒ schedule required` and `PaymentTerms == FullUpfront ⇒ schedule forbidden`. The reverse direction is not an invariant.

## 3. Final Commercial Benefit Model

```text
Commercial Benefit
├── Pricing Benefit            (no row; Offer/Contract price math)
│   ├── PayForXMonths
│   └── PromotionalPrice
└── Entitlement Benefit        (row on Contract)
    ├── FreeMonths             → FreeMonthsBenefit
    └── PhysicalGift           → ContractBenefit.BenefitType = PhysicalGift
```

Both Entitlement Benefit types share eligibility semantics but are **separate persistence aggregates** because their fulfillment actions diverge (subscription extension vs. physical handover).

## 4. Final FreeMonths Model

Four independent counters / states:

| Concept | Storage |
|---|---|
| Commercial Entitlement | `FreeMonthsBenefit.EntitlementMonths` (row, immutable) |
| Eligibility | `FreeMonthsBenefit.EligibilityStatus` (reversible) |
| Grant | `FreeMonthsBenefit.FulfillmentStatus = Granted` + `GrantedAtUtc` (one-way) |
| Application | `TenantPlan.AppliedFreeMonthsBenefitIds[]` + `TenantPlan.BonusMonths` (one-way, idempotent) |

`Contract.BonusMonths` is independent of these counters until T11; thereafter it becomes a denormalized projection.

## 5. Final PhysicalGift Model

| Concept | Storage |
|---|---|
| Commercial Definition | `ContractBenefit.{BenefitType, Name, ContractualValue, CurrencyCode}` (immutable) |
| Eligibility | `ContractBenefit.EligibilityStatus` (reversible) |
| Grant | `ContractBenefit.FulfillmentStatus = Granted` (one-way) |
| Delivery | `ContractBenefit.FulfillmentStatus = Delivered` + `GrantedAtUtc`/`DeliveredBy` (terminal) |

## 6. Final Eligibility / Fulfillment Lifecycle

```text
Eligibility (reversible)         Fulfillment (irreversible, monotone)
──────────────────────           ────────────────────────────────────
NotEligible  ⇄  Eligible   ───►  Pending → Granted → Delivered  (PhysicalGift)
                                Pending → Granted → AppliedToSubscription  (FreeMonths)
```

No `Earned`, `Consumed`, or `Withdrawn` states.

## 7. Final Installment Relationship

* `Installment` remains the authoritative payment obligation / entitlement-period model. No schema, validation, or lifecycle change.
* `Installment` schedule creation is triggered only when `Contract.PaymentTerms == Installments` and only at the lifecycle point chosen in Task A (likely contract activation).
* `Contract.PaymentTerms == FullUpfront` rejects any attempt to create an installment schedule for that contract.

## 8. Final Refund Implications

* `RefundCalculationService.Calculate` signature gains `IReadOnlyList<FreeMonthsBenefit>`; the engine loops only over `ContractBenefit` rows for `BenefitContributions`.
* `ContractBenefit.IsGranted` check is replaced with `FulfillmentStatus ∈ { Granted, Delivered }`. Migration backfills `IsGranted = true ⇒ FulfillmentStatus = Delivered`, preserving all existing security-test expectations.
* `FreeMonthsBenefit` is never recoverable as cash.
* `DiscountedMonths` (Pricing Benefit) is unchanged; the discount is implicitly returned via `ContractedAmount` math.

## 9. Final Domain Invariants

See §L (invariants 21–32). Highlights:

* Invariant 21: `Contract.PaymentTerms` equals the Offer snapshot value.
* Invariant 22: directional only — `Installments ⇒ schedule`; never the reverse.
* Invariant 26: `FulfillmentStatus` is monotone.
* Invariant 28: a `Delivered` PhysicalGift never reverts.
* Invariant 29: `FreeMonthsBenefit.Id` appears at most once in `TenantPlan.AppliedFreeMonthsBenefitIds[]`.
* Invariant 30: bonus months never increase `ContractedAmount`.

## 10. Final Implementation Sequence for Task A Onward

T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → T9 → T10 → T11 → T12 (see §K for details). Tasks T1–T8 are the Task A critical path; T9–T12 are follow-up.

---

## Stop Condition Confirmed

This document is the **binding design baseline** for Task A and beyond. It is documentation only.

* Files changed in this commit: `docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md` (rewritten).
* No production code, migrations, entities, controllers, or workflows were modified.
* All four open decisions from the previous report are now **resolved** in this baseline (see §D.1 / §E.3 / §F.4 / §G.5).
* No genuinely unresolved business decisions remain for Task A.
