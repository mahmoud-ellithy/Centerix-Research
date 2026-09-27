# CENTERIX — COMMERCIAL BENEFIT DESIGN VALIDATION & FINAL BASELINE

**Type:** Final design baseline for Task A onward. Supersedes the initial validation (`996237d`) and the previous baseline (`aed6e6a`).
**Scope:** Apply the two final corrections from the architecture review (no invented historical facts, no `PromotionType → PaymentTerms` inference), keep all four-level FreeMonths concepts and Commercial Benefit unification intact, and lock the implementation-ready baseline.
**Posture:** Documentation only. **No code, migrations, entities, controllers, or tests were modified.**
**Repository state at audit time:** `mahmoud-ellithy/Centerix-Research`, HEAD `6c6ed34`. Previous commits: `c92e170` (deep audit), `996237d` (initial validation), `aed6e6a` (first baseline correction) — both superseded by this document.

---

## A. Executive Summary

The Deep Financial Domain Audit (`c92e170`), the initial validation (`996237d`), and the first baseline correction (`aed6e6a`) reached these conclusions that this final baseline **confirms**:

1. **`Installment` is the authoritative payment obligation / entitlement-period model.** No new `PaymentObligation` aggregate is required.
2. **`PaymentTerms` does not exist anywhere** in the codebase; full-tree grep confirms zero matches.
3. **`BenefitEligibilityService` is a single global rule** used for every `ContractBenefit`. It conflates *eligibility* with *delivery*.
4. **`OfferBenefit` rows are never populated in production today** — the promotion pipeline carries price only.
5. **`Commercial Benefit` is the unified business concept** — Bonus Months and Physical Gifts are two manifestations of the same idea.
6. **`FreeMonths` keeps four independent counters/states**: Commercial Entitlement, Eligibility, Grant, Application.
7. **Eligibility is reversible; Fulfillment is monotonic; Application is idempotent.**

This document applies the **two final corrections**:

* **C1. `PaymentTerms` is NOT inferred from `PromotionType`.** A promotion does not inherently determine the payment mode. Whether an offer requires upfront payment is a rule of **that particular commercial offer** carried on `Offer.PaymentTerms`. All four combinations (`PayForXMonths | PromotionalPrice`) × (`FullUpfront | Installments`) must be representable.
* **C2. No historical commercial facts are invented.** The repository is greenfield. Migrations establish schema and update existing fixtures explicitly; they do NOT infer `PaymentTerms` from installment rows and do NOT manufacture `FreeMonthsBenefit` rows from `Contract.BonusMonths > 0` without authoritative business evidence.

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

Every requirement in the original prompt §13 is present in [Installment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L21-L345) and [CreateInstallmentScheduleCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L25-L270). The schedule factory accepts arbitrary `InstallmentScheduleItem` rows (custom amounts, custom due dates, custom covered periods). No `Installment` schema, validation, or lifecycle change is required.

### B.5 Offers — promotion service carries price only

