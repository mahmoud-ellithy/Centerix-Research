# CENTERIX — COMMERCIAL BENEFIT DESIGN VALIDATION & REVISED AUDIT

**Type:** Re-validate and revise of `docs/DEEP-FINANCIAL-DOMAIN-AUDIT-REPORT.md` (`c92e170`).
**Scope:** Validate the proposed `CommercialBenefit` / `PaymentTerms` / Benefit-Lifecycle model against the actual repository state, then specify the smallest safe corrections required to satisfy the prompt's business rules.
**Posture:** read-only audit. **No code, migrations, or entities were modified.**
**Repository state at audit time:** `mahmoud-ellithy/Centerix-Research`, HEAD `6c6ed34` (audit commit `c92e170` already merged).

---

## A. Executive Summary — what changed since the previous audit

The previous audit (`DEEP-FINANCIAL-DOMAIN-AUDIT-REPORT.md`, `c92e170`) already reached four well-supported conclusions that this re-audit confirms and refines:

1. **The `Installment` aggregate is the authoritative payment obligation/entitlement model.** Confirmed by direct read of [Installment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L21-L345) and [CreateInstallmentScheduleCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L25-L270). No additional `PaymentObligation` entity is needed.
2. **The full Eligibility / Refund / Invoice / BillingCycle machinery already exists** and uses the `payments >= contracted amount` plus `no-overdue-installment` gate at [BenefitEligibilityService.cs#L28-L61](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L28-L61).
3. **A `PaymentTerms` field does not exist anywhere in the codebase.** Re-verified by a full-tree grep for `PaymentTerms|PaymentMode|IsFullyPaidAtAcceptance|FullUpfront|FullPaymentMode` returning zero matches across `src/`.
4. **Referral qualification/reward handlers are still missing.** Confirmed unchanged.

What this re-audit adds or corrects:

5. **Bonus Months are NOT a `ContractBenefit`. They live as a scalar `Contract.BonusMonths` (and `TenantPlan.BonusMonths`).** The previous audit's proposed `BenefitBonusPolicy` mapping onto `ContractBenefit` would conflate two different lifecycle models. A new `FreeMonthsBenefit` value-object on `Contract` is the correct fix (see §Q1 / §C / §D).
6. **The benefit lifecycle must be split into two parallel state machines**: current *eligibility* (`NotEligible / Eligible`) and historical *entitlement* (`Earned / Granted / Delivered / Consumed / Withdrawn`). The current single state machine `NotEligible → Eligible → Delivered` collapses them and is the root cause of every "delivered-then-overdue" risk called out in the prompt §9. Detail: §G.
7. **`OfferBenefit` rows are never populated in production today.** `PromotionCalculationService` produces only price/discount values ([PromotionCalculationService.cs#L17-L146](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs#L17-L146)), and `CalculateAndPersistOfferCommand` ([CalculateAndPersistOfferCommand.cs#L75-L132](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Promotions/Commands/CalculateAndPersistOfferCommand.cs#L75-L132)) snapshots Plan/PricingTiers/Features but **never** populates `Offer.Benefits`. Every benefit in the test suite is constructed directly via `Contract.AddBenefit(...)`, bypassing the offer chain. So there are no historical contracts whose benefits depend on the current `Offer` schema. Detail: §B.5.
8. **The "early settlement → bonus" rule currently fails differently from what the previous audit described.** The bonus is not calculated dynamically from payment behavior — it is *only* a contract-time scalar (`Contract.BonusMonths`). Eligibility for the *gift* uses the global `payments >= ContractedAmount` rule — which does NOT distinguish upfront from installment. Detail: §8.

The minimum-safe correction is **NOT** a new aggregate. It is:

* A new value-object `PaymentTerms` (with two arms: `FullUpfront` and `Installments`) snapshotted on `Offer` and `Contract`.
* Per-`ContractBenefit` eligibility expression, evaluation plugin-style, instead of a single global rule in `BenefitEligibilityService`.
* A new `FreeMonthsBenefit` row type on `Contract` (not a sub-type of `ContractBenefit`) sharing the same eligibility-expression evaluation.
* A lifecycle enum that separates *eligibility status* from *delivery status*, so a delivered gift never "un-delivers" when the customer later goes overdue.
* A migration backfilling `Contract.PaymentTerms` from the existence of installment rows (zero installments ⇒ `FullUpfront`; ≥1 ⇒ `Installments`). See §15 for the boundary.

---

## B. Current Domain Reality

### B.1 Contract Benefit (`ContractBenefit`) — the existing single benefit model

File: [ContractBenefit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/ContractBenefit.cs#L29-L222).

| Field / method | Significance |
|---|---|
| `BenefitType : ContractBenefitType` | Enum: `PhysicalGift = 0`, `Service = 1`, `FinancialCredit = 2`, `ExtendedTerm = 3`, `Other = 4` ([ContractBenefitType.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Enums/ContractBenefitType.cs#L1-L13)). |
| `ContractualValue`, `CurrencyCode`, `Name`, `Description` | Immutable snapshot at contract time; locked once `IsGranted = true`. |
| `EligibilityStatus : BenefitEligibilityStatus` | `NotEligible = 0 / Eligible = 1 / Delivered = 2` ([BenefitEligibilityStatus.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Enums/BenefitEligibilityStatus.cs#L1-L27)). |
| `EligibleAtUtc`, `IsGranted`, `GrantedAtUtc`, `DeliveredBy` | Audit fields. |
| `CalculateConsumedValue` / `CalculateRemainingValue` | Day-based depreciation used by the refund calculation ([RefundCalculationService.cs#L87-L106](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs#L87-L106)). |
| `AddBenefit` on Contract | Enforces `Σ ContractualValue ≤ 3 × ContractualMonthlyValue` ([Contract.cs#L413-L429](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L413-L429)). |

**Only `PhysicalGift` may be delivered** ([MarkBenefitDeliveredCommand.cs#L119-L120](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Contracts/Commands/MarkBenefitDeliveredCommand.cs#L119-L120)). All other enum members exist but have no semantics today.

### B.2 Bonus Months — modeled as scalars, not as a benefit

`BonusMonths` exists as `Contract.BonusMonths` ([Contract.cs#L101-L105](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L101-L105)) and is snapshotted into `TenantPlan.BonusMonths` ([TenantPlan.cs#L70](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs#L70)) by `SubscriptionFactory.CreateFromSnapshotAsync` ([SubscriptionFactory.cs#L61-L110](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Subscriptions/SubscriptionFactory.cs#L61-L110)).

* `PromotionCalculationService` produces `BonusMonths` only via the `Plan.BonusMonths` snapshot at calculation time — it is NEVER a function of payment behavior.
* `CreateContractFromOfferCommand` ([CreateContractFromOfferCommand.cs#L92-L119](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Promotions/Commands/CreateContractFromOfferCommand.cs#L92-L119)) populates `Contract.BonusMonths` from `offer.BonusMonths`, snapshotted from `plan.BonusMonths` at the time the Offer was calculated.

So today the system has only **two** ways the "bonus" exists:

* Static `Plan.BonusMonths` provided at calculation time ⇒ snapshotted to `Offer` ⇒ snapshotted to `Contract` ⇒ snapshotted to `TenantPlan`.
* `ChargedMonths` on `Offer`/`Contract` ([Offer.cs#L107-L109](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/Offer.cs#L107-L109)) for `PayForXMonths` promotions.

There is no benefit row representing a bonus month, no eligibility check beyond the contract-time snapshot, and no way to "withhold" a bonus because the customer later pays by installments.

### B.3 Eligibility service — one global rule, not per-benefit

File: [BenefitEligibilityService.cs#L28-L61](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L28-L61):

```text
if (contract.Status != Active)            return false;
if (contractedAmount > 0
 && completedPaymentTotal < contractedAmount) return false;
if (hasOverdueInstallment)                return false;
return true;
```

This is invoked from a single handler, `CheckBenefitEligibilityCommand` ([CheckBenefitEligibilityCommand.cs#L67-L78](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Contracts/Commands/CheckBenefitEligibilityCommand.cs#L67-L78)). There is no per-benefit override. The handler computes *one* `completedPaymentTotal` and *one* `hasOverdueInstallment` flag for the whole contract and applies them to every benefit of that contract.

### B.4 Installments — already authoritatively modeled

File: [Installment.cs#L21-L345](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L21-L345) and [CreateInstallmentScheduleCommand.cs#L25-L270](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L25-L270).

Every property the prompt §13 requires is present:

* `Amount`, `DueDateUtc`, `CoveredPeriodStartUtc`, `CoveredPeriodEndUtc`.
* Server-derived `SettledAmount`, `RemainingAmount`, `Status` (`Pending / PartiallyPaid / Paid / Overdue / Cancelled`) and concurrency token `RowVersion`.
* `SubscriptionId`, `ContractId`, `TenantId`, `InvoiceId?`.
* Validation: total-equals-contracted-amount, no-gap (contiguity), no-overlap, within contract period, currency match, tenant scope.

The factory is the only authoritative creation path and uses `Serializable` isolation, deadlock retries, and `ChangeTracker.Clear()` hygiene. **No new `PaymentObligation` entity is required.**

### B.5 Offers — promotion service carries price only; benefits are NOT populated

* `PromotionCalculationService.Calculate(...)` ([PromotionCalculationService.cs#L17-L146](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs#L17-L146)) returns `CalculatedOffer` containing only `BaseAmount, DiscountAmount, FinalAmount, PromotionType, ChargedMonths, PromotionId, PromotionName, PromotionCode, DiscountPercentage, MonthlyListPrice, CurrencyCode, CalculatedAtUtc`. There is no benefit entry in this record.
* `CalculateAndPersistOfferCommand` ([CalculateAndPersistOfferCommand.cs#L75-L132](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Promotions/Commands/CalculateAndPersistOfferCommand.cs#L75-L132)) then snapshots pricing tiers, features, and entitlements, but **never** adds `OfferBenefit` rows.
* All production contract creation flows go through `Offer → Contract` ([CreateContractFromOfferCommand.cs#L174-L196](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Promotions/Commands/CreateContractFromOfferCommand.cs#L174-L196)), so **no benefit currently flows from the promotion pipeline into contracts**. Every benefit test sets up `Contract.AddBenefit(...)` directly in memory.

### B.6 Promotion type taxonomy

`PromotionType` enum (referenced at [PromotionCalculationService.cs#L87-L124](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs#L87-L124)): `PercentageDiscount / FixedAmountDiscount / PayForXMonths / PromotionalPrice`.

Today:

* `PayForXMonths` (`Promotion.ChargedMonths`) collapses its discount into `ContractedAmount` and `Contract.ChargedMonths` — the customer pays *less* over the same period and gets no extra days.
* `PromotionalPrice` lowers the total but does NOT extend the term — used for "12 months for the price of 10".
* `Plan.BonusMonths` — the ONLY source of true "free months" — is the one the prompt §Examples A and C are actually about.

### B.7 Existing migrations & greenfield status

Migration history ends at `20260926221850_Task21_InvoiceMoneyPrecisionAlignment`. There is **no seed data file** (`Glob Seed*.cs` returned empty), no `DataSeeder.cs`, and no production-data fixtures. Tests construct the domain in-memory. The repo is therefore effectively **greenfield on benefits** — no historical bonus-months or gift rows exist that would need to be preserved through the `ContractBenefit` schema change.

### B.8 Test coverage of benefits today

All benefit tests live in `tests/Centerix.SecurityTests/` and include `OfferToContractFlowTests`, `ContractBenefitsGiftsHardeningTests`, `Phase11PlanChangeTests`, `Task18CommercialIntegrityTests`, `Task18FinalCommercialHardeningTests`, `CompleteOfferSnapshotIntegrityTests`. They consistently construct `ContractBenefit` directly via `Contract.AddBenefit(...)`. **No test exists for "installments + gift eligibility" or "installments + bonus months withheld"**.

---

## C. Corrected Commercial Model

```text
┌────────────────────────────────┐
│ Plan        (catalog)          │
│   - BonusMonths                │
│   - PricingTiers               │
│   - PlanFeatures               │
└──────────────┬─────────────────┘
               │  (read at calculation time only)
               ▼
┌────────────────────────────────┐
│ Offer        (immutable snapshot) │
│   - PlanId                    │
│   - DurationMonths            │
│   - BaseAmount / FinalAmount  │
│   - PromotionType             │
│   - ChargedMonths (PayForXMonths) │
│   - PaymentTerms ◄── NEW FIELD│
│   - BonusMonths (snapshot)    │
│   - OfferBenefits[]           │
│     └ BenefitType=PhysicalGift   │
│       + BenefitEligibilityRule  ◄── NEW FIELD per benefit│
└──────────────┬─────────────────┘
               │  (only path to Contract)
               ▼
┌────────────────────────────────┐
│ Contract        (immutable snapshot) │
│   - ... existing commercial fields ...    │
│   - PaymentTerms ◄── NEW FIELD, snapshotted from Offer │
│   - BonusMonths (scalar; legacy compatibility) │
│   - FreeMonthsBenefits[] ◄── NEW: explicit typed bonus rows │
│       └ BenefitType=FreeMonths              │
│         + EligibilityRule (referenced)     │
│   - Benefits[] (ContractBenefit)            │
│       └ BenefitType ∈ {PhysicalGift, …}    │
│         + EligibilityRule ◄── NEW FIELD    │
└──────────────┬─────────────────┘
               │  (Contract.GetSubscriptionSnapshot)
               ▼
┌────────────────────────────────┐
│ TenantPlan / Subscription      │
│   - DurationMonths, BonusMonths (effective entitlement)│
│   - EffectiveEndsAtUtc = Base + BonusMonths (calendar math)│
│   - BonusServicePeriod (per-FreeMonthsBenefit snapshot) ◄── NEW│
└──────────────┬─────────────────┘
               │
               ▼
┌────────────────────────────────┐
│ BillingCycle / Invoice / Payment / Installment / Refund │
│ (no conceptual change; reference new PaymentTerms field)│
└────────────────────────────────┘
```

The **single explicit parent concept `CommercialBenefit` IS `ContractBenefit` + a sibling `FreeMonthsBenefit`**, both carrying an `EligibilityRule`. We do NOT introduce a 3rd aggregate `CommercialBenefit` because:

1. The existing `ContractBenefit` already covers PhysicalGift and the unused `Service / FinancialCredit / ExtendedTerm / Other` slots.
2. `FreeMonthsBenefit` differs semantically: it is a *time credit* that extends the subscription period, not a deliverable object. Conflating them under one aggregate would force a polymorphic discriminator with conditional behaviors everywhere (`if (Benefit is FreeMonthsBenefit) ... else if (Benefit is PhysicalGift) ...`).

Hence the recommended shape is **two parallel aggregates** that share an `EligibilityRule` evaluation pipeline.

---

## D. Benefit Types — minimum required taxonomy

| Domain concept | Existing today? | Storage | Recommended action |
|---|---|---|---|
| **FreeMonths** (a.k.a. Bonus Months) | Partial. `Contract.BonusMonths` scalar; `Offer.BonusMonths`; `TenantPlan.BonusMonths`. | Scalar on Contract/Offer/TenantPlan; no row in `ContractBenefits`. | **KEEP** the scalar for backward compat AND introduce `Contract.FreeMonthsBenefits` (rows) representing each explicit **bonus grant** with its own eligibility rule. The `BonusMonths` scalar becomes `Σ FreeMonthsBenefits.GrantedMonths` invariant. New `PromotionType` "BonusMonths" added that creates one `FreeMonthsBenefit` row. |
| **DiscountedMonths** (Example C — 12 months for the price of 10/11) | Yes, indirectly. `PromotionType.PromotionalPrice` reduces `ContractedAmount` without extending `DurationMonths` ([PromotionCalculationService.cs#L112-L120](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs#L112-L120)). | `Offer.FinalAmount`/`Contract.ContractedAmount`. | **KEEP as-is**. This is NOT a benefit row — it is a commercial discount at the Offer level. **Do not** promote it to a `ContractBenefit`. |
| **PhysicalGift** (Example B — barcode printer, PC) | Yes. `ContractBenefit.BenefitType = PhysicalGift`, full lifecycle: NotEligible/Eligible/Delivered. | `ContractBenefits` table. | **KEEP**, attach per-benefit `EligibilityRule`. |
| Future benefit types (e.g. `Service`, `FinancialCredit`, `ExtendedTerm`, `Other`) | Enum slot reserved but unused. | `ContractBenefits` table. | **KEEP** as enum slots; do not implement delivery semantics until a benefit of that type is added. |

**Conclusion for Q2:** three benefit categories exist — FreeMonths, DiscountedMonths (NOT a row), PhysicalGift. The prompt's `FreeMonths / DiscountedMonths / PhysicalGift` taxonomy does **not** map cleanly because `DiscountedMonths` is not a benefit row in our current model. We therefore propose distinguishing *Commercial Discounts* (price-side, on Offer/Contract) from *Commercial Benefits* (rows on Contract).

---

## E. Payment Terms — where they belong

**Decision:** Introduce a `PaymentTerms` value-object. Place it on **Offer** AND snapshotted onto **Contract**. Do NOT place it on Subscription, Installment, or Payment.

Why:

| Option | Evidence | Verdict |
|---|---|---|
| On `Offer` (and snapshotted on `Contract`) | Offer is the authoritative commercial agreement; it already carries `DurationMonths / ChargedMonths / PromotionType` — adding `PaymentTerms` is natural and matches the snapshot principle. **Every existing snapshot path is Offer → Contract**. | **Chosen.** |
| On `TenantPlan` (subscription) | Subscription is *operational execution* of a contract; commercial terms should not be rediscovered from subscription state. TenantPlan already inherits commercial terms from Contract via snapshot. | Reject. |
| On `Installment` | Installments are *consequences* of payment terms, not the source. Moving them onto Installment would force every aggregate to inspect installments to learn whether the customer paid upfront. | Reject. |
| On `Payment` | Payment records are immutable financial events. They cannot retroactively change payment terms. | Reject. |
| Implicit-only ("does a contract have any installment rows?") | Inferable but non-auditable: an offline/manual customer with no installment schedule but a deferred Cash payment looks identical to "full upfront". | Reject. |

**Implementation shape** (no code changes here — design only):

```text
enum PaymentTerms { FullUpfront = 0, Installments = 1 }
```

* Offer exposes `PaymentTerms` set at calculation time. By default: `FullUpfront` if `Offer.BonusMonths > 0` OR `PromotionType == PayForXMonths OR PromotionalPrice`; else `Installments`.
* Contract snapshots `Offer.PaymentTerms` at contract creation; `Contract.ValidateSnapshotCompleteness` enforces equality with Offer.
* Migration: every existing contract row gets `PaymentTerms = Installments` if there is at least one Installment, otherwise `FullUpfront` (see §15 for the boundary).

---

## F. Eligibility Model

Today the entire evaluation collapses to:

```text
CanBecomeEligible = (Contract.Active)
                 && (completedPaymentTotal >= contractedAmount)
                 && (!hasOverdueInstallment)
```

This is correct **only** for the single use-case where the benefit is "physical gift given when fully paid". The prompt calls out two distinct benefit types with different rules. Recommendation:

* Introduce a per-benefit (per-row) `EligibilityRule` value-object stored on `ContractBenefit` AND on the new `FreeMonthsBenefit` row.
* The rule is a *closed* algebraic expression — five primitives max — evaluated by `BenefitEligibilityEvaluator` against the contract snapshot, the subscribed financial state, and one clock:

```text
BenefitEligibilityRule
  ::= AllOf(EligibilityRule[])        // AND
    | AnyOf(EligibilityRule[])        // OR
    | ContractActive                  // Contract.Status == Active
    | PaymentTermsEq(PaymentTerms)    // Contract.PaymentTerms == X
    | PaymentMethodEq(string)         // composed from allocations: dominant method
    | CompletedByUtc(DateTime)        // ∃ Completed Payment with CompletedAtUtc <= X
    | NoOverdueInstallment            // ∄ Installment in Overdue/PartiallyOverdue
    | AmountPaidAtLeast(decimal)      // Σ valid active allocations ≥ X
    | DaysFromContractStartGte(int)   // AsOfUtc - Contract.EffectiveAtUtc >= X days
    | DurationMonthsGte(int)          // Contract.DurationMonths >= X
```

* `ContractBenefit` and the new `FreeMonthsBenefit` both carry a rule. The legacy `PhysicalGift` rows would default to:

```text
AllOf([ContractActive, AmountPaidAtLeast(ContractedAmount), NoOverdueInstallment])
```

which is **bit-equivalent** to today's behavior. Existing tests remain green.

* `CheckBenefitEligibilityCommand` is refactored to call `BenefitEligibilityEvaluator.Evaluate(rule, contract, …)` and feed the result into `ContractBenefit.MarkEligible` / `FreeMonthsBenefit.MarkGranted` according to the lifecycle below.

This is a *plugin-style* evaluator. New benefit types can declare their own rules without code changes anywhere else (e.g. "annual contract + cash payment within promotional period ⇒ gift eligible").

---

## G. Benefit Lifecycle — split into two parallel state machines

Today the single state machine ([BenefitEligibilityStatus.cs#L17-L27](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Enums/BenefitEligibilityStatus.cs#L17-L27)) collapses *eligibility* (can the benefit be delivered / granted?) with *entitlement* (has it been?). The prompt §9 shows exactly why this is unsafe: a delivered PhysicalGift under a later overdue installment must not appear "un-earned".

**Recommended model: two parallel state machines on `ContractBenefit` (and parallel on `FreeMonthsBenefit`):**

```text
EligibilityStatus      DeliveryStatus
───────────────        ───────────────
NotEligible    ─┐      Pending ─┐
                ├────►           ├──► Granted  ──► Delivered ──► (terminal)
Eligible  ──────┘      Declined ┘    │
                                     └──► Withdrawn (only if rule re-fails after Granted but
                                                  before Delivered; refund applies)
```

* `EligibilityStatus` is *re-derivable* from the rule; it can move `Eligible → NotEligible` if conditions are later violated. Reversible.
* `DeliveryStatus` is a *historical ledger* — `Granted` is set exactly once and never re-evaluated. `Delivered` is set exactly once. Refund logic never queries `EligibilityStatus` for granted/delivered benefits; only `DeliveryStatus`.
* `Eligible → NotEligible` is VALID — only `Granted/Delivered` are irreversible.
* `NotEligible → Delivered` is INVALID (must pass through `Granted`).
* This protects the existing test invariant `Delivered` ⇒ `IsGranted = true` ([ContractBenefit.cs#L217-L221](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/ContractBenefit.cs#L217-L221)) and the refund rule "only `IsGranted` benefits are recoverable" ([RefundCalculationService.cs#L72-L74](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs#L72-L74)).

For `FreeMonthsBenefit` the lifecycle is similar but `Delivered` becomes `AppliedToSubscription` (the subscription `EffectiveEndsAtUtc` was extended by N months and `BonusMonths` counter incremented).

---

## H. Installment Model — sufficient, do not replace

Confirmed by direct read. Every property, validation, and concurrency safety required by the prompt §13 is already in [Installment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L21-L345) and [CreateInstallmentScheduleCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L25-L270).

The only schema gap surfaced by the new requirements is the **lack of `PaymentTerms` on `Contract`**. Once that exists, the relationship `Contract.PaymentTerms == Installments ⇒ Contract.Installments.Count > 0` becomes an enforced invariant during `ValidateSnapshotCompleteness` — see §15.

---

## I. Refund Interaction — preserved, plus a small extension

[RefundCalculationService.cs#L47-L199](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs#L47-L199) already:

* Uses pricing tiers correctly for the elapsed-months calculation.
* Excludes non-granted benefits from the recoverable value.
* Honors the existing invariant `ContractedAmount = GrossAmount - DiscountAmount`.
* Subtracts already-converted SubscriptionChange credit (Task 18.4.2).
* Prevents cross-contract payment contamination (`Invoice.ContractId` filter).

**Bonus Months (FreeMonthsBenefit):** refund must NOT refund a bonus as cash — correct. The existing engine already handles this implicitly because `FreeMonthsBenefit` never enters `BenefitContributions`. If we model it as a separate row type, we explicitly exclude it from the benefit loop in `RefundCalculationService`.

**PhysicalGift:** existing day-based consumption handles delivery-then-cancel correctly *as long as* `IsGranted` stays true after grant ([RefundCalculationService.cs#L72-L74](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundCalculationService.cs#L72-L74)). The current single-state-machine model would let a re-evaluation flip `Delivered → NotEligible` — which the proposed split in §G forecloses.

**DiscountedMonths:** no refund logic change needed; it is a price-side adjustment already reflected in `ContractedAmount`. Refund correctly refunds the surplus of paid over (used + benefits), which works out to the discount being implicitly returned.

---

## J. Required Changes

### Required (blocking the prompt's scenarios from working correctly)

1. **`enum PaymentTerms` + `Offer.PaymentTerms` + `Contract.PaymentTerms` + migration backfilling existing rows.**
2. **`ContractBenefit.EligibilityRule` (typed) + `FreeMonthsBenefit` row + `Contract.FreeMonthsBenefits[]`.**
3. **`BenefitEligibilityEvaluator` domain service** that replaces the global rule in [BenefitEligibilityService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs) and is invoked from `CheckBenefitEligibilityCommand`.
4. **Split lifecycle**: add `DeliveryStatus` enum / fields on `ContractBenefit` and on the new `FreeMonthsBenefit`. Keep `EligibilityStatus` for the reversible rule evaluation.
5. **PlanEligibilityRule defaults**: assign each existing benefit row a default rule identical to today's behavior, so all existing security tests stay green without change.

### Recommended (not blocking the prompt; improves auditability)

6. **Promotion pipeline:** make `CalculateAndPersistOfferCommand` able to populate `OfferBenefits` from a trusted source (e.g. a future `Promotion.Benefits` collection), so the Offer→Contract chain becomes the authoritative benefit-creation path instead of in-test direct calls.
7. **PromotionType `BonusMonths`:** add a fifth promotion type whose result is an `OfferBenefits` row of category `FreeMonths` carrying the rule "PaymentTerms == FullUpfront". This makes the example A scenario expressible end-to-end.
8. **Audit event** for `FreeMonthsBenefit.AppliedToSubscription` to record the actual `StartsAtUtc → EffectiveEndsAtUtc` extension as a frozen historical record separate from `TenantPlan.BonusMonths` counter changes.
9. **Test coverage** for the four §8 scenarios end-to-end.

### Not Required

* Any change to `Installment` schema, validation, or lifecycle.
* Any change to `RefundCalculationService` beyond (a) explicitly excluding `FreeMonthsBenefit` from the benefit loop, and (b) reading `ContractBenefit.DeliveryStatus` instead of `EligibilityStatus == Delivered`.
* Any change to `Invoice`, `Payment`, `PaymentAllocation`, `PaymentReceipt`, `BillingCycle`, `TenantCredit`, `Promotion`, `Plan`, `SubscriptionPolicy`.
* Any change to tenancy, audit logging, or concurrency tokens.

---

## K. Implementation Task Breakdown — smallest safe sequence

> Ordering criterion: each task depends on nothing from later tasks and is independently revertible.

**T1. Domain value-object: `PaymentTerms`.**
* Add enum + `Offer.PaymentTerms`, `Contract.PaymentTerms`. Migration to add the two nullable columns.
* Backfill migration sets existing rows per §15 rule.
* No code path behavior change yet. All existing tests still green.
* Files: `Platform/Promotions/Enums/PaymentTerms.cs` (new), `Offer.cs`, `Contract.cs`, `Platform/Subscriptions/Enums/` (none — PaymentTerms is not on Subscription), `Centerix.Infrastructure/Data/Migrations/<TS>_AddPaymentTerms.cs`.

**T2. `OfferBenefit` (already exists) + `ContractBenefit` carry `EligibilityRule`; introduce default rule factory.**
* Add `EligibilityRule` value-object (sealed class hierarchy in `Platform/Contracts/EligibilityRules/`).
* Default-rule builder: existing benefits get `DefaultPhysicalGiftRule`.
* Migration is additive — nullable column with backfill default.
* Files: `Platform/Contracts/EligibilityRules/*.cs` (new), `ContractBenefit.cs`, `OfferBenefit.cs`, migration file.

**T3. `FreeMonthsBenefit` aggregate + `Contract.FreeMonthsBenefits` navigation + `FreeMonthsBenefits` table.**
* Mirrors `ContractBenefit` but with its own lifecycle (`DeliveryStatus = AppliedToSubscription`) and its own contribution-to-TenantPlan semantics.
* Invariant `Contract.BonusMonths == Σ FreeMonthsBenefits.GrantedMonths` is enforced in `Contract.ValidateSnapshotCompleteness`.
* Migration adds the new table and populates one row per existing contract where `BonusMonths > 0` (rule: `DefaultFreeMonthsRule`).
* Files: `Platform/Contracts/FreeMonthsBenefit.cs` (new), `Contract.cs`, migration file.

**T4. `BenefitEligibilityEvaluator` domain service.**
* Pure evaluator over the closure `EligibilityRule`.
* Replaces the `if/else` in `BenefitEligibilityService` — `IBenefitEligibilityService` interface remains so DI is unchanged.
* Default-rule evaluation must be **bit-equivalent** to today's `CanBecomeEligible` for the existing test suite.
* Files: `Infrastructure/Platform/Services/BenefitEligibilityService.cs` (rewrite), `Application/Platform/Contracts/Services/IBenefitEligibilityService.cs` (unchanged).

**T5. `CheckBenefitEligibilityCommand` refactor.**
* Reads the benefit's `EligibilityRule`, calls the evaluator, applies either `ContractBenefit.MarkEligible` or `FreeMonthsBenefit.MarkGranted` according to the rule result. No other behavior change.
* Files: `Application/Platform/Contracts/Commands/CheckBenefitEligibilityCommand.cs`.

**T6. Lifecycle split on `ContractBenefit`: add `DeliveryStatus`.**
* New enum + fields; `EligibilityStatus` is preserved for back-compat.
* `MarkGranted` now sets `DeliveryStatus = Granted` (in addition to current behavior).
* `MarkGranted` is idempotent on `DeliveryStatus` (do not "re-deliver").
* `RefundCalculationService` switches its `benefit.IsGranted` check to `DeliveryStatus in {Granted, Delivered}` (which is exactly today's semantics under the new name).
* Migration adds the new columns and backfills existing rows: if `IsGranted = true` then `DeliveryStatus = Delivered`.
* Files: `BenefitEligibilityStatus.cs` (keep, do not delete), `Platform/Contracts/Enums/DeliveryStatus.cs` (new), `ContractBenefit.cs`, `RefundCalculationService.cs`, migration file.

**T7. PromotionType `BonusMonths` + OfferBenefit population.**
* Extends `PromotionCalculationService` to produce an `OfferBenefits[]` when `PromotionType = BonusMonths` (one row per `BonusQuantity`).
* `CalculateAndPersistOfferCommand` writes those rows.
* Files: `PromotionCalculationService.cs`, `CalculateAndPersistOfferCommand.cs`. No migration needed.

**T8. Refund explicit exclusion of FreeMonthsBenefit.**
* `RefundCalculationService.Calculate` already takes `IReadOnlyList<ContractBenefit>`. Add `IReadOnlyList<FreeMonthsBenefit>` argument and a one-line "ignore them" branch with an audit-event explaining why.
* Files: `RefundCalculationService.cs`, `CalculateRefundCommand.cs` (signature change), `ExecuteRefundCommand.cs`.

**T9. Test coverage for §8 scenarios.**
* Scenario A: 6m + upfront ⇒ bonus applied; installments-with-early-payment ⇒ bonus not applied.
* Scenario B: 12m + cash-within-promo ⇒ printer delivered.
* Scenario C: 12m + discounted ⇒ contract price lowered, no bonus, no gift.
* Scenario D: default rules ⇒ "installments ⇒ all benefits withheld unless benefit rule allows".

---

## Q1 — What exactly is a Commercial Benefit?

* **Commercial Discount** (price-side): `PromotionType.PercentageDiscount / FixedAmountDiscount / PayForXMonths / PromotionalPrice`. Lives on `Offer.PromotionType + DiscountAmount + FinalAmount + ChargedMonths`, snapshotted into `Contract.{PromotionType, DiscountAmount, ContractedAmount, ChargedMonths}`. Not a row.
* **Commercial Benefit** (entitlement-side): a row on `Contract` representing something the customer receives beyond the paid service. Two existing/supported row types:
  * `ContractBenefit` — already covers `PhysicalGift` (active), and reserves slots for `Service / FinancialCredit / ExtendedTerm / Other` (future).
  * `FreeMonthsBenefit` (new) — time credit that extends the subscription's `EffectiveEndsAtUtc`. Counterpart to today's `Contract.BonusMonths` scalar.

The current `ContractBenefit` aggregate is sufficient; it does not need to be renamed to `CommercialBenefit`, but it must be extended with `EligibilityRule` and split lifecycle. We do NOT add a new aggregate named `CommercialBenefit` — that would create two synonyms for the same concept and risk cross-cutting duplication.

## Q2 — Benefit Type

The minimum required set is **two benefit categories** (FreeMonths, PhysicalGift) which together cover all four §1 examples:

| Example | Outcome | Storage |
|---|---|---|
| A — 6-for-5 | Customer gets 1 bonus month. | `FreeMonthsBenefit { GrantedMonths = 1, EligibilityRule = PaymentTermsEq(FullUpfront) }`. |
| B — annual + cash printer | Customer gets a printer. | `ContractBenefit { BenefitType = PhysicalGift, EligibilityRule = AllOf([ContractActive, DurationMonthsGte(12), PaymentMethodEq(Cash), CompletedByUtc(<promo deadline>)]) }`. |
| C — 12-for-10 | Customer pays less; no extra time, no gift. | `Offer.{FinalAmount < BaseAmount}`; no benefit row created. |
| D — installments then early-pay | No bonus (PaymentTerms still `Installments` regardless of payment completion). | Default `FreeMonthsBenefit` rule `PaymentTermsEq(FullUpfront)` rejects; refund never recovers it. |

---

## Migration / default strategy (§15)

* The repository is **greenfield on benefits**: no seed data, no production fixtures, no migration that pre-populates `ContractBenefits` / `OfferBenefits` data.
* `Contract.PaymentTerms` column is added as **nullable**; the migration backfill is deterministic and verifiable:
  * `SELECT ContractId FROM Contracts C WHERE EXISTS (SELECT 1 FROM Installments I WHERE I.ContractId = C.Id)` ⇒ `Installments`.
  * Otherwise ⇒ `FullUpfront`.
  * If the contract has no installments but the tenant paid in fewer than `DurationMonths` calendar months, mark `FullUpfront` and add a "legacy FullUpfront" free-months benefit default where `Contract.BonusMonths > 0` (no inferring eligibility rules for existing contracts — leave them as noop).
* `ContractBenefit.EligibilityRule` column is added nullable; the migration backfill assigns every existing row `DefaultPhysicalGiftRule` so today's behavior is preserved bit-exactly.
* `DeliveryStatus` column is added nullable; existing rows where `IsGranted = true` are set to `Delivered`; the rest to `Pending`. The `RefundCalculationService` reads `DeliveryStatus` going forward, but since the migration aligns the bit semantics, every existing test refund expectation stays valid.

---

## Final Domain Invariants (refined)

In addition to the 20 invariants listed in the prompt §16, this re-audit mandates:

21. `Contract.PaymentTerms` MUST equal the `PaymentTerms` value snapshotted from the accepted Offer.
22. `Contract.PaymentTerms` MUST equal `Installments` iff `Σ Installments (active) > 0` for that contract.
23. `Contract.BonusMonths` MUST equal `Σ FreeMonthsBenefits.GrantedMonths` (the scalar is a denormalization of the rows).
24. Every benefit row (ContractBenefit or FreeMonthsBenefit) MUST carry exactly one `EligibilityRule`.
25. The default eligibility rule for legacy `PhysicalGift` rows MUST be `AllOf([ContractActive, AmountPaidAtLeast(ContractedAmount), NoOverdueInstallment])` — bit-equivalent to today.
26. `DeliveryStatus` transitions are irreversible: `Pending → Granted → Delivered → (terminal)`. `EligibilityStatus` may move freely between `NotEligible` and `Eligible`.
27. A `Delivered` benefit can never transition to `Withdrawn`; cancellation/recovery affects money, not physical possession.

---

## Stop Condition Confirmed

This document does **not** modify any production code, migration, entity, controller, or workflow. It is a design specification only.

* Commit: TBD (audit document only).
* Files changed: `docs/COMMERCIAL-BENEFIT-DESIGN-VALIDATION.md` (new).
* Main findings: see §A and §J.
* Recommended implementation sequence: §K (T1–T9).
* Unresolved business decisions awaiting explicit approval before T1 begins:
  1. **Should `Offer.PaymentTerms` default to `FullUpfront` when `Offer.BonusMonths > 0` (or `PayForXMonths` is applied)?** Recommendation: yes.
  2. **Should we add a fifth `PromotionType = BonusMonths`?** Recommendation: yes (T7).
  3. **Should the new `FreeMonthsBenefit` rows be visible in the existing `Contract.Benefits` collection (with a discriminator) or be a separate `Contract.FreeMonthsBenefits[]` navigation?** Recommendation: **separate navigation** — physical-gift and time-credit lifecycles diverge enough that polymorphism here would be brittle.
  4. **Default `EligibilityRule` for legacy benefits**: confirm we want bit-equivalence with today's `BenefitEligibilityService` (so existing security tests remain green without test rewrites).