`PromotionCalculationService` ([PromotionCalculationService.cs#L17-L146](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs#L17-L146)) returns only price/discount fields. `CalculateAndPersistOfferCommand` ([CalculateAndPersistOfferCommand.cs#L75-L132](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Promotions/Commands/CalculateAndPersistOfferCommand.cs#L75-L132)) snapshots PricingTiers and Features but never `OfferBenefit`. All benefit tests construct rows directly via `Contract.AddBenefit(...)`.

### B.6 Promotion type taxonomy

`PromotionType`: `PercentageDiscount / FixedAmountDiscount / PayForXMonths / PromotionalPrice`. Today:

* `PayForXMonths` collapses to `ContractedAmount` + `Contract.ChargedMonths` — customer pays less over the same period.
* `PromotionalPrice` lowers total without extending term — Example C "12 for the price of 10".
* `Plan.BonusMonths` is the only source of true "free months" — Example A "6 for the price of 5".

`PromotionType` carries **price math only**. It does NOT determine `PaymentTerms` and does NOT determine benefit eligibility.

### B.7 Greenfield on benefits

No seed data file, no production fixtures. Migrations end at `20260926221850_Task21_InvoiceMoneyPrecisionAlignment`. No historical `ContractBenefit` / `OfferBenefit` rows exist. Tests construct the domain in-memory. **There is no production legacy customer population whose historical commercial entitlement must be reconstructed.**

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
│     └ FreeMonthsBenefit snapshot │
│       + EligibilityRule ◄── NEW│
└──────────────┬─────────────────┘
               │  (only path to Contract)
               ▼
┌────────────────────────────────┐
│ Contract        (immutable snapshot) │
│   - DurationMonths             │
│   - PaymentTerms ◄── NEW, snapshotted from Offer, immutable│
│   - BonusMonths (legacy scalar; preserved unchanged)     │
│   - ContractedAmount                                        │
│   - Benefits[]                                               │
│     └ ContractBenefit {                                     │
│         BenefitType,                                         │
│         ContractualValue,                                    │
│         EligibilityRule ◄── NEW                              │
│         EligibilityStatus                                    │
│         FulfillmentStatus ◄── NEW                            │
│       }                                                       │
│   - FreeMonthsBenefits[] ◄── NEW navigation                  │
│     └ FreeMonthsBenefit {                                    │
│         EntitlementMonths (commercial entitlement, immutable) │
│         EligibilityRule ◄── NEW                              │
│         EligibilityStatus                                    │
│         FulfillmentStatus ◄── NEW                            │
│         GrantedAtUtc                                         │
│         AppliedAtUtc                                         │
│       }                                                       │
└──────────────┬─────────────────┘
               │  (Contract.GetSubscriptionSnapshot)
               ▼
┌────────────────────────────────┐
│ TenantPlan / Subscription      │
│   - DurationMonths             │
│   - BonusMonths (applied counter; authoritative for EffectiveEndsAtUtc)│
│   - StartsAtUtc, BaseEndsAtUtc │
│   - EffectiveEndsAtUtc = Base + BonusMonths (calendar math)│
│   - AppliedFreeMonthsBenefitIds[] ◄── NEW: which FreeMonthsBenefit rows have already been applied│
└──────────────┬─────────────────┘
               │  (Installment schedule is a SEPARATE lifecycle step, created ONLY if Contract.PaymentTerms == Installments)
               ▼
┌────────────────────────────────┐
│ Installment Schedule / Invoice / Payment / Refund / TenantCredit │
└────────────────────────────────┘
```

**Direction-of-truth:** `PaymentTerms` is the commercial decision on `Offer` and is snapshotted onto `Contract`. Installments are a *consequence* of `Contract.PaymentTerms == Installments` and are created by an explicit schedule-creation step. **PaymentTerms is never inferred from installment rows; installment rows are never used to determine the original commercial agreement.**

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

A single `ContractBenefit` aggregate is **not** polymorphic enough to carry FreeMonths correctly: FreeMonths must call `TenantPlan.ApplyBonusMonths(...)` on fulfillment, while PhysicalGift must record `DeliveredBy` and stop. We therefore keep `FreeMonthsBenefit` and `ContractBenefit` as separate aggregates that share the `EligibilityRule` evaluation pipeline. **We do NOT introduce a third aggregate named `CommercialBenefit`** — the business concept is unified; the technical persistence is split because the fulfillment lifecycles genuinely differ.

### D.3 Shared semantics across Entitlement Benefits

Every Entitlement Benefit row — whether `FreeMonthsBenefit` or `ContractBenefit` — satisfies all five:

1. Granted by an Offer/Contract (Offer carries a snapshot, Contract snapshots it from the Offer).
2. Commercial definition is **immutable** after Contract creation.
3. Eligibility conditions are **explicit** and per-row (carried in `EligibilityRule`).
4. Historical state is **auditable** via the fulfillment lifecycle.
5. Fulfillment is **idempotent**.

### D.4 `PromotionType` does NOT determine benefit eligibility

`PayForXMonths` and `PromotionalPrice` are **Pricing Benefits**. They affect the commercial amount only. Whether a particular offer's bonus requires upfront payment is a property of that offer's `FreeMonthsBenefit.EligibilityRule`, not a property of the promotion enum. A future offer may grant the same bonus with a different `EligibilityRule` (e.g. "pay 80% of contracted amount" instead of "FullUpfront") without changing `PromotionType`.

---

## E. Payment Terms — commercial decision, no inference

### E.1 Final placement

* `enum PaymentTerms { FullUpfront = 0, Installments = 1 }`.
* `Offer.PaymentTerms` — set explicitly by the platform operator at Offer calculation time.
* `Contract.PaymentTerms` — snapshotted from Offer at Contract creation. **Immutable thereafter.**

Not on `TenantPlan`, `Installment`, `Payment`, `BillingCycle`, `Invoice`.

### E.2 Directional invariant

```text
Contract.PaymentTerms == Installments
    ⇒
    an installment schedule MUST be created before the contract is fully
    operationally live (i.e. before the first invoice for that contract
    can be issued)

Contract.PaymentTerms == FullUpfront
    ⇒
    NO installment schedule is created for this contract
    (any attempt to create one is rejected)
```

The **reverse direction is not an invariant**:

* The existence of installment rows does NOT mean `PaymentTerms == Installments`. An installment schedule could exist as a financial-execution artifact under a `FullUpfront` contract only by error; if so, the schedule is the anomaly, not `PaymentTerms`.
* The absence of installment rows does NOT mean `PaymentTerms == FullUpfront`. An `Installments` contract may simply not yet have its schedule created (lifecycle ordering).

**PaymentTerms is the commercial decision. The schedule is the financial execution. The schedule cannot rewrite PaymentTerms.**

### E.3 PaymentTerms is NOT derived from `PromotionType`

The previous baseline contained rules such as:

```text
PayForXMonths / PromotionalPrice → FullUpfront
Plan.BonusMonths > 0              → FullUpfront
otherwise                          → Installments
```

These rules are **removed**. They were an unsupported commercial assumption.

The actual business reality is that every combination of `PromotionType` × `PaymentTerms` must be expressible:

```text
12 months, PayForXMonths,      FullUpfront    // 6-for-5 paid up front
12 months, PayForXMonths,      Installments   // 6-for-5 spread across installments
12 months, PromotionalPrice,   FullUpfront    // 12 for the price of 10, paid up front
12 months, PromotionalPrice,   Installments   // 12 for the price of 10, spread across installments
```

`PromotionType` carries **price math only**. Whether a specific offer requires upfront payment is determined by:

1. The platform operator's explicit choice of `Offer.PaymentTerms` at Offer calculation time, and
2. The `EligibilityRule` carried on each `FreeMonthsBenefit` / `ContractBenefit` row.

### E.4 Eligibility rule is authoritative for bonus payment-mode requirements

A bonus that requires upfront payment is expressed by the **EligibilityRule**, not by `PromotionType`:

```text
// Upfront-only bonus
AllOf(
    ContractActive,
    PaymentTermsEq(FullUpfront),
    AmountPaidAtLeast(ContractedAmount)
)
```

A future bonus that allows either payment mode is expressed as:

```text
// Bonus regardless of payment mode
AllOf(
    ContractActive,
    AmountPaidAtLeast(ContractedAmount)
)
```

Both rules are valid; both are first-class. The benefit's rule, not the promotion enum, is authoritative for bonus eligibility.

### E.5 Early settlement MUST NOT rewrite PaymentTerms

```text
Contract.PaymentTerms == Installments
    ⇒
    remains Installments for the lifetime of the contract, regardless of
    how early the customer settles the installments.
```

Customer behavior (early payment, late payment, full settlement) operates on **financial execution**. It cannot alter the **commercial agreement**. A customer's `PaymentTerms` is the contractual fact that was in force at Offer acceptance time; it is preserved unchanged.

---

## F. FreeMonths Benefit — final model

### F.1 The four distinctions (preserved verbatim)

The four counters / states that were conflated by the initial validation are now **independent**:

| Concept | Storage | Meaning | Mutability |
|---|---|---|---|
| **Commercial Entitlement** | `FreeMonthsBenefit.EntitlementMonths` (row on Contract) | "The contract grants N free months as a commercial benefit." Configured at Offer/Contract creation. | Immutable after Contract creation. |
| **Eligibility** | `FreeMonthsBenefit.EligibilityStatus ∈ {NotEligible, Eligible}` | "Conditions currently allow grant." Re-evaluated from `EligibilityRule`. | Reversible. |
| **Grant** | `FreeMonthsBenefit.FulfillmentStatus = Granted` + `FreeMonthsBenefit.GrantedAtUtc` | "The grant decision has been recorded." Set once when eligibility becomes true. | Irreversible. |
| **Application** | `TenantPlan.AppliedFreeMonthsBenefitIds[]` + `TenantPlan.BonusMonths` counter | "The grant has been added to the subscription's entitlement period." The authoritative extension of `EffectiveEndsAtUtc`. | Append-only; idempotent by `FreeMonthsBenefitId`. |

### F.2 Critical invariants

* `Contract.BonusMonths` is **NOT** `Σ FreeMonthsBenefits.GrantedMonths` and is **NOT** `Σ FreeMonthsBenefits.EntitlementMonths`. The legacy scalar is preserved unchanged for backward compatibility until T11 makes it derived; until then, the two are independent fields and `Contract.ValidateSnapshotCompleteness` does NOT enforce any sum invariant between them.
* A `FreeMonthsBenefit` row with `EntitlementMonths = 1` represents the **commercial entitlement**: "the contract grants one free month." It does **not** mean `Granted = true` or `Applied = true`.
* Eligibility can change (the rule may fail later).
* Grant is historical (recorded once).
* Application is historical and idempotent (the same `FreeMonthsBenefit.Id` may not extend the subscription twice).
* Bonus months never increase the **billable contractual amount** — `ContractedAmount` is unchanged by `EntitlementMonths`.
* Bonus months **may** increase service entitlement — `TenantPlan.EffectiveEndsAtUtc` extends when the bonus is applied.
* Eligibility checking must not itself cause duplicate subscription extension — the extension happens only on the explicit grant-and-apply step, which is idempotent.

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
* `Pending → Granted` is the only grant transition.
* `Granted → AppliedToSubscription` is the only application transition; it mutates `TenantPlan.BonusMonths` and `EffectiveEndsAtUtc` exactly once.
* `AppliedToSubscription` is terminal.

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

* `Eligible` does NOT imply `Granted`. The grant is an explicit handler invocation.
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
* Whether a particular benefit requires `PaymentTerms == FullUpfront` is a property of **that benefit's** rule. It is not a global rule of the system.

### G.5 Default rules for legacy data

* Legacy `ContractBenefit` rows with `BenefitType = PhysicalGift` are backfilled with `DefaultPhysicalGiftRule = AllOf([ContractActive, AmountPaidAtLeast(ContractedAmount), NoOverdueInstallment])` — **bit-equivalent** to today's `BenefitEligibilityService.CanBecomeEligible`.
* Legacy `FreeMonthsBenefit` rows — see §J.4 — are NOT backfilled from `Contract.BonusMonths > 0`. The migration does not invent historical commercial entitlements (see §J.4 and §E for rationale).

---

## H. Installment Relationship (directional)

`Installment` remains the authoritative payment obligation / entitlement-period model. The only correction vs. the original report is that the relationship is **directional**, not bidirectional.

```text
Contract.PaymentTerms == Installments
    → CreateInstallmentScheduleCommand must succeed before first invoice issuance

Contract.PaymentTerms == FullUpfront
    → CreateInstallmentScheduleCommand is rejected for this contract
```

Schedule creation is an explicit lifecycle step. The exact trigger (contract activation, first invoice, first payment, or another lifecycle point) is **NOT** decided by this design baseline. It is **deferred to the installment-schedule integration task**. What this baseline does lock is the directional invariant in §E.2 and the directional forbiddenness in §E.2 — not the timing. The reverse direction is not an invariant: an existing installment schedule does NOT determine `PaymentTerms`, and a missing schedule does NOT determine `PaymentTerms` either.

No `Installment` schema, validation, or lifecycle change is required.

---

## I. Refund Interaction

[RefundCalculationService.cs#L47-L199](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs#L47-L199) is preserved. The minimum changes required:

* `RefundCalculationService.Calculate` gains an explicit `IReadOnlyList<FreeMonthsBenefit>` argument (separate from `IReadOnlyList<ContractBenefit>`). The engine loops only over `ContractBenefit` for `BenefitContributions`; `FreeMonthsBenefit` rows are explicitly excluded with a comment justifying why (a bonus time-credit is not a recoverable monetary value).
* The `IsGranted`-style check is replaced with `FulfillmentStatus ∈ { Granted, Delivered }`. For the migrated bit-equivalent default rule + the migration backfill (`IsGranted = true ⇒ FulfillmentStatus = Delivered`), every existing security test refund expectation remains valid.
* `DiscountedMonths` (Pricing Benefit) is unchanged — already expressed as `ContractedAmount < BaseAmount`; no refund logic shift.

Bonus Months are never refunded as cash. PhysicalGift recovery continues to be day-based. No additional money math change.

---

## J. Required Changes

### J.1 Required (blocking the prompt scenarios)

1. `enum PaymentTerms` + `Offer.PaymentTerms` + `Contract.PaymentTerms`.
2. `EligibilityRule` value-object + per-row field on `ContractBenefit` and `FreeMonthsBenefit`.
3. `BenefitEligibilityEvaluator` domain service replacing the global rule in [BenefitEligibilityService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs).
4. `FreeMonthsBenefit` aggregate + `Contract.FreeMonthsBenefits[]` navigation.
5. `FulfillmentStatus` enum on `ContractBenefit` and `FreeMonthsBenefit`; `EligibilityStatus` retained for reversible rule evaluation.
6. `TenantPlan.AppliedFreeMonthsBenefitIds[]` to enforce idempotent application.
7. **Schema migration only** for the new `PaymentTerms` and `FreeMonthsBenefits` tables (see §J.4 for the greenfield position).

### J.2 Recommended (improves auditability)

8. `PromotionType = BonusMonths` so the Offer chain can authoritatively create a `FreeMonthsBenefit` row from a promotion (T9 below).
9. `PromotionType = PhysicalGift` (analogous) so gift rows also originate in the promotion pipeline.
10. `OfferBenefit` population in `CalculateAndPersistOfferCommand` from the new PromotionTypes.
11. Test coverage for scenarios A–D (see §K).

### J.3 Not Required

* No change to `Installment` schema, validation, or lifecycle.
* No change to `RefundCalculationService` beyond (a) explicit exclusion of `FreeMonthsBenefit` from the benefit loop and (b) `FulfillmentStatus` lookup replacing `IsGranted`.
* No change to `Invoice`, `Payment`, `PaymentAllocation`, `PaymentReceipt`, `BillingCycle`, `TenantCredit`, `Promotion` (other than adding the two new types), `Plan`, `SubscriptionPolicy`.
* No change to tenancy, audit logging, or concurrency tokens.
* No new aggregate named `CommercialBenefit`.
* No automatic inference of `PaymentTerms` from `PromotionType`.
* No historical reconstruction of `FreeMonthsBenefit` rows from `Contract.BonusMonths > 0`.

### J.4 Migration / Greenfield Position (corrected)

This repository is **greenfield on benefits**. No seed data, no production fixtures, no production legacy customer population. The `Seed*.cs` glob returns empty; all tests construct the domain in-memory.

The migration policy is:

1. **Schema migration** — establish the new schema (`Contract.PaymentTerms` column, `ContractBenefits.EligibilityRule` column, `ContractBenefits.FulfillmentStatus` column, new `FreeMonthsBenefits` table, `TenantPlans.AppliedFreeMonthsBenefitIds` column). For columns that must be `NOT NULL` for application correctness, an explicit operator-supplied default is the only acceptable value when actual persisted data exists.
2. **Fixture update** — explicitly update existing tests / development fixtures to set `PaymentTerms` and benefit eligibility rules where commercial meaning is intended. No silent default values are invented.
3. **No historical commercial reconstruction** — the migration does **NOT**:
   * Infer `PaymentTerms` from existing installment rows.
   * Default `PaymentTerms` to `Installments` (or anything else) based on "unknown historical state".
   * Manufacture `FreeMonthsBenefit` rows from `Contract.BonusMonths > 0` without business evidence.
   * Manufacture `ContractBenefit` rows from `ContractBenefit`-like legacy data without explicit operator confirmation.
4. **Production data exception** — if, at the moment of migration, real production data exists with rows that would be rejected by the new schema, those rows must be handled by an explicit operator-supplied data-fix step that establishes the historical commercial meaning from authoritative business evidence (contracts, signed offers, billing records). The migration script itself must not invent this meaning.

The migration is a **schema change**. It is not a **historical commercial reconstruction**. These are two different things and the implementation must keep them separate.

---

## K. Final Scenario Validation

### Scenario A — 6 months for price of 5, upfront

```text
Duration = 6 months
PaymentTerms = FullUpfront
Commercial Benefit = FreeMonths(1)
```

| Step | Expected behavior |
|---|---|
| Offer calculated with `PaymentTerms = FullUpfront`, `PromotionType = PayForXMonths` with `ChargedMonths = 5`, benefit row `FreeMonthsBenefit(EntitlementMonths = 1, EligibilityRule = AllOf([ContractActive, PaymentTermsEq(FullUpfront), AmountPaidAtLeast(ContractedAmount)]))`. | `Offer` carries the FreeMonths row snapshot. `PaymentTerms = FullUpfront` chosen explicitly by the operator (not inferred from `PayForXMonths`). |
| Contract created from accepted Offer. | `Contract.PaymentTerms = FullUpfront` snapshotted. `Contract.FreeMonthsBenefits` mirrors the Offer row. **No installment schedule is created.** `ContractedAmount = 5 × MonthlyListPrice`. |
| Customer pays `ContractedAmount` upfront. | `BenefitEligibilityEvaluator` returns `Eligible`. |
| `GrantBenefitCommand` runs for the bonus row. | `FulfillmentStatus: Pending → Granted`. |
| `ApplyFreeMonthsToSubscriptionCommand` runs for the bonus row. | `TenantPlan.BonusMonths += 1`, `EffectiveEndsAtUtc += 1 month` (calendar math). `TenantPlan.AppliedFreeMonthsBenefitIds += row.Id`. `FulfillmentStatus: Granted → AppliedToSubscription`. |
| Idempotency re-check. | Second invocation of either command is a no-op. |
| Billable amount? | `ContractedAmount` unchanged from `5 × MonthlyListPrice`. No accidental over-charge. Customer receives 6 months of service, pays for 5. |

### Scenario B — 6 months for price of 5, installments

```text
Duration = 6 months
PaymentTerms = Installments
Commercial Benefit rule requires FullUpfront
```

| Step | Expected behavior |
|---|---|
| Offer calculated with `PaymentTerms = Installments`, `PromotionType = PayForXMonths` with `ChargedMonths = 5`, benefit row `FreeMonthsBenefit(EntitlementMonths = 1, EligibilityRule = AllOf([ContractActive, PaymentTermsEq(FullUpfront), AmountPaidAtLeast(ContractedAmount)]))`. | `Offer` carries the FreeMonths row snapshot. `PaymentTerms = Installments` chosen explicitly (this is a valid combination even with `PayForXMonths`). |
| Contract created. | `Contract.PaymentTerms = Installments` snapshotted. Installment schedule created. The FreeMonths row exists but its `EligibilityRule` includes `PaymentTermsEq(FullUpfront)` which FAILS for this contract. |
| Customer pays all installments very early. | All installments move to `Paid`. **`Contract.PaymentTerms` is NOT mutated.** The FreeMonths row remains `NotEligible` because `PaymentTerms == Installments` is unchanged. |
| Customer pays the full amount early. | **This does NOT retroactively convert the contract into FullUpfront.** The contractual commercial decision remains Installments. |
| Refund on early cancellation. | No `Delivered` / `Applied` benefit to recover. Customer's refundable balance is `Paid - Used - 0`. No accidental bonus application. |

### Scenario C — Annual physical gift

```text
Duration = 12 months
PaymentTerms = FullUpfront
PaymentMethod = Cash
Payment completed within promotion window
PhysicalGift = Printer / PC
```

| Step | Expected behavior |
|---|---|
| Offer calculated with `PaymentTerms = FullUpfront`, `PromotionType = PercentageDiscount` (or any other), benefit row `ContractBenefit(BenefitType = PhysicalGift, ContractualValue = 1500, EligibilityRule = AllOf([ContractActive, DurationMonthsGte(12), PaymentMethodEq(Cash), CompletedByUtc(T)]))`. | `Offer` carries the PhysicalGift row snapshot. `PaymentTerms = FullUpfront` chosen explicitly. |
| Contract created. | `Contract.Benefits` mirrors the Offer row. `Contract.PaymentTerms = FullUpfront`. No installments. |
| Customer pays in cash before `T`. | Eligibility evaluator returns `Eligible` (`Cash` method verified from dominant payment method; `CompletedByUtc(T)` verified; `DurationMonths = 12 ≥ 12`). |
| `GrantBenefitCommand` runs. | `FulfillmentStatus: Pending → Granted`. |
| `MarkBenefitDeliveredCommand` runs. | `FulfillmentStatus: Granted → Delivered`. |
| Customer later misses a payment obligation. | `EligibilityStatus` may flip to `NotEligible` if the rule re-evaluates against new state, but `FulfillmentStatus = Delivered` is **never** touched. Refund calculation continues to treat the gift as `Delivered`. |

### Scenario D — Promotional price

```text
Duration = 12 months
PromotionType = PromotionalPrice
```

| Step | Expected behavior |
|---|---|
| Offer calculated with `PromotionType = PromotionalPrice`, `FinalAmount = 10 × MonthlyPrice`, no benefit rows. | `Offer.PromotionType = PromotionalPrice`. **No `FreeMonthsBenefit` rows. No `ContractBenefit` rows. `Offer.PaymentTerms` chosen explicitly by the operator** (this scenario works with either `FullUpfront` or `Installments` — the PromotionType does not determine it). |
| Contract created. | `Contract.ContractedAmount = FinalAmount` (10 × MonthlyPrice). `Contract.BonusMonths = 0`. `Contract.FreeMonthsBenefits` is empty. `Contract.Benefits` is empty. |
| Customer pays `ContractedAmount`. | No benefit eligibility check fires (no rows). No bonus months. No gift. |
| Refund on early cancellation. | Refundable surplus = `Paid - Used - Benefits = Paid - Used - 0`; the discount is implicitly returned as cash because `ContractedAmount` is the lower amount. |

---

## L. Final Domain Invariants

In addition to the 20 invariants listed in the original prompt §16, this final baseline mandates:

21. `Contract.PaymentTerms` MUST equal the `PaymentTerms` value explicitly accepted in the Offer. (Established by Offer creation, snapshotted at Contract creation.)
22. `Contract.PaymentTerms` is **immutable** after Contract acceptance.
23. **Directional**: `Contract.PaymentTerms == Installments ⇒ an installment schedule MUST exist before the first invoice for that contract can be issued`. The reverse is NOT an invariant. Payment execution cannot rewrite `Contract.PaymentTerms`.
24. `Contract.PaymentTerms == FullUpfront ⇒ NO installment schedule is created for this contract; any attempt to create one is rejected`.
25. `Contract.PaymentTerms` MUST NOT be inferred from `PromotionType` or from the existence of installment rows.
26. Every benefit row (`ContractBenefit` and `FreeMonthsBenefit`) MUST carry exactly one `EligibilityRule`.
27. The default eligibility rule for legacy `PhysicalGift` rows is `AllOf([ContractActive, AmountPaidAtLeast(ContractedAmount), NoOverdueInstallment])` — bit-equivalent to today.
28. `FulfillmentStatus` is monotone: `Pending → Granted → (Delivered | AppliedToSubscription)`. No reverse transitions, no skip transitions.
29. `EligibilityStatus` is reversible: `NotEligible ⇄ Eligible` based on rule evaluation.
30. A `Delivered` PhysicalGift is historical and never reverts, regardless of later overdue state.
31. A `FreeMonthsBenefit.Id` MUST appear at most once in `TenantPlan.AppliedFreeMonthsBenefitIds[]`; the application step is idempotent.
32. `ContractedAmount` is independent of `FreeMonthsBenefits.EntitlementMonths` — bonus time credit does not increase the billable amount.
33. The refund calculation never treats `FreeMonthsBenefit` rows as recoverable monetary value.
34. The Offer/Contract snapshot of benefits is immutable after `Contract.Create(...)`; only `EligibilityStatus`, `FulfillmentStatus`, and `GrantedAtUtc`/`AppliedAtUtc` mutate thereafter.
35. **No historical commercial facts are invented by migrations.** Schema migrations do not infer `PaymentTerms` from installment rows and do not manufacture `FreeMonthsBenefit` rows from `Contract.BonusMonths > 0`.

---

## M. Implementation Task Breakdown (smallest safe sequence)

**T1. `PaymentTerms` enum + Offer/Contract field + schema migration.**
* `Platform/Promotions/Enums/PaymentTerms.cs` (new).
* `Offer.PaymentTerms`, `Contract.PaymentTerms` (nullable column on both; Contract field becomes `required` after T3).
* **The migration does NOT default existing rows to any value** based on `PromotionType` or installment-row inference. For greenfield, the column starts nullable and tests / fixtures are updated to set it explicitly. For pre-existing data with real persisted rows, the operator must supply an explicit value via a documented data-fix step before the column is set to `NOT NULL`.
* Files: `Platform/Promotions/Enums/PaymentTerms.cs` (new), `Offer.cs`, `Contract.cs`, `Centerix.Infrastructure/Data/Migrations/<TS>_AddPaymentTerms.cs`.

**T2. `EligibilityRule` value-object + default-rule factory.**
* `Platform/Contracts/EligibilityRules/*.cs` (new sealed-class hierarchy).
* `ContractBenefit.EligibilityRule` and `OfferBenefit.EligibilityRule` columns added nullable.
* Backfill existing rows with `DefaultPhysicalGiftRule` only if existing rows are part of test fixtures; production rows must be backfilled by an operator-supplied step.
* Files: `Platform/Contracts/EligibilityRules/*.cs` (new), `ContractBenefit.cs`, `OfferBenefit.cs`, migration file.

**T3. `FreeMonthsBenefit` aggregate + `Contract.FreeMonthsBenefits[]` + schema migration.**
* `Platform/Contracts/FreeMonthsBenefit.cs` (new) carrying `EntitlementMonths`, `EligibilityStatus`, `EligibilityRule`, `FulfillmentStatus`, `GrantedAtUtc`, `AppliedAtUtc`, `RuleSnapshot` (serialized rule for audit).
* `Contract.AddFreeMonthsBenefit(...)` enforcing `EntitlementMonths > 0` and currency match.
* **The migration adds the table; it does NOT populate it from `Contract.BonusMonths > 0`.** The `BonusMonths` scalar remains the legacy field. If the operator wants to expose bonus months as `FreeMonthsBenefit` rows for an existing contract, they do so via an explicit fixture / data-fix step.
* Files: `Platform/Contracts/FreeMonthsBenefit.cs` (new), `Contract.cs`, migration file.

**T4. `BenefitEligibilityEvaluator` domain service.**
* Pure evaluator. `IBenefitEligibilityService` interface unchanged so DI does not break. Internal implementation is replaced.
* Files: `Infrastructure/Platform/Services/BenefitEligibilityService.cs` (rewrite), `Application/Platform/Contracts/Services/IBenefitEligibilityService.cs` (unchanged).

**T5. `CheckBenefitEligibilityCommand` refactor.**
* Reads the benefit's `EligibilityRule`, calls the evaluator. Eligibility → `MarkEligible`. No grant step here.
* Files: `Application/Platform/Contracts/Commands/CheckBenefitEligibilityCommand.cs`.

**T6. Fulfillment lifecycle split.**
* `Platform/Contracts/Enums/FulfillmentStatus.cs` (new): `Pending / Granted / Delivered / AppliedToSubscription`.
* `ContractBenefit.FulfillmentStatus` and `FreeMonthsBenefit.FulfillmentStatus` columns.
* Migration backfill: `IsGranted = true ⇒ FulfillmentStatus = Delivered` (ContractBenefit), no backfill on FreeMonthsBenefit (greenfield).
* `RefundCalculationService` switches to `FulfillmentStatus ∈ { Granted, Delivered }`.
* Files: `BenefitEligibilityStatus.cs` (keep, do not delete), `Platform/Contracts/Enums/FulfillmentStatus.cs` (new), `ContractBenefit.cs`, `RefundCalculationService.cs`, migration file.

**T7. `GrantBenefitCommand` (new).**
* Explicit grant step. Reads the benefit's row, verifies `EligibilityStatus == Eligible` and `FulfillmentStatus == Pending`, sets `FulfillmentStatus = Granted` and `GrantedAtUtc`. Idempotent (returns success if already Granted).
* Files: `Application/Platform/Contracts/Commands/GrantBenefitCommand.cs` (new).

**T8. `ApplyFreeMonthsToSubscriptionCommand` (new).**
* Reads the `FreeMonthsBenefit` row, verifies `FulfillmentStatus == Granted` and that `TenantPlan.AppliedFreeMonthsBenefitIds` does not already contain its Id, then mutates `TenantPlan.BonusMonths += EntitlementMonths` and `EffectiveEndsAtUtc = AddCalendarMonths(EffectiveEndsAtUtc, EntitlementMonths)`. Sets `FulfillmentStatus = AppliedToSubscription`. **Idempotent** by `FreeMonthsBenefit.Id`.
* Triggers `SubscriptionReconciliationService` to recompute lifecycle.
* Files: `Application/Platform/Subscriptions/Commands/ApplyFreeMonthsToSubscriptionCommand.cs` (new).

**T9. PromotionType `BonusMonths` + `PhysicalGift`.**
* Extend `PromotionCalculationService` to produce `OfferBenefits[]` rows for these two types.
* `CalculateAndPersistOfferCommand` writes those rows.
* The `PromotionType` is **price math only**; the benefit row carries its own `EligibilityRule` (which may or may not include `PaymentTermsEq(FullUpfront)`).
* Files: `PromotionCalculationService.cs`, `CalculateAndPersistOfferCommand.cs`.

**T10. Refund explicit exclusion of `FreeMonthsBenefit`.**
* `RefundCalculationService.Calculate` signature change (new `IReadOnlyList<FreeMonthsBenefit>` argument, loop ignored). Handler `CalculateRefundCommand` loads both.
* Files: `RefundCalculationService.cs`, `CalculateRefundCommand.cs`, `ExecuteRefundCommand.cs`.

**T11. (Long-term) `Contract.BonusMonths` becomes derived.**
* Drop the scalar column; the canonical source becomes `Σ FreeMonthsBenefits.EntitlementMonths`.
* Out of scope for Task A; documented for the future cleanup task.

**T12. Test coverage for scenarios A–D** (see §K).

---

# FINAL DESIGN BASELINE

> This section is the binding specification for Task A and subsequent implementation work.
> It supersedes any contradictory recommendation in §A–§M above (none should exist).

## 1. Final Domain Model

```text
Plan (catalog) → Offer (immutable snapshot)
                     │  PaymentTerms (operator-chosen, NOT inferred from PromotionType)
                     │  DurationMonths, PromotionType (price-only), ChargedMonths
                     │  BonusMonths (catalog hint), OfferBenefits[]
                     ▼
                  Contract (immutable snapshot)
                     │  PaymentTerms (snapshotted from Offer, immutable)
                     │  DurationMonths, ContractedAmount, ChargedMonths
                     │  BonusMonths (legacy scalar; becomes derived in T11)
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
                  Installment Schedule  (ONLY if Contract.PaymentTerms == Installments)
                     │
                     ▼
                  BillingCycle → Invoice → Payment → PaymentAllocation → Refund
```

## 2. Final PaymentTerms Semantics

```text
Offer.PaymentTerms is an explicit commercial decision.

Contract.PaymentTerms is an immutable snapshot of the accepted Offer.PaymentTerms.

PromotionType does not determine PaymentTerms.

Benefit eligibility may depend on PaymentTerms through the benefit's own EligibilityRule.

Installment rows never determine or rewrite PaymentTerms.
```

* `enum PaymentTerms { FullUpfront = 0, Installments = 1 }`.
* `Offer.PaymentTerms` is set **explicitly** by the platform operator at Offer calculation time. **It is NOT inferred from `PromotionType`** and **NOT inferred from any other commercial field**. All six combinations are valid:

  ```text
  PayForXMonths   + FullUpfront
  PayForXMonths   + Installments
  PromotionalPrice + FullUpfront
  PromotionalPrice + Installments
  BonusMonths      + FullUpfront
  BonusMonths      + Installments
  ```

* Whether a particular bonus requires upfront payment is expressed by that benefit's `EligibilityRule` (e.g. `PaymentTermsEq(FullUpfront)`). It is **not** a property of `PromotionType` and **not** a global rule for all `BonusMonths`.
* `Contract.PaymentTerms` is **snapshotted from `Offer` at Contract creation and is immutable thereafter**.
* **Directional invariant only**:
  * `PaymentTerms == Installments ⇒ installment schedule is required before the first invoice for that contract can be issued`.
  * `PaymentTerms == FullUpfront ⇒ no installment schedule may be created for that contract`.
  * The reverse direction is NOT an invariant. The installment schedule does not determine `PaymentTerms`, and `PaymentTerms` is not changed by payment execution events.
* **Customer behavior (early payment, late payment, full settlement) operates on financial execution and cannot alter `PaymentTerms`.**
* **The exact lifecycle trigger for installment-schedule creation is NOT decided by this baseline** — it is **deferred to the installment-schedule integration task** (see §N.3).

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

Both Entitlement Benefit types share eligibility semantics but are **separate persistence aggregates** because their fulfillment actions diverge (subscription extension vs. physical handover). **We do NOT introduce a third aggregate named `CommercialBenefit`** — the business concept is unified; the technical persistence is split because the fulfillment lifecycles genuinely differ.

## 4. Final FreeMonths Model

Four independent counters / states:

| Concept | Storage |
|---|---|
| Commercial Entitlement | `FreeMonthsBenefit.EntitlementMonths` (row, immutable) |
| Eligibility | `FreeMonthsBenefit.EligibilityStatus` (reversible) |
| Grant | `FreeMonthsBenefit.FulfillmentStatus = Granted` + `GrantedAtUtc` (one-way) |
| Application | `TenantPlan.AppliedFreeMonthsBenefitIds[]` + `TenantPlan.BonusMonths` (one-way, idempotent) |

`EntitlementMonths = 1` means **"the contract grants one free month."** It does NOT mean `Granted = true` or `Applied = true`. The same `FreeMonthsBenefit` must never extend the subscription twice.

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
* `Installment` schedule creation is triggered only when `Contract.PaymentTerms == Installments`. **The exact lifecycle trigger (activation, first invoice, first payment, or other) is NOT decided by this baseline — it is deferred to the installment-schedule integration task** (see §N).
* `Contract.PaymentTerms == FullUpfront` rejects any attempt to create an installment schedule for that contract.
* `Installment` is the **financial execution**. It does NOT determine `Contract.PaymentTerms`.

## 8. Final Refund Implications

* `RefundCalculationService.Calculate` signature gains `IReadOnlyList<FreeMonthsBenefit>`; the engine loops only over `ContractBenefit` rows for `BenefitContributions`.
* `ContractBenefit.IsGranted` check is replaced with `FulfillmentStatus ∈ { Granted, Delivered }`. Migration backfills `IsGranted = true ⇒ FulfillmentStatus = Delivered`, preserving all existing security-test expectations.
* `FreeMonthsBenefit` is never recoverable as cash.
* `DiscountedMonths` (Pricing Benefit) is unchanged; the discount is implicitly returned via `ContractedAmount` math.

## 9. Final Domain Invariants

See §L (invariants 21–35). Highlights:

* Invariant 21: `Contract.PaymentTerms` equals the Offer snapshot value (set explicitly by the operator).
* Invariant 22: `Contract.PaymentTerms` is immutable after Contract acceptance.
* Invariant 23: directional only — `Installments ⇒ schedule`; never the reverse. Payment execution cannot rewrite PaymentTerms.
* Invariant 25: `PaymentTerms` is NOT inferred from `PromotionType` or from installment rows.
* Invariant 28: `FulfillmentStatus` is monotone.
* Invariant 30: a `Delivered` PhysicalGift never reverts.
* Invariant 31: `FreeMonthsBenefit.Id` appears at most once in `TenantPlan.AppliedFreeMonthsBenefitIds[]`.
* Invariant 32: bonus months never increase `ContractedAmount`.
* Invariant 35: no historical commercial facts are invented by migrations.

## 10. Final Implementation Sequence for Task A Onward

T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → T9 → T10 → T11 → T12 (see §M for details). Tasks T1–T8 are the Task A critical path; T9–T12 are follow-up.

## 11. Final Principles (Unambiguous)

```text
Offer defines commercial terms.
Contract snapshots commercial terms.
PaymentTerms is a commercial term.
Installment is financial execution.
Payment execution cannot rewrite PaymentTerms.
PromotionType does not inherently determine PaymentTerms.
Benefit eligibility is explicit and per-benefit.
Bonus and Gift are one Commercial Benefit concept.
FreeMonths entitlement ≠ grant ≠ application.
Eligibility is reversible.
Fulfillment is historical/monotonic.
FreeMonths application is idempotent.
Bonus months never increase billable contractual amount.
No historical commercial facts are invented.
```

---

## N. Genuinely Unresolved Business Decisions

These cannot be determined from the repository and require explicit business-owner input before or during Task A. They are NOT settled by assumptions.

1. **Exact mechanism by which the platform operator selects `PaymentTerms` when creating/calculating an Offer.** Is `PaymentTerms`:
   * a parameter on `CalculateAndPersistOfferCommand`,
   * a field on the `Promotion` catalog entity (so each promotion declares its own payment mode),
   * a field on the `Plan` catalog entity (so each plan declares its preferred mode),
   * or a combination?
   This affects both the API surface and the migration of catalog data. **No assumption is made here.**

2. **Exact payment schedule structure for `Installments`.** Today `CreateInstallmentScheduleCommand` accepts arbitrary `InstallmentScheduleItem` rows (custom amounts, custom due dates, custom covered periods) and supports equal-count schedules implicitly. The open question is: should the platform enforce any canonical structure (e.g. equal amounts, contiguous months aligned with billing cycle), or should it continue to accept arbitrary operator-supplied schedules? **The repository already supports arbitrary schedules; the question is whether any policy should constrain them.**

3. **Exact timing of installment schedule creation — DEFERRED to the installment-schedule integration task.** When is `CreateInstallmentScheduleCommand` invoked for a `Contract.PaymentTerms == Installments` contract?
   * at Contract activation,
   * at first invoice issuance,
   * at first customer payment,
   * or at another lifecycle point?
   This affects invoice / installment integration tests and the order of side-effects in `CreateSubscriptionFromContractCommand`. **The design baseline does NOT decide this.** It is deferred to the installment-schedule integration task. **No assumption is made here.**

4. **Catalog-level expression of bonus rules.** Should `Promotion.BonusRules` (or equivalent) exist as a first-class catalog entity, so the bonus rule is configured once at the promotion level and inherited by all `FreeMonthsBenefit` rows created from that promotion? Or should each `FreeMonthsBenefit` row carry its own `EligibilityRule` independently? The `Promotion` catalog does not currently model bonus rules at all; this decision shapes whether T9 introduces `Promotion.BonusRules` or leaves rules per-row. **No assumption is made here.**

5. **Treatment of `Contract.BonusMonths` in the greenfield migration.** Since the migration does not invent historical rows, what does `Contract.BonusMonths > 0` mean for any pre-existing contract after T3 ships? Three options:
   * leave the scalar untouched (current baseline — backward-compatible, no meaning change);
   * backfill one `FreeMonthsBenefit` row per contract with `BonusMonths > 0` (an explicit data-fix step, performed once, by the operator);
   * leave the scalar and require each operator to create `FreeMonthsBenefit` rows manually for existing contracts (no data-fix step).
   The current baseline recommends option 1 for Task A and option 2 as a one-time data-fix whenever the operator decides to migrate. **No automatic migration is performed.**

---

## Stop Condition Confirmed

This document is the **binding design baseline for Task A implementation**. It is documentation only.

* Files changed in this commit: `docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md` (rewritten).
* No production code, migrations, entities, controllers, or tests were modified.
* The two final corrections from the architecture review are applied: no `PromotionType → PaymentTerms` inference; no invented historical commercial facts.
* The four-level FreeMonths model and Commercial Benefit unification are preserved.
* The final five principles (offer defines commercial terms; payment execution cannot rewrite PaymentTerms; benefit eligibility is per-benefit; bonus entitlement ≠ grant ≠ application; no invented historical facts) are stated unambiguously in §11.
* Five genuinely unresolved business decisions are listed in §N.
