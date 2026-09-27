# CENTERIX — DEEP FINANCIAL DOMAIN AUDIT REPORT

**Audit type:** Evidence-based reconstruction of the commercial / financial domain.
**Hypothesis under test:** "Centerix needs an Installment / Payment Obligation Engine."
**Head:** `6c6ed34` (Task 21 closure correction).
**Closure baseline:** Task 21 = CLOSED (`docs/TASK-21-FINAL-INVOICE-COMMERCIAL-INTEGRITY-CLOSURE.md`).
**Posture:** read-only audit. No code was modified.

---

## 1. Executive Summary

The hypothesis that Centerix still needs an authoritative **Payment Obligation / Installment Schedule engine is REJECTED** as the primary remaining work.

A fully-implemented Payment Obligation model already exists and is enforced end-to-end:

* The `Installment` aggregate at [Installment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L8-L345) is explicitly defined as *"Authoritative payment obligation for a customer. Represents a specific amount due on a specific date, covering a specific service period within a Contract."* It carries a `DueDateUtc` (payment lateness), `CoveredPeriodStartUtc` / `CoveredPeriodEndUtc` (entitlement coverage), `Amount`, `CurrencyCode`, derived `SettledAmount`/`RemainingAmount`, a deterministic `Status` (`Pending / PartiallyPaid / Paid / Overdue / Cancelled`), `RowVersion`, and a unique `(TenantId, ContractId, SequenceNumber)` index.
* `CreateInstallmentScheduleCommand` at [CreateInstallmentScheduleCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L25-L270) is the authoritative factory: it validates contiguity, no-gap, no-overlap, amount-equals-contract, applies `Serializable` isolation, deadlocks retries (`1205`), duplicate-key handling (`2601/2627`), tenant scope, `ChangeTracker.Clear()` hygiene, and stamps the audit log.
* `AllocatePaymentCommand` at [AllocatePaymentCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs#L131-L455) is the only settlement path. It supports partial allocation, optional `InstallmentId` settlement, overpayment-as-`TenantCredit`, UPDLOCK invoice serialization, ledger correctness, idempotency, and triggers `SubscriptionReconciliationService`.
* `SubscriptionReconciliationService` at [SubscriptionReconciliationService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/SubscriptionReconciliationService.cs#L15-L172) deterministically derives `PastDue`/`Suspended`/`Expired` from installment overdue state vs. central `SubscriptionPolicy.GracePeriodDays`, with lazy reconciliation.
* `CheckBenefitEligibilityHandler` at [CheckBenefitEligibilityCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Contracts/Commands/CheckBenefitEligibilityCommand.cs#L20-L80) evaluates payment-vs-contracted-amount AND overdue-installment checks for physical gifts.

What Centerix still lacks is **not** the obligation engine. The actual open problems are:

1. **The `Full Upfront Payment vs Installment Payment` business distinction is not modeled anywhere** — there is no `PaymentTerms`, `PaymentMode`, `IsFullyPaidAtAcceptance`, or equivalent attribute on Offer / Contract / Payment. The new bonus/benefit rule from the prompt is therefore impossible to enforce today and is the most important financial-domain gap.
2. **Referral qualification and reward-application workflow does not exist** — `TenantReferral.Qualify()` and `ApplyReward()` exist on the entity ([TenantReferral.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Referrals/TenantReferral.cs#L79-L108)) and the events are defined, but no command/handler invokes them. Confirmed by [MODULE-INVENTORY-20260903.md#L237](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/docs/MODULE-INVENTORY-20260903.md#L237).
3. **Eligibility for physical gifts uses a global "payments >= contracted amount" gate** ([BenefitEligibilityService.cs#L53-L54](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L53-L54)) — this silently couples benefit delivery to *fully paid* state without modeling *why* the customer paid upfront. That coupling is what the new requirement is exposing as a gap.

The premise "build an Installment Engine" is therefore **wrong in priority order**. The audit instead recommends:

* Introduce a `PaymentTerms` value object on `Contract` (with explicit `FullUpfront` and `Installments` arms).
* Snap-shot the chosen terms on `Offer` and propagate to `Contract`.
* Add a `BenefitBonusPolicy` mapping (FullUpfront → bonus available; Installments → bonus withheld unless explicitly allowed per offer) to `ContractBenefit` / `OfferBenefit`.
* Implement the missing referral qualification/reward handlers.
* Test the 10 scenarios end-to-end (only a subset are covered today).

All other identified gaps are MEDIUM/LOW severity and do not block the new bonus rule.

---

## 2. Current Financial Domain Reality

### 2.1 Aggregate map (evidence)

| Aggregate | File | What it actually represents | Mutability | Tenant scope |
|---|---|---|---|---|
| **Offer** | [Offer.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/Offer.cs#L17-L412) | Persisted immutable snapshot of a calculated commercial offer at one point in time. Carries `FinalAmount`, snapshot of `BonusMonths`, limits, and child snapshots `OfferBenefit`, `OfferFeature`, `OfferPricingTier`. Lifecycle: `Calculated → Accepted → ConvertedToContract` or `Calculated → Expired`. | Mutable only while `Calculated`; immutable after `Accepted`. | `IHasTenantId`. |
| **Contract** | [Contract.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L27-L653) | Authoritative commercial agreement. Carries **immutable** commercial snapshot: `MonthlyListPrice`, `ContractualMonthlyValue`, `GrossAmount`, `DiscountAmount`, `ContractedAmount`, `CurrencyCode`, `BonusMonths`, `ChargedMonths`, plan limits, `PromotionReference/Id/Type`, `EntitlementSnapshotVersion`. Lifecycle: `Draft → PendingApproval → Active → Suspended → Expired/Terminated`. Children: `ContractPricingTier`, `ContractBenefit`, `ContractFeature`, `Subscriptions`. | Mutates status only; commercial fields are private setters never re-written after `Create`. | `IHasTenantId`. |
| **Subscription / TenantPlan** | [TenantPlan.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs#L32-L406) | Lifecycle-bearing entitlement row. Snapshots `SnapshotPrice`, `SnapshotMonthlyCharge`, `SnapshotCurrency`, `DurationMonths`, `BonusMonths`, `StartsAtUtc`, `BaseEndsAtUtc` (= `StartsAtUtc + DurationMonths`), `EffectiveEndsAtUtc` (= `BaseEndsAtUtc + BonusMonths`), plan limits, feature codes. May link to `ContractId`. Lifecycle: `Pending / Active / PastDue / Suspended / Expired / Cancelled`. | Status transitions are domain-controlled via `internal` methods (`MarkPastDue`, `SuspendFromObligation`, `ReactivateFromFinancialRecovery`); commercial fields are immutable. | `IHasTenantId`. |
| **BillingCycle** | [BillingCycle.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/BillingCycles/BillingCycle.cs#L12-L190) | The billable service period tied to a Subscription. Lifecycle: `Draft → Invoiced → Paid` or `Cancelled`. | Status only. Period itself is set at creation and treated as immutable by `IsFullTermFor` identity check. | `IHasTenantId`. |
| **Invoice** | [Invoice.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs#L10-L185) | Server-derived financial demand for one BillingCycle. `Subtotal`, `DiscountAmount`, `TaxAmount`, `TotalAmount`. Tracks `ContractId`, `SubscriptionId`, `BillingCycleId` (required for billing-cycle invoices). Lifecycle: `Draft → Issued → PartiallyPaid → Paid` (or `Cancelled`). | Subtotal/discount/tax/total become **immutable** after `Issue`. | `IHasTenantId`. |
| **Payment** | [Payment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/Payment.cs#L12-L152) | Money actually received. Immutable once `Completed`. `Amount`, `CurrencyCode`, `Method` (channel only, not schedule), `Status`, `CompletedAtUtc`, `IdempotencyKey`. | Once `Completed`, frozen. | `IHasTenantId`. |
| **PaymentAllocation** | [PaymentAllocation.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/PaymentAllocation.cs#L15-L83) | Settlement of a `Payment` against an `Invoice`, optionally to a specific `Installment`. Has `AllocatedAmount`, `Status` (`Active`/`Reversed`), `AllocatedAtUtc`, `RowVersion`. | Active allocations cannot be mutated; reversal is explicit. | `IHasTenantId`. |
| **Installment** | [Installment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L21-L345) | **Authoritative payment obligation** — both payment obligation AND entitlement period. See §4. | Pending can be `Update`-ed; after any allocation, only `Cancel()` is permitted. | `IHasTenantId`. |
| **ContractBenefit** | [ContractBenefit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/ContractBenefit.cs#L29-L222) | Benefit/gift under contract (mostly `PhysicalGift`). Snapshot `ContractualValue`, `CurrencyCode`, `Name`, `BenefitType`. Lifecycle: `NotEligible → Eligible → Delivered`. | Delivery is a one-way state transition; once `Delivered`, snapshot fields are immutable. | Via Contract. |
| **TenantCredit** | [TenantCredit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Credits/TenantCredit.cs#L7-L214) | Customer credit balance. `Amount`, `RemainingAmount` (mutable via `ConsumeAmount`), `SourceType` (`Overpayment/SubscriptionChange/ReferralReward/Promotional/Compensation/Manual`), `Status`. `TransferredPaidAmount` (Task 18.5) records economic-origin lineage. | `RemainingAmount` mutates via `ConsumeAmount`; status transitions are domain-controlled. | `IHasTenantId`. |
| **CreditApplication** | [CreditApplication.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Credits/CreditApplication.cs#L17-L72) | Immutable record of credit applied to an invoice. Carries `IdempotencyKey`. | Immutable. | `IHasTenantId`. |
| **Refund** | [Refund.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/Refund.cs#L23-L252) | Refund transaction. Lifecycle: `Pending → Approved → Processing → Completed`; `Rejected`/`Cancelled`/`Failed`. | Once `Completed`, frozen. | `IHasTenantId`. |
| **RefundAllocation** | [RefundAllocation.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundAllocation.cs#L12-L78) | Per-payment refund source allocation. | Immutable. | `IHasTenantId`. |
| **CustomerLedgerEntry** | [CustomerLedgerEntry.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/CustomerLedgerEntry.cs#L23-L284) | Immutable financial movement (`InvoiceCharge / PaymentSettlement / CreditCreation / CreditUsage / RefundSettlement`). Reconcile balance. | Immutable. | `IHasTenantId`. |
| **PaymentReceipt** | [PaymentReceipt.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/PaymentReceipt.cs#L12-L89) | Issued receipt for a completed payment. | `Cancel()` only; immutable otherwise. | `IHasTenantId`. |
| **TenantReferral** | [TenantReferral.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Referrals/TenantReferral.cs#L8-L122) | Referral record. Lifecycle: `Pending → Qualified → RewardApplied / Revoked`. `LockedUntil` set at qualification. | Mutates only via `Qualify`/`ApplyReward`/`Revoke`. | `IHasTenantId`. |
| **SubscriptionPolicy** | [SubscriptionPolicy.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/SubscriptionPolicy.cs#L11-L43) | Central platform policy singleton (`GlobalAuditableEntity`). Holds `GracePeriodDays`. | `UpdateGracePeriodDays`. | Platform-global. |

### 2.2 What this means

* **The "Installment" entity is not a thin scheduling helper.** It is the authoritative payment obligation, with payment lateness (`DueDateUtc`) and covered entitlement period (`CoveredPeriodStartUtc`/`CoveredPeriodEndUtc`) explicitly separated, server-derived `SettledAmount`/`RemainingAmount`/`Status`, and unique `(TenantId, ContractId, SequenceNumber)` index.
* **Money, allocation, covered period, and entitlement are already separated.** `Payment.Allocations` → `PaymentAllocation` → `Installment` (with `InvoiceId` link) reconstructs the chain `Payment → settled Invoice → corresponding obligation → covered entitlement period`.
* **BillingCycle is correctly the "paid period" only.** `BaseEndsAtUtc` (not `EffectiveEndsAtUtc`) is used as the invoice period end ([CreateInvoiceFromBillingCycleCommand.cs#L372-L406](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs#L372-L406), [RenewSubscriptionOfferCommand.cs#L376-L406](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Commands/RenewSubscriptionOfferCommand.cs#L376-L406), [ChangeSubscriptionPlanCommand.cs#L371-L410](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs#L371-L410)), satisfying the prompt's "BonusMonths are FREE entitlement and must NOT increase billed months" rule.
* **Invoice integrity is hardened.** Filtered unique index `UX_Invoices_BillingCycleId`, FKs with `Restrict`, `decimal(18,2)` storage, math identity check on creation ([Invoice.cs#L99-L102](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs#L99-L102)), and one-invoice-per-cycle enforced by `MarkInvoiced` ([BillingCycle.cs#L164-L171](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/BillingCycles/BillingCycle.cs#L164-L171)).

---

## 3. Source-of-Truth Map

| Business fact | Current source of truth | Authoritative? | Risk |
|---|---|---|---|
| Contract price snapshot | `Contract.GrossAmount`, `Contract.ContractedAmount`, `Contract.DiscountAmount` ([Contract.cs#L78-L84](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L78-L84)) | Yes — created at `Contract.Create` and never mutated. | None observed. |
| Subscription snapshot | `TenantPlan.SnapshotPrice`, `SnapshotMonthlyCharge`, `DurationMonths`, `BonusMonths`, `BaseEndsAtUtc`, `EffectiveEndsAtUtc` ([TenantPlan.cs#L59-L78](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs#L59-L78)) | Yes — built by `Contract.GetSubscriptionSnapshot()` and consumed by `SubscriptionFactory.CreateFromSnapshotAsync` (referenced by [RenewSubscriptionOfferCommand.cs#L359-L369](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Commands/RenewSubscriptionOfferCommand.cs#L359-L369)). | None observed. |
| BillingCycle paid-period | `BillingCycle.PeriodStart`/`PeriodEnd`, `IsFullTermFor` identity check ([BillingCycle.cs#L159-L162](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/BillingCycles/BillingCycle.cs#L159-L162)) | Yes — full-term detection is by **period identity**, not duration equality. | None observed. |
| Invoice amounts | `Invoice.Subtotal`, `DiscountAmount`, `TaxAmount`, `TotalAmount` derived from `Offer`/`Contract` ([CreateInvoiceFromBillingCycleCommand.cs#L74-L109](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs#L74-L109)) | Yes — server-derived, immutable after `Issue`. | None observed. |
| Payment amount | `Payment.Amount`, `CurrencyCode`, `Method` ([Payment.cs#L14-L22](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/Payment.cs#L14-L22)) | Yes — immutable once `Completed`. | None observed. |
| Payment allocation | `PaymentAllocation` records, summed live from active allocations ([Installment.cs#L335-L340](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L335-L340), [Invoice.cs#L148-L153](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs#L148-L153)) | Yes — `SettledAmount` is **derived** from active allocations, not stored separately. | None observed. |
| Installment obligation | `Installment.Amount`, `DueDateUtc`, `CoveredPeriodStartUtc/EndUtc`, `SettledAmount` (derived), `RemainingAmount` (derived), `Status` (derived) | Yes — see §4. | None observed. |
| Covered entitlement period | `Installment.CoveredPeriodStartUtc` / `CoveredPeriodEndUtc` | Yes — authoritative per obligation. The chain `Payment → Allocation → Installment` reconstructs covered period per payment. | None observed. |
| Period paid for | Sum of `Installment` covered periods where the `Subscription` was billed in `BaseEndsAtUtc` window | Derived. Authoritative because `Installment` covered periods are required to be **contiguous and within the contract period** ([CreateInstallmentScheduleCommand.cs#L150-L172](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L150-L172)). | Gap: covered-period is **not persisted on the Invoice** itself. Invoice links to `Installment` indirectly via `BillingCycle.SubscriptionId`/`ContractId`. Reconstructing covered period per paid invoice requires joining `Invoice → BillingCycle → Subscription → Installment`. See §18. |
| Refundable amount | `RefundCalculationService` ([RefundCalculationService.cs#L45-L199](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Refunds/RefundCalculationService.cs#L45-L199)) | Yes — deterministic, side-effect-free. | None observed. |
| Refund completion | `Refund.Status == Completed`, `RefundAllocation` rows | Yes. | None observed. |
| Credit remaining | `TenantCredit.RemainingAmount` | Derived via `ConsumeAmount`. Stored for query speed; can be reconstructed from `Amount - sum(CreditApplication.Amount)`. | The `Apply(Guid invoiceLineId)` overload still exists but only zero-remaining branch is used; functionally dead alongside the more general `ApplyToInvoice` and `ConsumeAmount`. See §29. |
| Customer ledger balance | `CustomerLedgerEntry.RunningBalance` | Denormalized; reconstructable from movements. Ledger invariant is **NOT** enforced by a DB trigger — see §25. | MEDIUM: a bug in handler sequencing could desync RunningBalance from movements. |
| **Payment terms (Full vs Installments)** | **NOT MODELED** | No | CRITICAL — the new business rule has no home. See §8. |
| Bonus eligibility (per benefit) | `ContractBenefit.EligibilityStatus` + `BenefitEligibilityService.CanBecomeEligible` ([BenefitEligibilityService.cs#L28-L61](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L28-L61)) | Partial — see §7. | HIGH — gates bonus on `payments >= contractedAmount`, which is the very behavior the prompt is asking to discriminate. |
| Referral qualification | `TenantReferral.Status` (only mutated via entity methods, **no command exists**) | Missing command. | HIGH — see §17. |

---

## 4. Installment Model Assessment

### What `Installment` actually represents today

**Evidence:** the entity's own XML doc comment states:

> "Authoritative payment obligation for a customer. Represents a specific amount due on a specific date, covering a specific service period within a Contract. Key distinction: DueDateUtc controls payment lateness. CoveredPeriodStartUtc/EndUtc controls the entitlement/period that the installment pays for. They are NOT the same concept. Status is server-derived from: DueDateUtc, Amount, SettledAmount, and current time. Clients cannot manufacture PaidAmount, SettledAmount, RemainingAmount, or Status. Settlement is derived from valid completed PaymentAllocation records assigned to this installment, not from client-supplied values." ([Installment.cs#L8-L19](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L8-L19))

This corresponds to **option (D) — both payment obligation + entitlement period**, plus a non-trivial state model.

### Verifying the actual behavior

| Behavior | Evidence | Verdict |
|---|---|---|
| Due date separated from covered period | `DueDateUtc`, `CoveredPeriodStartUtc`, `CoveredPeriodEndUtc` are independent and validated separately ([Installment.cs#L111-L118](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L111-L118)) | Correct. |
| Server-derived status | `RecalculateStatus(utcNow)` deterministically derives from `SettledAmount` and `DueDateUtc` ([Installment.cs#L308-L325](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L308-L325)) | Correct. |
| `SettledAmount` is **derived** from active allocations | `GetSettledAmount()` sums `_paymentAllocations.Where(IsActive).Sum(AllocatedAmount)` ([Installment.cs#L335-L340](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L335-L340)); `SynchronizeSettledAmount()` is the only writer ([Installment.cs#L270-L273](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L270-L273)) | Correct — single source of truth. |
| Cannot over-settle | `ApplyAllocation` projects `current + new > Amount` and rejects ([Installment.cs#L243-L246](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L243-L246)) | Correct. |
| Cannot mutate after first allocation | `Update` requires `Status == Pending` ([Installment.cs#L178-L179](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs#L178-L179)) | Correct. |
| `CoveredPeriodStartUtc/EndUtc` cannot exceed contract period | `CoveredPeriodExceedsContract` error exists ([InstallmentErrors.cs#L43-L46](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/InstallmentErrors.cs#L43-L46)) and is enforced in `CreateInstallmentScheduleCommand` ([CreateInstallmentScheduleCommand.cs#L161-L162](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L161-L162)) | Correct. |
| Contiguous and gap-free | `GapInSchedule` error ([InstallmentErrors.cs#L56-L58](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/InstallmentErrors.cs#L56-L58)) enforced ([CreateInstallmentScheduleCommand.cs#L168-L171](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L168-L171)) | Correct. |
| `TotalScheduleAmount == Contract.ContractedAmount` | `TotalScheduleAmountMismatch` enforced ([CreateInstallmentScheduleCommand.cs#L176-L178](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L176-L178)) | Correct. |
| Unique sequence per contract | `UX_Installments_TenantContractSequence` ([InstallmentConfiguration.cs#L93-L95](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Configurations/InstallmentConfiguration.cs#L93-L95)) | Correct. |
| Subscription linkage required | `SubscriptionRequired` enforced ([InstallmentErrors.cs#L129-L131](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/InstallmentErrors.cs#L129-L131), [CreateInstallmentScheduleCommand.cs#L114-L115](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L114-L115)) | Correct — except **no production command creates the schedule**. See §13. |
| Cancellation when contract is cancelled | `CancelSubscriptionCommand` cancels future non-paid/non-cancelled installments ([CancelSubscriptionCommand.cs#L251-L272](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs#L251-L272)) | Correct. |

### Conclusion on the Installment model

The Installment entity is **already an authoritative Payment Obligation engine**, not just "a list of due dates." It implements payment obligation, entitlement coverage, and reconciliation rules in a single model with a hardened command path.

The previous working hypothesis "Centerix needs a Payment Obligation engine" was either:

1. **Already satisfied** — and was in fact delivered incrementally across Tasks 8, 9, 18.4, 21; or
2. **Stale relative to current HEAD** — the previous architect looked at an earlier snapshot.

The repository does **not** currently provide evidence that the entity is wired into the production Offer→Contract→Subscription path. `CreateInstallmentScheduleCommand` exists but no Offer-acceptance or Contract-activation flow calls it. See §13 Scenario 2 and §23 critical findings.

---

## 5. Payment Obligation Assessment

### Is a separate `PaymentObligation` aggregate required?

**No.** The current `Installment` aggregate already fulfills every obligation implied by the prompt's audit questions:

* Identifies a specific amount due at a specific date → `Amount`, `DueDateUtc`.
* Identifies the entitlement period it pays for → `CoveredPeriodStartUtc/EndUtc`.
* Detects overdue → `IsOverdue(now)`, `Status == Overdue`.
* Supports partial payment → `PartiallyPaid`, derived `RemainingAmount`.
* Records settlement → `PaymentAllocation` rows, `SettledAmount` derived.
* Reverses settlement → `ReverseAllocation`, `SynchronizeSettledAmount`.
* Distinguishes covered period from due date → explicit invariants.
* Supports cancel-by-contract-termination → `Cancel()`.

What is **not** currently true (and therefore is the actual remaining work, not a new obligation engine):

1. There is **no production flow that creates an installment schedule from an Offer or Contract**. `CreateInstallmentScheduleCommand` is a backend command with a controller (`InstallmentsController`) but no Offer/Contract lifecycle calls it. Installments are therefore created only manually or by tests today.
2. There is **no domain representation of "this contract is on installment terms vs full upfront"**. The current `Contract` does not carry `PaymentTerms`. This is the gap that the new business rule exposes.

Both of these are **smaller** than a fresh PaymentObligation design and can be fixed without disturbing the existing installment model.

---

## 6. Payment vs Entitlement Analysis

Centerix already separates these six concepts:

| Concept | Where it lives |
|---|---|
| Money owed | `Contract.ContractedAmount` + `Installment.Amount` per obligation |
| Money received | `Payment.Amount` (completed) |
| Money allocated | `PaymentAllocation.AllocatedAmount` (active) |
| Period paid for | Sum of `Installment.CoveredPeriodStartUtc/EndUtc` rows for the contract where allocations are `Active` and `CoveredPeriodEndUtc <= subscription.BaseEndsAtUtc` |
| Period entitled | Sum of `Installment.CoveredPeriodStartUtc/EndUtc` regardless of payment (i.e., all installments for an active subscription) |
| Period consumed | `subscription.IsActiveAsOf(utcNow)` semantics; refund uses day-based `elapsedDays / contractDurationDays` ([RefundCalculationService.cs#L56-L106](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Refunds/RefundCalculationService.cs#L56-L106)) |

### Findings

* **Money → allocation → obligation → covered period is reconstructable** via `PaymentAllocation` → `InstallmentId` ([PaymentAllocation.cs#L19](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/PaymentAllocation.cs#L19)) → `Installment.CoveredPeriodStartUtc/EndUtc`. The chain exists.
* **No persisted "covered period" on Invoice itself** — Invoice only carries `ContractId/SubscriptionId/BillingCycleId`. To answer "which obligation paid for this invoice" one must join through `BillingCycle.SubscriptionId` → `TenantPlan.Id` → `Installment.SubscriptionId`. This is correct but indirect. Adding `Invoice.InstallmentId` (or a many-to-many if multiple installments settle the same invoice) would harden auditability but is **not** a correctness gap.
* **Partial payment semantics are enforced.** `Installment.ApplyAllocation` rejects over-allocation; `PaymentAllocation` is the single source of truth.
* **Overpayment is captured as `TenantCredit` of `SourceType.Overpayment`** ([AllocatePaymentCommand.cs#L344-L379](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs#L344-L379)), with ledger entry, `CustomerPaidEconomicValue = Amount`. Overpayment credits are automatically available for the next invoice.
* **Refund ↔ Settlement integrity is preserved** — refunds only reference completed payments, and `[RefundAllocation.Amount] == Refund.Amount` is the audit invariant.

### Risks

* The **chain `Payment → Allocation → Installment`** is enforced by FK, but **there is no application-level rule that prevents a `PaymentAllocation` from pointing to an `InstallmentId` whose `ContractId` differs from the `Invoice.ContractId`**. The handler checks this inline ([AllocatePaymentCommand.cs#L268-L271](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs#L268-L271)) but it is not a database check. MEDIUM.

---

## 7. Bonus / Benefit Assessment

### Current behavior (evidence)

`ContractBenefit` ([ContractBenefit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/ContractBenefit.cs#L29-L222)) carries:

* `BenefitType` (mostly `PhysicalGift` is delivered; other types are gated by `OnlyPhysicalGiftCanBeDelivered`).
* `ContractualValue`, `CurrencyCode` (snapshot, immutable after creation).
* `EligibilityStatus` lifecycle (`NotEligible → Eligible → Delivered`).
* `IsGranted` (delivery), `GrantedAtUtc`, `DeliveredBy`.
* 3-month contract-cap check on total `ContractualValue` ([Contract.cs#L420-L425](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L420-L425)).

`BenefitEligibilityService.CanBecomeEligible` ([BenefitEligibilityService.cs#L28-L61](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L28-L61)) requires:

1. `Contract.Status == Active`.
2. `completedPaymentTotal >= contractedAmount` (when contracted amount > 0).
3. No overdue installment (`hasOverdueInstallment == false`).

### Findings

* **The current rule is binary on "fully paid"**, not on payment terms. It does not distinguish a 12-month contract paid in 3 installments from the same contract paid upfront. This is **exactly the gap the prompt is asking us to expose**.
* **Zero-value benefits are not exempt** — explicit comment ([BenefitEligibilityService.cs#L17-L19](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L17-L19)).
* **BonusMonths** are handled at the **subscription entitlement** level (`EffectiveEndsAtUtc = BaseEndsAtUtc + BonusMonths`). They are NOT a `ContractBenefit` and therefore never go through eligibility. Bonus months are always granted to the subscription regardless of payment terms — see §8 for the question of whether they should be tied to payment terms.
* **PhysicalGift is the only deliverable benefit type** ([ContractBenefit.cs#L160-L164](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/ContractBenefit.cs#L160-L164)). Other types are present in the enum but cannot be delivered, which is suspicious but harmless.

### Where eligibility should live (analysis)

Per the prompt's question A: where should bonus eligibility live?

* **Not on `Offer`** — Offer is a calculated snapshot used to produce a Contract. Eligibility rules belong to the commercial commitment, not the calculation.
* **Not on `Subscription`** — Subscription is a lifecycle projection; commercial bonus policy must not change when a subscription is renewed.
* **On `ContractBenefit`** — already there as `EligibilityStatus`. The **policy** that decides when to flip it belongs in a domain service that takes:
  * `ContractBenefit`
  * `Contract.PaymentTerms` (proposed, currently absent)
  * `Contract.ContractedAmount`
  * `completedPaymentTotal`
  * `hasOverdueInstallment`
  * `BenefitBonusPolicy` (proposed, currently absent)

The current `IBenefitEligibilityService` is the correct location, but its signature is **incomplete**: it does not accept a `PaymentTerms` parameter and therefore cannot implement the new rule.

### When does it become immutable?

The natural anchor is **Contract activation** — once the Contract becomes `Active`, the eligibility rule is fixed for the lifetime of that contract. This is already true because the rule depends only on Contract-level data. The new rule therefore only requires:

* A `PaymentTerms` attribute on the Contract (snapshotted from Offer).
* A `BenefitBonusPolicy` (or inline rule) consulted by `IBenefitEligibilityService` that can grant/deny bonus for `Installments` contracts unless explicitly allowed by the Offer.

This is implemented in §20.

---

## 8. Full Payment vs Installment Bonus Policy

### What the new requirement asks

> "Full upfront payment may intentionally qualify for bonuses/benefits that are not available to installment customers. In a rapidly changing economic environment and with currency depreciation, immediate full payment has greater commercial value to Centerix than deferred installment payments."

### Current state

The distinction does not exist. Specifically:

| Check | Result |
|---|---|
| Is `PaymentTerms` modeled on `Contract`? | **No** — grep finds no property `PaymentTerms`, `PaymentMode`, `IsFullPayment`, `IsInstallment`, `Upfront`, `InstallmentTerms`. |
| Is the distinction captured in `Offer`? | **No.** `Offer` has `DurationMonths`, `ChargedMonths` (for `PayForXMonths` promotions only), `BonusMonths` — but no payment-mode attribute. |
| Does `IBenefitEligibilityService` accept a payment-mode input? | **No** ([IBenefitEligibilityService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Contracts/Services/IBenefitEligibilityService.cs#L20-L46)). |
| Does `BenefitEligibilityService.CanBecomeEligible` distinguish the two? | **No** — it only checks `completedPaymentTotal >= contractedAmount` ([BenefitEligibilityService.cs#L53-L54](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L53-L54)). |
| Are there tests for "full payment bonus"? | **No** — see §30. |
| Are there tests for "installment denied bonus"? | **No.** |

This is the **critical missing model**.

### Recommended domain representation (analysis)

Three reasonable shapes were considered:

| Shape | Verdict |
|---|---|
| **A. Boolean `Contract.IsFullUpfront`** | Rejected. Confuses payment mode (commitment at acceptance) with payment state (settled amount). A contract that *commits* to installments but receives full payment early is not "full upfront" by mode — and yet the new requirement arguably wants bonus for it (see edge case below). |
| **B. `Contract.PaymentTerms` value object with two arms** (`FullUpfront`, `Installments(count, schedule)`) | **Recommended.** Anchors the commercial *commitment* (made at acceptance) separately from the *settlement state*. Allows per-contract/per-offer configuration. |
| **C. PaymentMethod extension** | Rejected. `PaymentMethod` is a payment-channel enum (Cash, Wallet, InstaPay, Card, BankTransfer, Other) at [PaymentMethod.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/Enums/PaymentMethod.cs#L1-L25). Reusing it would conflate channels with schedule. |

### Recommended shape (B): `PaymentTerms` value object

```csharp
public enum PaymentTermsKind : byte { FullUpfront = 0, Installments = 1 }

public sealed record PaymentTerms(
    PaymentTermsKind Kind,
    int? InstallmentCount = null,      // null when FullUpfront
    Guid? InstallmentScheduleId = null // FK to InstallmentSchedule if materialized
);
```

* Snapshotted on `Offer` (calculated snapshot).
* Snapshotted on `Contract` at creation.
* Immutable for the life of the Contract (renewal produces a new Contract with new terms).
* When `Kind == Installments`, the `Offer.PaymentTerms` is the **commercial commitment** that the customer will pay over time. The implementation in §20 then grants the bonus only if the offer was `FullUpfront` OR if the offer explicitly overrides the default for installments.

### Bonus policy decision

Two reasonable policies:

| Policy | Pros | Cons |
|---|---|---|
| **P-A. Bonus denied for installments unless explicitly offered** | Simple. Encodes the new requirement directly. | Requires Offer author to remember to opt in. |
| **P-B. Bonus granted for installments only when contract is fully paid before service start** | Aligns with "value to Centerix" rationale. | Hard to compute cleanly; "before service start" depends on subscription timing. |

The repository already hints at the right test: `completedPaymentTotal >= contractedAmount` ([BenefitEligibilityService.cs#L53-L54](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L53-L54)). The current rule grants bonus on full payment regardless of mode. The new rule should instead say:

```
benefit eligible iff
    contract active
  ∧ (contract.paymentTerms.kind == FullUpfront ∨ contract.offer.grantedBonusForInstallments == true)
  ∧ completedPaymentTotal >= contractedAmount
  ∧ no overdue installment
```

This is the **P-A** policy: deny-by-default for installments, opt-in per offer. It mirrors the rationale ("immediate full payment has greater commercial value") without requiring dynamic settlement-state computation.

### The early-settlement edge case (prompt §12)

> Customer pays installments, then settles the remaining balance early.

**Recommended rule:**

* The customer's eligibility for the bonus is anchored at the **commercial commitment at acceptance** (`Contract.PaymentTerms.Kind == Installments`).
* Early settlement does **not** retroactively promote the customer to "full upfront" — the bonus was a commercial inducement for upfront commitment, and the customer explicitly chose installments.
* The eligibility check fires when `Contract.MarkBenefitEligible` is called (or via `CheckBenefitEligibilityCommand`) — which happens after the contract becomes Active and after the first payment window. If the contract was committed as Installments and the offer did not opt in, the benefit remains `NotEligible` regardless of how fast the customer pays.
* This is **configurable per offer** via `Offer.GrantsBonusForInstallments` (boolean). When true, the benefit is granted when full payment is achieved regardless of mode.

**Why this is the right rule** (audit reasoning):

1. It is **commercially consistent**: the offer *advertises* the bonus; the customer *commits* knowing the rule. The rule does not silently change after acceptance.
2. It is **financially consistent** with currency-depreciation rationale: customers who choose installments transfer currency-depreciation risk to Centerix; the bonus compensates Centerix for not bearing that risk; the compensation is forfeited when the customer chooses installments, regardless of how soon they pay.
3. It is **immutable at the right point** — `Contract.Activate(utcNow)` freezes the payment terms for the life of the contract.
4. It **does not require new settlement-time machinery** — eligibility is computed once, not re-evaluated on every payment.

### Alternative (rejected)

Some centers model "full upfront" as "fully paid before `StartsAtUtc`". This was rejected because:

* It creates a TOCTOU race between late payment and eligibility checks.
* It forces benefit-delivery to wait for a fully-paid snapshot, which interacts badly with `Contract.IsActiveAsOf` semantics.
* It breaks the immutability of the bonus policy — a late-paying customer retroactively unlocks a bonus that was advertised to upfront customers only.

---

## 9. Contract Snapshot Assessment

### Is the commercial snapshot sufficient to reproduce the original deal?

**Yes**, for everything except the new payment-terms field.

The Contract already carries:

* `MonthlyListPrice`, `ContractualMonthlyValue`, `CurrencyCode`, `GrossAmount`, `DiscountAmount`, `ContractedAmount`, `PromotionReference/Id/Type`, `ChargedMonths`, `BonusMonths`.
* `MaxStudents/Users/Branches/Teachers`, `StorageGb`, `SmsQuota`.
* `EntitlementSnapshotVersion` to distinguish complete from legacy/incomplete.
* `PreviousSubscriptionId` for renewal traceability.
* Children: `PricingTiers`, `Benefits`, `Features`.

`Contract.ValidateSnapshotCompleteness()` enforces these in one place ([Contract.cs#L546-L622](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L546-L622)).

### What's missing for the new requirement

* `Contract.PaymentTerms` (FullUpfront vs Installments).
* `Offer.GrantsBonusForInstallments` (the opt-in flag for the bonus).
* `Offer.PaymentTerms` (snapshot on the Offer so Contract can be created from it).

These are three small additions. The existing `ValidateSnapshotCompleteness` should be extended to require non-null `PaymentTerms`.

---

## 10. Billing / Invoice Assessment

### Task 21 closure evidence

Task 21 is **CLOSED** at commit `6c6ed34`. The closure document enumerates the verified invariants ([TASK-21-FINAL-INVOICE-COMMERCIAL-INTEGRITY-CLOSURE.md](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/docs/TASK-21-FINAL-INVOICE-COMMERCIAL-INTEGRITY-CLOSURE.md)) and reports:

* 1600 tests passed, 0 failed (739.3s) in the recorded run.
* 20 SQL Server integration tests pass.
* Filtered unique index `UX_Invoices_BillingCycleId` enforces one invoice per cycle at the database.
* Commercial-chain FKs with `Restrict`.
* `decimal(18,2)` for invoice monetary storage.

### Independent verification (re-audit)

The audit re-reads the production code paths and **confirms** each Task 21 invariant is honored:

* `ContractedAmount = GrossAmount - DiscountAmount` enforced in `Contract.Create` ([Contract.cs#L280-L284](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L280-L284)).
* `Contract.GetSubscriptionSnapshot()` divides `ContractedAmount / DurationMonths` to produce `SnapshotMonthlyCharge`, eliminating the previous double-discount bug ([Contract.cs#L521](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L521)).
* `BillingCycle.IsFullTermFor` compares `PeriodStart == StartsAtUtc && PeriodEnd == BaseEndsAtUtc` ([BillingCycle.cs#L159-L162](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/BillingCycles/BillingCycle.cs#L159-L162)).
* `CreateInvoiceFromBillingCycleCommand` uses period-identity full-term detection ([CreateInvoiceFromBillingCycleCommand.cs#L72](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs#L72)), `RoundMoney(2)` before persist, and excludes bonus months from billable duration ([CreateInvoiceFromBillingCycleCommand.cs#L96-L108](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs#L96-L108)).
* `Invoice.Create` checks the math identity `TotalAmount = Subtotal - DiscountAmount + TaxAmount` ([Invoice.cs#L99-L102](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs#L99-L102)).

### Findings

* **Invoice amounts are server-derived** — confirmed for the billing-cycle path; the legacy `CreateInvoiceCommand` re-derives from Contract too ([CreateInvoiceCommand.cs#L83-L100](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceCommand.cs#L83-L100)).
* **`InvoiceLine` aggregate is a passive container** — `Create` has no validation ([InvoiceLine.cs#L44-L56](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/InvoiceLine.cs#L44-L56)). It is not used by `CreateInvoiceFromBillingCycleHandler` (no lines are created during billing-cycle invoice generation), and lines added via `AddInvoiceLineCommand` do **not** modify the Invoice's `TotalAmount`. This is **internally consistent** (the math identity `Total = Subtotal - Discount + Tax` is preserved at the Invoice header; line totals are informational), but is **a possible audit-traceability gap** if a downstream reader assumes line totals roll up to invoice total. MEDIUM.
* **`InvoiceLineSourceType` enum exists** but the only meaningful use is via `AddInvoiceLineCommand` (handwritten one-off line items). No integration with `PayForXMonths`, `BonusMonths`, or any other automated line. There is **no automated invoice-line generation** today. See §29.
* **BillingCycle is currently created by renewal/change-plan flows**, not by an automated monthly-run job. There is **no scheduled job that creates the next BillingCycle on subscription anniversary**. The system currently relies on platform admins manually triggering renewals.

---

## 11. Refund / Cancellation Assessment

### Current behavior (evidence)

`CancelSubscriptionCommand` ([CancelSubscriptionCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs#L55-L362)) is the **single authoritative cancellation workflow**. It:

1. Guards against non-platform callers (`IPlatformAdminGuard`).
2. Loads subscription + contract (with cross-tenant validation).
3. Loads completed payments scoped to the contract's invoices.
4. Loads `IssuedSubscriptionChangeCredit.GetIssuedAmountAsync` (Task 18.4.2 already-converted credit).
5. Calls `RefundCalculationService.Calculate(contract, cancellationDate, payments, benefits, alreadyConvertedCredit)`.
6. If `calculation.IsRefundDue`, creates a `Refund` row.
7. Cancels future unpaid installments (does not cancel installments that already have allocations, even if partially paid).
8. Cancels the subscription via `subscription.Cancel(...)`.
9. Syncs `TenantRegistrySync` and writes audit.

`RefundCalculationService` ([RefundCalculationService.cs#L45-L199](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Refunds/RefundCalculationService.cs#L45-L199)) implements:

* **Used subscription amount** = historical contract pricing tiers, with explicit 2-month exception.
* **Benefit consumption** = day-based (`elapsedDays / contractDurationDays`).
* **Customer economic obligation** = `usedSubscriptionAmount + remainingBenefitValue`.
* **Refundable** = `paid - obligation - alreadyConvertedCredit`.
* **Customer outstanding** = `max(0, paid - obligation)`.

### Findings on the prompt's §16 cases

#### Case A (full upfront + bonus + gift)

* Bonus months are NOT in the refund math — they are `BonusMonths` on `Contract`, never charged. They extend `EffectiveEndsAtUtc` but not `BaseEndsAtUtc`. Refund uses `BaseEndsAtUtc` (via `(contract.EndsAtUtc - contract.EffectiveAtUtc).Days`) for day-based gift recovery. **Correct.**
* Gift is recoverable only if `IsGranted` ([RefundCalculationService.cs#L72-L84](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Refunds/RefundCalculationService.cs#L72-L84)). A non-delivered gift creates no recovery. **Correct.**
* Delivery does NOT block cancellation — refund is still computed; gift depreciation is the only deduction. **Consistent.**

#### Case B (installments + no bonus)

* Same math applies; no bonus-related refund behavior.
* No test exists for this scenario.

#### Case C (installment contract with bonus allowed)

* Same math applies. No rule today says "if bonus was allowed on an installment contract, the bonus months are recoverable". **Gap — see §19.**

### Outstanding risks

* `RefundAllocation` records the `PaymentMethod` as a `string` (`.ToString()` of enum), not as the original `PaymentMethod` enum ([RefundAllocation.cs#L74](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundAllocation.cs#L74)). This is **display only** (no behavior depends on it), but a refactor that introduces a fourth enum value would silently change the persisted string. LOW.
* The cancellation date is bounded `[subscription.StartsAtUtc, now]` ([CancelSubscriptionCommand.cs#L156-L163](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs#L156-L163)) but there is no upper-bound on "too far in the past" check (e.g., cannot cancel after `Contract.EndsAtUtc`). LOW.

---

## 12. Plan Change Assessment

`ChangeSubscriptionPlanCommand` ([ChangeSubscriptionPlanCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs#L62-L862)) is the authoritative plan-change flow. It:

1. Validates the old subscription is `Active`.
2. Wraps the entire flow in `Serializable` isolation with `ChangeTracker.Clear()` retry hygiene and `ConcurrentRenewalConflict` on deadlock.
3. Calculates a fresh Offer at current plan pricing.
4. Creates a new Contract from the Offer.
5. Creates a new Subscription with `activate=true`.
6. Creates a new BillingCycle with paid-period `[startsAt, BaseEndsAtUtc]`.
7. Creates a new Invoice = `FinalAmount` of the new offer.
8. Computes **unused paid value** from the **old** contract:
   * `consumedValue = Contract.CalculateValueForElapsedMonths(elapsedMonths)` (uses old pricing tiers).
   * `unusedValue = ContractedAmount - consumedValue`.
   * `paidAmount = Σ active payment allocations on old contract invoices + overpayment credit applications + subscription-change credit applications - executed refunds` (all currency-scoped to old contract).
   * `creditAmount = min(unusedValue, paidAmount)`.
9. Creates a `TenantCredit` with `SourceType = SubscriptionChange` and `TransferredPaidAmount` proportional to the consumed SubscriptionChange credits (Task 18.5 lineage).
10. Applies the credit to the new invoice if currencies match; otherwise leaves it Available.
11. Cancels the old subscription.

### Findings

* The `creditAmount = min(unusedValue, paidAmount)` rule is the correct "no over-credit, no double-count" rule.
* `TransferredPaidAmount` is computed proportionally — Task 18.5 lineage invariant holds.
* Cross-currency handling is present (credit stays in old currency if new contract is in different currency).
* The plan-change credit is **NOT** keyed to the installment schedule of the old contract. There is no "used installment #N, paid installment #N+M" reasoning. The credit is computed at the **contract** level.

### Critical gap on installments

**Scenario:** Customer has 12-month contract with 3 installments (4 paid each at months 1, 5, 9), 6 months elapsed. Plan changed at month 6.

* `consumedValue` from old contract = pricing-tier-based, e.g., 6 × monthly price.
* `unusedValue` = `ContractedAmount - consumedValue` = 6 months of unused value.
* `paidAmount` = sum of active allocations on old contract invoices.
* `creditAmount = min(unusedValue, paidAmount)`.

If `paidAmount` exactly equals the contract amount paid so far (i.e., all completed installments), the credit is `unusedValue`. If the customer is behind on installments (paid less than expected), `paidAmount < unusedValue` and the credit is capped at `paidAmount`. **This is correct.**

However, there is **no test** that exercises an installment-based plan change. The plan-change math is independent of the installment schedule, which is a feature: the schedule is for tracking, the contract amount is for refund/credit.

### Renewal-assessment (prompt §22) — also covered here

Renewal (`RenewSubscriptionOfferCommand`) is structurally identical to plan change but **does not compute an unused credit**. The old subscription is preserved unchanged; a new offer → contract → subscription → billing cycle → invoice chain is created starting at `oldSubscription.EffectiveEndsAtUtc` (or `now` if past).

* Old contract stays intact and is not refunded.
* New contract may have a different `PaymentTerms` (currently cannot, since the field doesn't exist).
* No bonus-months-bleed: old `BonusMonths` extend only `oldSubscription.EffectiveEndsAtUtc`, new `BonusMonths` extend `newSubscription.EffectiveEndsAtUtc`. Correct.
* No automated billing of the renewal: a platform admin must invoke the command.

---

## 13. Renewal Assessment (deeper)

Findings (extending §12):

* **Future obligations are NOT auto-generated on renewal.** The renewal command creates the new subscription + billing cycle + invoice. The new contract has no installments until a separate `CreateInstallmentScheduleCommand` is invoked (which the production flow does not currently invoke). See Scenario 2.
* **Concurrent renewal races** are protected by `Serializable` isolation + a non-terminal-unique index on `TenantPlans` (referenced by `RenewSubscriptionOfferCommand.cs#L172-L186`).
* **No "auto-renewal" path** — `RenewSubscriptionOfferCommand` is the only renewal API; `AutoRenew` is a snapshot on the Subscription ([TenantPlan.cs#L80](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs#L80)) but no background job honors it.

---

## 14. Multi-Tenant Security Assessment

### Evidence of consistent tenant isolation

* **All financial aggregates implement `IHasTenantId`** (inherited from `AuditableEntity<T>`).
* **Global query filter** on `TenantId` for tenant-scoped entities is set up via Finbuckle + `TenantInterceptor` ([centerix-development skill](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/.trae/skills/centerix-development/SKILL.md)).
* **TenantGuardMiddleware** rejects requests lacking tenant resolution on tenant-scoped endpoints ([TenantGuardMiddleware.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.API/Infrastructure/TenantGuardMiddleware.cs)).
* **Hand-rolled cross-tenant checks** are present in most financial handlers:
  * `AllocatePaymentCommand` rejects `payment.TenantId != invoice.TenantId` ([AllocatePaymentCommand.cs#L194-L198](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs#L194-L198)).
  * `ApplyCreditToInvoiceHandler` rejects `credit.TenantId != invoice.TenantId` ([CreateTenantCreditCommand.cs#L321-L324](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateTenantCreditCommand.cs#L321-L324)).
  * `CancelSubscriptionCommand` rejects `contract.TenantId != subscription.TenantId` ([CancelSubscriptionCommand.cs#L180-L184](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs#L180-L184)).
  * `ChangeSubscriptionPlanCommand` and `RenewSubscriptionOfferCommand` operate under `IgnoreQueryFilters()` and rely on the `SubscriptionId` arg + tenant derivation from the loaded entity.

### Findings

* **Platform admin operations bypass tenant filters via `IgnoreQueryFilters()`** — every such call is gated by `IPlatformAdminGuard.EnsurePlatformAdmin()` ([centerix-development skill](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/.trae/skills/centerix-development/SKILL.md)). Correct posture.
* **`CheckBenefitEligibilityCommand`** runs under tenant filter and reads `dbContext.TenantId` — it is tenant-scoped. Good.
* **RefundCalculationService** is a **pure function** — it takes `Contract`, `payments`, `benefits` as parameters and does no DB lookup. The handler filters payments by tenant+contract+status before passing them in ([CancelSubscriptionCommand.cs#L201-L208](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs#L201-L208)). Correct.
* **`RefundAllocation.PaymentMethod` is stored as `string`** ([RefundAllocation.cs#L17](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/RefundAllocation.cs#L17)). Not a security risk but an invariant worth pinning.
* **`IHasTenantId` is required for tenant filtering, but `ContractBenefit` does not implement `IHasTenantId`** — its `TenantId` is reachable via `Contract.TenantId`. This is correct via EF navigation but could be exploited if a future handler does `dbContext.ContractBenefits.Where(...).FirstOrDefault()` without joining Contract. LOW.

### No critical multi-tenancy gaps observed for the financial domain.

---

## 15. Concurrency / Idempotency Assessment

### Evidence of hardened concurrency

| Mechanism | Where |
|---|---|
| `IsolationLevel.Serializable` | Every mutating financial command: `AllocatePayment`, `ApplyCreditToInvoice`, `CreateInstallmentSchedule`, `CancelSubscription`, `ChangeSubscriptionPlan`, `RenewSubscriptionOffer`. |
| `ChangeTracker.Clear()` on retry | All commands retry with cleared tracker to avoid stale Added/Modified entities leaking across attempts. |
| Deadlock detection (`1205`) + bounded exponential backoff (50ms × 2^attempt) | All commands. |
| Duplicate-key detection (`2601/2627`) | All commands. |
| `DbUpdateConcurrencyException` retry path | All commands. |
| **UPDLOCK, ROWLOCK, HOLDLOCK** on critical reads | `AllocatePayment` on `Invoices`, `ApplyCreditToInvoice` on both `TenantCredits` and `Invoices`. |
| Idempotency keys | `Payment.IdempotencyKey`, `TenantCredit.IdempotencyKey`, `CreditApplication.IdempotencyKey`, `Refund.IdempotencyKey`. Unique filtered indexes (where present) protect against TOCTOU races. |
| Optimistic concurrency tokens (`byte[] RowVersion`) | All financial aggregates (`Invoice`, `Payment`, `PaymentAllocation`, `TenantCredit`, `CreditApplication`, `Refund`, `RefundAllocation`, `CustomerLedgerEntry`, `PaymentReceipt`, `Installment`, `BillingCycle`). |
| Hand-tracked uniqueness | `UX_Invoices_BillingCycleId` filtered unique index on non-null `BillingCycleId` — the authoritative guard for one-invoice-per-cycle. |

### Findings

* **The combination of Serializable + UPDLOCK + IdempotencyKey + filtered unique index is correct.**
* **Idempotency check ordering is consistent**: handlers check idempotency **before** financial validations, so a retry that occurs after settlement does not fail with `AllocationExceedsInvoice`. Verified at `AllocatePaymentCommand.cs#L211-L231` and `CreateTenantCreditCommand.cs#L242-L264`.
* **No idempotency key on `Installment` itself.** `CreateInstallmentScheduleCommand` relies on `(existingCount > 0) → ScheduleAlreadyComplete` ([CreateInstallmentScheduleCommand.cs#L133-L137](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs#L133-L137)) as the idempotency guard. If a schedule creation is retried after the count check passes but before commit, the duplicate-key path triggers; after commit, the count check rejects. The race window is small but **could** be closed with an idempotency key. LOW.
* **Reconciliation uses non-tracking reads** but does not use `IsolationLevel.Serializable` — this is acceptable because it only reads (`dbContext.TenantPlans.IgnoreQueryFilters().FirstOrDefault(...)`) and writes the loaded entity back. The loaded entity's `RowVersion` is the optimistic concurrency guard.
* **One notable gap: the `CustomerLedgerEntry.RunningBalance` is denormalized** — concurrent settlement handlers that read `previousBalance` via `Sum(...)` and write `RunningBalance` are **racey across tenants** (a tenant-scoped `Sum` will not serialize two writers in different handlers if neither takes a Serializable transaction on the tenant's ledger). Today this works because every settlement handler takes `Serializable` on the invoice/credit/payment row, which acquires range locks that include the tenant's ledger writes. But this is **implicit**, not enforced. MEDIUM.

---

## 16. Database Integrity Assessment

### Verified at the database level

| Invariant | Mechanism |
|---|---|
| One invoice per `BillingCycle` | `UX_Invoices_BillingCycleId` filtered unique index. |
| Unique `InvoiceNumber` | `UX_Invoices_InvoiceNumber`. |
| `Invoice.ContractId/SubscriptionId/BillingCycleId` FKs | `Restrict` deletes. |
| Invoice money precision | `decimal(18,2)` per `InvoiceConfiguration`. |
| Subscription snapshot money | `decimal(18,2)` per `TenantPlanConfiguration`. |
| Installment money | `decimal(18,2)` per `InstallmentConfiguration`. |
| Tenant scope | `IHasTenantId` + global query filter + `TenantInterceptor` auto-stamp. |
| Concurrency | `RowVersion` (`byte[] IsRowVersion()`). |
| One active installment schedule per contract | `UX_Installments_TenantContractSequence`. |
| One credit per source | `UX_TenantCredits_TenantId_SourceType_SourceId`. |

### Findings

* **`Contract.ContractedAmount = GrossAmount - DiscountAmount`** is enforced **only at application level** in `Contract.Create` ([Contract.cs#L280-L284](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L280-L284)) and re-checked in `ValidateSnapshotCompleteness` ([Contract.cs#L592-L596](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L592-L596)). **No check constraint** enforces this. MEDIUM.
* **`Invoice.TotalAmount = Subtotal - DiscountAmount + TaxAmount`** is enforced at application level ([Invoice.cs#L99-L102](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs#L99-L102)). No check constraint. MEDIUM.
* **`TenantCredit.Amount >= RemainingAmount`** is enforced only in `ConsumeAmount` ([TenantCredit.cs#L172-L181](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Credits/TenantCredit.cs#L172-L181)). No check constraint. LOW.
* **`Installment.Amount >= SettledAmount`** is enforced only in `ApplyAllocation`. No check constraint. LOW.
* **No unique index on `(PaymentId, InvoiceId, InstallmentId)` for `PaymentAllocation`** — the application enforces idempotency by checking the same tuple before insert ([AllocatePaymentCommand.cs#L211-L218](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs#L211-L218)). A `UX_PaymentAllocations_TenantId_PaymentId_InvoiceId_InstallmentId` filtered unique index would harden the race. LOW.

---

## 17. Referral Reward Interaction

### Existing rule (per domain XML doc + audit)

The expected rule is: **referral reward becomes eligible only after the referred customer fully pays and remains contracted for 3 months**.

### Current implementation

* `TenantReferral.Qualify()` ([TenantReferral.cs#L79-L91](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Referrals/TenantReferral.cs#L79-L91)) sets `Status=Qualified`, `QualifiedAt=now`, `LockedUntil=now+90days`, and emits `ReferralQualifiedEvent`. **No handler exists** to invoke this method.
* `TenantReferral.ApplyReward(string appliedTo)` ([TenantReferral.cs#L93-L108](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Referrals/TenantReferral.cs#L93-L108)) transitions `Qualified → RewardApplied` if not in locked period. **No handler exists** to invoke this method.
* `TenantReferral.Revoke(reason, revokedBy)` ([TenantReferral.cs#L110-L121](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Referrals/TenantReferral.cs#L110-L121)) exists. **No handler exists** to invoke this method.
* Confirmed by [MODULE-INVENTORY-20260903.md#L237](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/docs/MODULE-INVENTORY-20260903.md#L237): *"Referrals have only C/R — no qualify/apply reward workflow despite domain events `ReferralQualifiedEvent` + `ReferralRewardAppliedEvent` existing."*
* `CreateTenantReferralCommand` ([CreateTenantReferralCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Referrals/Commands/CreateTenantReferralCommand.cs#L17-L60)) only creates `Pending` referrals. No Qualify or ApplyReward command exists.

### Findings on the prompt's §17 sub-questions

| Sub-question | Answer (current implementation) |
|---|---|
| How is "fully pays" determined? | **Not determined.** There is no command to evaluate "fully paid + 3 months contracted". |
| Can installment contracts qualify? | **Not determined by any code.** |
| Does early settlement qualify? | **Not determined by any code.** |
| Does bonus eligibility affect referral qualification? | **Not determined by any code.** |
| What happens after cancellation/refund? | **Not determined by any code.** |
| Reward caps? | `RewardType` and `RewardValue` are snapshots on the entity; no cap enforcement. |
| Can reward make renewal effectively free? | `RewardType == ExtendedDays` could in principle add to `BonusMonths` on the next renewal — but **no code path does this**. |
| Locked period 90 days | `LockedUntil = QualifiedAt + 90 days` ([TenantReferral.cs#L86](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Referrals/TenantReferral.cs#L86)) — present on the entity. |

### Risk

The whole referral qualification and reward workflow is **structurally absent**. This is a **HIGH** severity finding, though outside the scope of the installment hypothesis.

---

## 18. BillingCycle ↔ Invoice ↔ Subscription ↔ Installment Relationship

| Level | Relationship | Evidence |
|---|---|---|
| BillingCycle → Subscription | `SubscriptionId` FK, `Restrict` | `BillingCycleConfiguration`, `[TenantPlanConfiguration]`. |
| BillingCycle → Invoice | one BillingCycle → at most one Invoice (DB-enforced) | `UX_Invoices_BillingCycleId` filtered unique index. |
| Invoice → Subscription | `SubscriptionId` FK, `Restrict` (when present) | `InvoiceConfiguration`, FK constraints in migration `20260926202117_Task21_FinalInvoiceIntegrity`. |
| Invoice → Contract | `ContractId` FK, `Restrict` | `InvoiceConfiguration`. |
| Installment → Contract | `ContractId` FK (required) | `InstallmentConfiguration`. |
| Installment → Subscription | `SubscriptionId` FK (required for new installments) | `InstallmentConfiguration`, `InstallmentErrors.SubscriptionRequired`. |
| Installment → Invoice | `InvoiceId` FK (optional, currently unused) | `InstallmentConfiguration`. |

### Critical observation

* The chain `BillingCycle → Subscription → Installment → PaymentAllocation → Payment` is reconstructable.
* The chain `Invoice → Contract → Plan` is reconstructable.
* The chain `Invoice → Subscription → Contract → PaymentTerms` (proposed) is reconstructable **after** `PaymentTerms` is added.

### Gaps

* **`Invoice.InstallmentId` is not persisted.** When a payment is allocated against an invoice with `InstallmentId`, the link is on the `PaymentAllocation` row, not the Invoice. To answer "which obligation did this invoice represent", one must join `Invoice → BillingCycle → Subscription → Installment`. Indirect but correct.
* **Multiple installments can pay the same invoice** in theory (e.g., customer has 6-month installments, and the invoice covers 12 months). The handler does not currently prevent this; the cap is at the installment-amount level only. MEDIUM.

---

## 19. Payment Allocation (deeper)

`PaymentAllocation` carries:

* `PaymentId`, `InvoiceId`, `InstallmentId?`, `AllocatedAmount`, `Status`, `AllocatedAtUtc`, `RowVersion`.

The full allocation proof is:

```
Payment
  .Allocations (active)
    → PaymentAllocation.InvoiceId  → Invoice (must be same tenant)
    → PaymentAllocation.InstallmentId → Installment (must be same tenant + contract)
        → Installment.CoveredPeriodStartUtc / CoveredPeriodEndUtc (entitlement)
```

**Verdict:** the chain is reconstructable but **not enforced by a database check** that the `Installment.ContractId == Invoice.ContractId`. Application check at [AllocatePaymentCommand.cs#L268-L271](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs#L268-L271).

---

## 20. Overpayment / Underpayment

### Underpayment

* Invoice = 10,000, Payment = 6,000 → `AllocatePaymentCommand`:
  * `actualAllocatedAmount = 6,000` (less than `invoiceRemaining = 10,000`).
  * Creates `PaymentAllocation(6,000)` → `Invoice.UpdatePaymentStatus()` sets `Status = PartiallyPaid`.
  * Creates `CustomerLedgerEntry.PaymentSettlement(6,000)`.
  * No overpayment credit created. **Correct.**
* **What happens to the remaining 4,000?** No automatic scheduled obligation is created; the customer must make a second payment. Correct.

### Exact payment

* Invoice = 10,000, Payment = 10,000 → `actualAllocatedAmount = 10,000` → `PaymentAllocation(10,000)` → `Invoice.Status = Paid` (via `UpdatePaymentStatus`) → `CustomerLedgerEntry.PaymentSettlement(10,000)`. Correct.

### Overpayment

* Invoice = 10,000, Payment = 12,000 → `invoiceRemaining = 10,000`, `actualAllocatedAmount = 10,000`, `overpaymentAmount = 2,000` → `TenantCredit(SourceType.Overpayment, 2,000)` is created → `CustomerLedgerEntry.CreditCreation(2,000)`. The credit is **immediately Available** and may be applied to the next invoice via `ApplyCreditToInvoiceCommand`. Correct.
* If invoice is already fully paid: `invoiceRemaining = 0`, `actualAllocatedAmount = 0`, `overpaymentAmount = 12,000` → full amount becomes credit. Correct.

### Verification

The overpayment path is hardened with both application and DB-level guards. **No bug found.**

---

## 21. Plan Change (deeper)

See §12. **No additional gaps** beyond the absence of a test for installment-based plan changes (which is a test-coverage issue, not a model gap).

---

## 22. Renewal (deeper)

See §13. The two notable observations:

1. **Auto-renewal is a snapshot on the Subscription but has no background job.** All renewals are admin-triggered via `RenewSubscriptionOfferCommand`. This is a deliberate "platform-controlled renewal" posture, acceptable for the greenfield design.
2. **Renewal does NOT generate installment schedules.** Even when the new contract is on installment terms (proposed), no `CreateInstallmentScheduleCommand` is invoked by `RenewSubscriptionOfferCommand`. The new contract has no installments until manually created.

---

## 23. Business Scenarios — End-to-End Trace

### Scenario 1 — Full upfront (12 months, full payment, 2 bonus months, no gift)

1. Plan selected → `CalculateOfferCommand` → `PromotionCalculationService.Calculate(plan, 12, now, eligiblePromotions)` → `CalculatedOffer(FinalAmount=12,000, BonusMonths=0)` (Plan has no bonus; bonus is a separate field from promotion).
2. `Offer.Create(...)` with `BonusMonths=2` from `Plan.BonusMonths`.
3. `Offer.Accept()` → Status `Accepted`.
4. `Contract.Create(...)` from Offer with `BonusMonths=2, GrossAmount=12,000, DiscountAmount=0, ContractedAmount=12,000, MonthlyListPrice=1,000`.
5. `Contract.GetSubscriptionSnapshot()` returns `MonthlyListPrice=1,000, MonthlyCharge=1,000, BonusMonths=2, DurationMonths=12`.
6. `SubscriptionFactory.CreateFromSnapshotAsync(...)` → `TenantPlan` with `SnapshotPrice=1,000, SnapshotMonthlyCharge=1,000, BaseEndsAtUtc=now+12m, EffectiveEndsAtUtc=BaseEndsAtUtc+2m`.
7. `BillingCycle.Create(now, BaseEndsAtUtc)` (paid period only).
8. `CreateInvoiceFromBillingCycleCommand` → `Invoice(Subtotal=12,000, Discount=0, Tax=0, Total=12,000)`. Period end = `BaseEndsAtUtc`.
9. `Payment.Create(12,000, Card)` + `Complete` → `AllocatePaymentCommand(InvoiceId, 12,000)` → `PaymentAllocation(12,000)` → `Invoice.Status=Paid`.
10. **Bonus months: subscription has access for 14 months** (12 paid + 2 free). `BonusMonths` are NOT in invoice. Correct.
11. **Benefit eligibility** (if gift present): would require `completedPaymentTotal >= 12,000` → satisfied.

**Verdict:** Works. Bonus eligibility for physical gifts is satisfied because the customer paid in full.

### Scenario 2 — Installment without bonus (12 months, 3 installments, 2 bonus months advertised, bonus not available for installments)

This scenario **cannot be expressed** in today's code because:

* `Offer.PaymentTerms` does not exist.
* `Contract.PaymentTerms` does not exist.
* `IBenefitEligibilityService` does not accept a payment-mode input.

What actually happens today if a customer requests installments:

* Same Offer/Contract/Subscription/BillingCycle/Invoice chain as Scenario 1, with `Invoice.TotalAmount = 12,000`.
* No `CreateInstallmentScheduleCommand` is invoked by the Offer-acceptance flow.
* The customer makes a single `Payment(12,000)` if they pay in full, or multiple `Payment(<installment>)` allocations if they pay over time. There is no automation.
* If a physical gift is in the contract, the gift becomes eligible the moment total payments reach `12,000` — which means the installment customer receives the bonus gift that the prompt says they should NOT receive.

**This is the gap the new requirement exposes.** The fix is in §8.

### Scenario 3 — Installment with bonus allowed

Same as Scenario 2 but `Offer.GrantsBonusForInstallments = true`. The benefit eligibility check is augmented to accept this flag.

### Scenario 4 — Installment paid early

Customer starts with 3 installments, then settles the remaining balance before the next due date.

* `PaymentAllocation` records show full settlement before `Installment.DueDateUtc`.
* `Installment.Status` flips to `Paid` via `RecalculateStatus`.
* All future `Installment` rows still exist in `Pending` status until their `DueDateUtc` passes or the contract is cancelled.
* No automated reconciliation makes them `Cancelled` just because the customer paid early.
* Benefit eligibility: with current rule, gift is granted because `completedPaymentTotal >= contractedAmount`.
* With new rule (P-A): the gift is **NOT** granted because `Contract.PaymentTerms.Kind == Installments` and the offer did not opt in. This is the recommended behavior.

### Scenario 5 — Partial payment

Customer pays 6,000 of 12,000 obligation.

* `AllocatePaymentCommand` creates `PaymentAllocation(6,000)`, `Invoice.Status=PartiallyPaid`, ledger entry.
* `Installment.RemainingAmount = 6,000`, `Status = PartiallyPaid` (or `Overdue` if past `DueDateUtc`).
* Customer retains access for the `CoveredPeriodStartUtc` → `CoveredPeriodEndUtc` window of the first installment.
* Customer does NOT have access beyond `CoveredPeriodEndUtc` of the **paid** installment — the next installment is still `Pending`/`Overdue`.

### Scenario 6 — Overpayment

See §20. Correct.

### Scenario 7 — Cancellation after partial consumption

Customer paid 8,000 of 12,000 over 6 months. Cancels at month 6.

* `CancelSubscriptionCommand` calculates:
  * `elapsedMonths = 6`, `usedSubscriptionAmount = 6 × MonthlyListPrice = 6,000` (no tier used for 6 months — falls back to monthly × 6).
  * `consumedBenefitValue` based on day-based ratio.
  * `amountActuallyPaid = 8,000` (active allocations).
  * `customerEconomicObligation = usedSubscriptionAmount + remainingBenefitValue`.
  * `refundableAmount = paid - obligation - alreadyConverted`.
  * If `refundable > 0` → `Refund` created.
* Future unpaid installments are `Cancelled`.
* Already-paid-but-overdue installments remain in their current status (the handler skips installments with active allocations — [CancelSubscriptionCommand.cs#L263-L264](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs#L263-L264)).
* Subscription `Status = Cancelled`.
* **No automatic credit** for the partially-paid installment's unconsumed covered period. This is **a possible gap**: if the customer paid for 3 months (Installment #1 of 3) and consumed 6 months, the over-payment is **not refunded** because the calculation is contract-scoped, not installment-scoped. MEDIUM.

### Scenario 8 — Plan change

See §12. No additional findings.

### Scenario 9 — Renewal

See §13. No additional findings.

### Scenario 10 — Concurrent payment

* Two requests try to allocate the same payment: `AllocatePaymentCommand` UPDLOCK on the invoice + idempotency check (before capacity checks). Loser returns idempotent success or `ConcurrencyConflict`.
* Two requests try to settle the same installment: `Installment.ApplyAllocation` checks projected settled amount and rejects if it exceeds; the second request receives `AllocationExceedsInstallment`. The row-level lock plus check ensures the second caller reads the post-first-commit state.
* Two requests try to create the same `InstallmentSchedule`: `existingCount > 0` check + `UX_Installments_TenantContractSequence` filtered unique index. Loser receives `ScheduleAlreadyComplete` or `DbUpdateException` mapped to `ConcurrencyConflict`.

**Verdict:** Hardened.

---

## 24. Dead / Duplicate / Conflicting Models

### Dead code

* `TenantCredit.Apply(Guid invoiceLineId)` ([TenantCredit.cs#L138-L147](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Credits/TenantCredit.cs#L138-L147)) and `ApplyToInvoice(Guid invoiceId)` ([TenantCredit.cs#L149-L158](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Credits/TenantCredit.cs#L149-L158)) — both are zero-remaining shortcuts that are not invoked anywhere. Only `ConsumeAmount(decimal)` is used (it supports partial). LOW (dead code, harmless).
* `Legacy Activate/Cancel` paths referenced in the comment at [CancelSubscriptionCommand.cs#L33-L36](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs#L33-L36): *"Legacy command (Centerix.Application.Platform.Commands.CancelSubscriptionCommand) delegates to this command for paid contract scenarios."* The legacy path lives at [Centerix.Application\Platform\Commands\CancelSubscriptionCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Commands/CancelSubscriptionCommand.cs). Both exist; the audit does not currently establish which is wired into the API. MEDIUM (potential for double-firing).
* `PlanFeature` config exists ([PlanFeatureConfiguration.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Configurations/PlanFeatureConfiguration.cs)) but is not part of the `Plan` aggregate's commercial snapshot. `PlanFeatures` is loaded via `Include(p => p.PlanFeatures)` only in production paths that need features. No dead code here.
* `PlatformPayment` ([PlatformPayment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/PlatformPayment.cs#L6-L60)) is an unused parallel payment model that coexists with the newer `Payment` + `PaymentAllocation` model. The collection on `Invoice._platformPayments` is private and has no producer. **Dead**. Confirmed by [MODULE-INVENTORY-20260903.md](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/docs/MODULE-INVENTORY-20260903.md) reference. MEDIUM (cleanup candidate).

### Duplicates / conflicting

* **No conflicting installment calculation** — only one `RecalculateStatus` and one `ApplyAllocation`. Good.
* **No conflicting refund calculation** — `RefundCalculationService` is the only one.
* **No double-discount path** — Task 21 closed this.
* **No silent fallbacks to invented grace period** — `SubscriptionReconciliationService` throws if `SubscriptionPolicy` is not seeded ([SubscriptionReconciliationService.cs#L155-L165](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/SubscriptionReconciliationService.cs#L155-L165)).

### Silent exception handling

* `AllocatePaymentCommand` swallows `DbUpdateException` with `IsDuplicateKeyException` and returns idempotent success — correct behavior.
* `AllocatePaymentCommand` swallows `DbUpdateException` with `IsDeadlockException` — correct.
* `CreateTenantCreditHandler` swallows `DbUpdateException` and attempts idempotency check — correct.
* `ChangeSubscriptionPlanCommand` swallows `DbUpdateException` with `IsDuplicateKeyException` and resolves via `TryResolveReplayResultAsync` — correct.
* No silent authorization-exception swallowing observed.

### Magic numbers

* `MaxDeadlockRetries = 3` repeated across commands — should be a constant. LOW.
* Backoff `50 * Math.Pow(2, attempt)` ms — same. LOW.
* `DefaultConnection` — infrastructure concern.
* `gracePeriodDays` is policy-driven (good).

---

## 25. Time / Date Semantics

* All financial dates are stored as `datetime2` (UTC).
* `TenantPlan.ComputeEffectiveEndsAtUtc` is the **single calendar-month helper** ([TenantPlan.cs#L151-L152](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs#L151-L152)) — used by `Contract.GetSubscriptionSnapshot` indirectly and by `CreateContractCommand`, `RenewSubscriptionOfferCommand`, `ChangeSubscriptionPlanCommand` explicitly. **Good** — single source of truth.
* `BillingCycle.ComputeBillableMonths` uses `(end.Year − start.Year) × 12 + (end.Month − start.Month)` ([BillingCycle.cs#L96-L111](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/BillingCycles/BillingCycle.cs#L96-L111)). Consistent with calendar-month addition.
* **Inclusive/exclusive boundaries**:
  * `EffectiveEndsAtUtc` is the EXCLUSIVE end of the access window: `IsActiveAsOf(now) → now < EffectiveEndsAtUtc` ([TenantPlan.cs#L249-L250](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs#L249-L250)).
  * `Installment.CoveredPeriodEndUtc` is also treated as exclusive in `GetBillableMonthsFor` — `PeriodEnd <= subscription.BaseEndsAtUtc` clamps to `BaseEndsAtUtc`. So Feb's covered period `[Jan 1, Feb 1)` bills one month. Good.
* **Off-by-one**: not observed.
* **Month-end / leap year**: `AddMonths` is used directly, with natural clamping (Jan 31 + 1 month = Feb 28/29). The repository relies on `DateTime.AddMonths` semantics — verified.
* **Partial months**: a positive period that stays inside one calendar month returns `MinimumBillableMonths = 1` ([BillingCycle.cs#L24](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/BillingCycles/BillingCycle.cs#L24)). Correct.
* **No time zone handling** beyond UTC — correct posture for a multi-tenant SaaS.

### Findings

* `Invoice.PeriodStart` and `PeriodEnd` are `DateOnly` ([Invoice.cs#L13-L14](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs#L13-L14)) — they drop the time component. This is acceptable for invoice periods but means the invoice period end may not exactly equal `BillingCycle.PeriodEnd` (which is `DateTime`). Verified at [CreateInvoiceFromBillingCycleCommand.cs#L116-L117](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs#L116-L117) — `DateOnly.FromDateTime` is used. The date-only semantic is correct for display, but **means the day-of-month of `PeriodEnd` is inclusive in the Invoice header**, which may not match the access semantics of `BillingCycle.PeriodEnd` (exclusive). LOW (potential display ambiguity).

---

## 26. Financial Precision

* `decimal(18,2)` for invoice money, payment allocation, credit amounts, refund amounts, customer ledger amounts.
* `decimal(18,6)` for snapshot values (`Subscription.SnapshotPrice/MonthlyCharge/ContractualMonthlyValue`, `Offer.MonthlyListPrice`, `Contract.MonthlyListPrice/ContractualMonthlyValue`).
* The mismatch is intentional: snapshot values are computed from `ContractedAmount / DurationMonths` and may produce 6-decimal values; once an invoice is created, the value is rounded to 2 decimals.

`RoundMoney` uses `MidpointRounding.AwayFromZero` ([CreateInvoiceFromBillingCycleCommand.cs#L46-L47](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs#L46-L47)) — consistent with the promotion engine.

**Verdict:** precision is correct and documented.

---

## 27. Critical Findings

### CRITICAL

* **C-1.** The `PaymentTerms` (Full Upfront vs Installments) business distinction is **not modeled** on `Offer` or `Contract`. The new requirement is therefore impossible to enforce today. Fix described in §8, §30, §20.

### HIGH

* **H-1.** Referral qualification and reward-application workflow is **structurally absent**. `TenantReferral.Qualify()` and `ApplyReward()` exist on the entity but are never invoked. No tests cover the workflow.
* **H-2.** `IBenefitEligibilityService` does not accept payment-terms input, so the new bonus rule cannot be expressed. This is the same root cause as C-1.
* **H-3.** There is **no production flow that creates an `InstallmentSchedule`** from an Offer or Contract. `CreateInstallmentScheduleCommand` exists with a controller, but no Offer/Contract lifecycle calls it. Installments are therefore only created manually or in tests.

### MEDIUM

* **M-1.** `InvoiceLine` aggregate has no validation in `Create` ([InvoiceLine.cs#L44-L56](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/InvoiceLine.cs#L44-L56)) and lines do not roll up to `Invoice.TotalAmount`. Inconsistency with the math-identity invariant at the header.
* **M-2.** `PlatformPayment` entity is dead code — collection on `Invoice._platformPayments` has no producer.
* **M-3.** `Invoice → Installment` link is indirect (via `BillingCycle → Subscription → Installment`). A direct `Invoice.InstallmentId` (or many-to-many) would harden auditability.
* **M-4.** `CustomerLedgerEntry.RunningBalance` is denormalized and not protected by a database trigger. Concurrent settlement handlers must serialize through invoice/credit locks. Correct today but **implicit**.
* **M-5.** `Contract.ContractedAmount = GrossAmount - DiscountAmount` is enforced only in application code, not as a check constraint.
* **M-6.** `Invoice.TotalAmount = Subtotal - DiscountAmount + TaxAmount` is enforced only in application code, not as a check constraint.
* **M-7.** The cancel flow does not auto-credit the over-payment of a partially-consumed installment. If customer paid Installment #1 (4,000) and consumed 6 months, the unconsumed 2 months of that installment are **not** refunded as credit. Correct per current business rule (no installment-level refund granularity) but **worth confirming** with product.
* **M-8.** `Installment → Invoice` cross-tenant / cross-contract consistency is checked only at application level ([AllocatePaymentCommand.cs#L268-L271](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs#L268-L271)). No database constraint.
* **M-9.** Two cancel flows exist: the legacy `Platform.Commands.CancelSubscriptionCommand` and the new `Platform.Billing.Commands.CancelSubscriptionCommand`. The audit could not fully establish which controllers wire which. Cleanup candidate.
* **M-10.** `Subscription.SnapshotMonthlyCharge` is `decimal(18,2)` (per configuration), but `ContractedAmount / DurationMonths` may produce a 6-decimal result that gets rounded on snapshot creation. The division is a financial decision, not a storage limitation — should be documented.
* **M-11.** `RefundAllocation.PaymentMethod` is stored as `string`, not as the enum value. Not a security risk, but a refactor that renames an enum value will silently change persisted strings.

### LOW

* **L-1.** `Installment` creation has no idempotency key — relies on `existingCount > 0`. Race window exists but is small.
* **L-2.** `PaymentAllocation` has no DB-level unique index on `(PaymentId, InvoiceId, InstallmentId)`. Application idempotency check covers it.
* **L-3.** `ContractBenefit` does not directly implement `IHasTenantId`; relies on `Contract.TenantId`. Could be exploited if a future handler queries ContractBenefits directly.
* **L-4.** `MaxDeadlockRetries = 3` and `50 * Math.Pow(2, attempt)` ms are repeated constants.
* **L-5.** `Invoice.PeriodEnd` is `DateOnly` (inclusive), `BillingCycle.PeriodEnd` is `DateTime` (exclusive). Possible display ambiguity.

---

## 28. Contradictions and Ambiguities

| # | Topic | Ambiguity |
|---|---|---|
| 1 | Bonus eligibility for upfront vs installment | **Not defined.** No `Offer`/`Contract` field, no `IBenefitEligibilityService` parameter. |
| 2 | "Fully pays" for referral qualification | **Not defined.** No command evaluates it. |
| 3 | Early-settlement bonus eligibility | **Not defined.** Implicit in current code: gift granted the moment total payments reach `contractedAmount`. |
| 4 | `Contract` 3-month benefit cap vs commercial value cap | Cap is on `sum(ContractualValue) <= 3 * ContractualMonthlyValue` ([Contract.cs#L420-L425](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs#L420-L425)). `ContractualMonthlyValue` is set to `MonthlyListPrice` in renewal/change-plan paths but a different value in `CreateContractCommand` — the relationship between the two is **not documented**. |
| 5 | `Refund` can be `Pending → Completed` directly OR `Pending → Approved → Processing → Completed` | Multiple paths supported. No rule forbids the shortcut for low-value refunds. |
| 6 | `Invoice.Cancel` is `Draft → Cancelled` only | Cannot cancel an `Issued` invoice. To correct a wrong invoice, the system must refund/cancel-contract instead. **Correct** but undocumented. |
| 7 | Bonus months `EffectiveEndsAtUtc = BaseEndsAtUtc + BonusMonths` | Two-step addition is mandatory ([TenantPlan.cs#L151-L152](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs#L151-L152)) to avoid off-by-one when crossing month boundaries. Documented. |
| 8 | `Contract.EntitlementSnapshotVersion = 0` (legacy) vs `1` (complete) | Two versions exist. Legacy contracts cannot be safely converted. |
| 9 | `Invoice.TotalAmount` for a partial billing cycle is **derived from the immutable subscription snapshot** ([CreateInvoiceFromBillingCycleCommand.cs#L100-L108](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs#L100-L108)) — but the formula `(SnapshotPrice - SnapshotMonthlyCharge) × months` reproduces the discount exactly **once**. **Subtle**: if `SnapshotPrice - SnapshotMonthlyCharge` produces a 6-decimal value, the partial invoice inherits the rounding error. |
| 10 | `IBenefitEligibilityService.CanBecomeEligible` returns `true` if benefit is already `Eligible` or `Delivered` ([BenefitEligibilityService.cs#L39-L43](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs#L39-L43)) | This makes the function **non-idempotent across eligibility loss scenarios**: if a benefit was `Eligible` and then the customer misses an installment, the function still returns `true` even though the rule is currently violated. **BUG-CLASS**: the eligibility check should re-evaluate the rule and demote if needed. **Confirmed by trace**: `CheckBenefitEligibilityHandler` only calls `MarkEligible` if `determinedStatus == Eligible && current == NotEligible` ([CheckBenefitEligibilityCommand.cs#L70-L76](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Contracts/Commands/CheckBenefitEligibilityCommand.cs#L70-L76)) — no demotion path. **HIGH**. |

---

## 29. Recommended Domain Model

This is the **new** design that addresses the prompt's gap. Existing aggregates are preserved.

```text
─── Commercial ───────────────────────────────────────────────

Offer (immutable snapshot)
  ├── PromotionType           (existing)
  ├── BonusMonths             (existing)
  ├── ChargedMonths           (existing)
  ├── PaymentTerms            (NEW: FullUpfront | Installments(count, scheduleRef))
  ├── GrantsBonusForInstallments (NEW: bool, default false)
  ├── Benefits[]              (existing OfferBenefit)
  ├── Features[]              (existing OfferFeature)
  └── PricingTiers[]          (existing OfferPricingTier)

Contract (immutable commercial snapshot)
  ├── PaymentTerms            (NEW: snapshotted from Offer at creation)
  ├── BonusMonths             (existing)
  ├── ChargedMonths           (existing)
  ├── GrossAmount, DiscountAmount, ContractedAmount  (existing — invariant enforced)
  ├── PaymentTerms            (NEW)
  ├── Benefits[]              (existing ContractBenefit)
  ├── PricingTiers[]          (existing ContractPricingTier)
  ├── Features[]              (existing ContractFeature)
  └── Subscriptions[]         (existing TenantPlan)

─── Entitlement ───────────────────────────────────────────────

Subscription / TenantPlan (lifecycle row, immutable commercial snapshot)
  ├── SnapshotPrice, SnapshotMonthlyCharge  (existing)
  ├── DurationMonths, BonusMonths            (existing)
  ├── BaseEndsAtUtc, EffectiveEndsAtUtc      (existing — calendar-month helper)
  ├── ContractId (link)                      (existing)
  ├── Status                                 (existing — Pending/Active/PastDue/Suspended/Expired/Cancelled)
  └── Installments[]                         (NEW navigation; created at activation when PaymentTerms = Installments)

Installment (payment obligation + entitlement period — existing)
  ├── ContractId, SubscriptionId             (existing — required)
  ├── SequenceNumber, DueDateUtc             (existing — payment lateness)
  ├── CoveredPeriodStartUtc, CoveredPeriodEndUtc  (existing — entitlement)
  ├── Amount, CurrencyCode, SettledAmount, RemainingAmount  (existing — server-derived)
  └── Status                                 (existing — Pending/PartiallyPaid/Paid/Overdue/Cancelled)

─── Billing ───────────────────────────────────────────────────

BillingCycle (paid period only — existing)
Invoice (server-derived from snapshot — existing)
InvoiceLine (informational; does not roll up — existing)

─── Payments / Refunds ───────────────────────────────────────

Payment (money received — existing)
PaymentAllocation (Payment → Invoice → optional Installment — existing)
PaymentReceipt (immutable receipt — existing)
CustomerLedgerEntry (immutable financial movement — existing)
TenantCredit (customer credit — existing; adds ReferralReward path once handlers exist)
CreditApplication (immutable credit usage — existing)
Refund + RefundAllocation (existing)

─── Eligibility ───────────────────────────────────────────────

IBenefitEligibilityService.CanBecomeEligible(
    ContractBenefit benefit,
    Contract contract,                       // NEW: contract carries PaymentTerms
    decimal completedPaymentTotal,
    decimal contractedAmount,
    bool hasOverdueInstallment = false,
    bool grantsBonusForInstallments = false) // NEW
```

**Changes are additive and small:**

1. Add `PaymentTerms` (record) and `PaymentTermsKind` (enum) to Domain.
2. Add `PaymentTerms` and `GrantsBonusForInstallments` to `Offer`.
3. Add `PaymentTerms` to `Contract`. Extend `Contract.ValidateSnapshotCompleteness` to require non-null `PaymentTerms`.
4. Extend `IBenefitEligibilityService` signature; update `BenefitEligibilityService` to use the new rule.
5. Wire `CreateInstallmentScheduleCommand` invocation into `AcceptOfferCommand` (or a new `ActivateContractCommand`) when the offer's `PaymentTerms.Kind == Installments`.
6. Implement `QualifyReferralCommand` and `ApplyReferralRewardCommand` (out of financial-critical scope for this audit, but tracked).

---

## 30. Required Business Invariants (testable language)

> These are derived from the audit, not invented.

1. `Offer.PaymentTerms` is mandatory after acceptance. `null` after acceptance is an invariant violation.
2. `Contract.PaymentTerms == Offer.PaymentTerms` (snapshotted at creation, equal forever).
3. `ContractedAmount = GrossAmount - DiscountAmount` (within 0.01 tolerance). **Currently application-only; should be a check constraint.**
4. `Invoice.TotalAmount = Subtotal - DiscountAmount + TaxAmount` (within 0.01 tolerance). **Currently application-only; should be a check constraint.**
5. `Invoice.PeriodEnd <= TenantPlan.BaseEndsAtUtc` (bonus months are never billed).
6. For every `Contract`, the sum of `Installment.Amount` equals `Contract.ContractedAmount`.
7. For every `Contract`, the `Installment.CoveredPeriodStartUtc/EndUtc` intervals are **contiguous and within `[Contract.EffectiveAtUtc, Contract.EndsAtUtc]`**.
8. `Installment.SettledAmount = Σ active PaymentAllocations.AllocatedAmount`.
9. `Installment.Status` is **server-derived**, never set by clients.
10. `PaymentAllocation.Installment.ContractId == PaymentAllocation.Invoice.ContractId`. **Currently application-only; should be a check constraint or filtered index.**
11. No `PaymentAllocation.AllocatedAmount` may exceed `Payment.Amount − sum(active allocations excluding this one)`.
12. A `PhysicalGift` benefit becomes `Eligible` only when `Contract.Status == Active` AND `(Contract.PaymentTerms.Kind == FullUpfront OR Offer.GrantsBonusForInstallments == true)` AND `completedPaymentTotal >= Contract.ContractedAmount` AND no overdue installment.
13. A `PhysicalGift` benefit that becomes ineligible after being `Eligible` MUST be demoted back to `NotEligible` on the next eligibility check. **Currently not enforced.**
14. A `Refund` can be created only after `CancelSubscriptionCommand` has validated that the contract has no earlier `Refund` (idempotency).
15. A `CustomerLedgerEntry` is never modified or deleted; corrections are new offsetting entries.
16. `CustomerLedgerEntry.RunningBalance` reconstructs from movements — the denormalized value is a cache, not a source of truth. (Future: a DB trigger could enforce this.)
17. Cross-tenant financial operations are rejected at both the application check and (where applicable) the global query filter.
18. `TenantPlan.Renew(...)` is called only for renewal-flow that preserves the old subscription; `RenewSubscriptionOfferCommand` creates a new subscription instead. **Decision documented; never both.**
19. `Contract.EntitlementSnapshotVersion == 1` (Complete) for any contract created by `RenewSubscriptionOfferCommand`, `ChangeSubscriptionPlanCommand`, or `CreateContractFromOfferCommand`.
20. `BillingCycle` may have at most one `Invoice` (enforced by `UX_Invoices_BillingCycleId`).
21. `Invoice.Issued → Subtotal/Discount/Tax/Total` are immutable.

---

## 31. Recommended Implementation Strategy

The implementation is broken into logical tasks in **dependency order**. The first task is **not** "Installment Engine" — it is "Payment Terms on Offer/Contract."

### Task A — `PaymentTerms` model + snapshot (the actual gap)

1. Add `PaymentTerms` record and `PaymentTermsKind` enum in `Domain/Platform/Contracts/PaymentTerms.cs`.
2. Add `Offer.PaymentTerms` (nullable until `Accepted`) + `Offer.GrantsBonusForInstallments`.
3. Add `Contract.PaymentTerms` (required, snapshotted from Offer).
4. Extend `Contract.ValidateSnapshotCompleteness` to require non-null `PaymentTerms`.
5. Update `Offer.Create` to accept `paymentTerms` and `grantsBonusForInstallments`.
6. Update `CreateContractFromOfferCommand`, `RenewSubscriptionOfferCommand`, `ChangeSubscriptionPlanCommand`, `CreateContractCommand` to pass the offer's `PaymentTerms` to the Contract.
7. Migration: `ALTER TABLE Platform.Contracts ADD PaymentTermsKind tinyint NULL, GrantsBonusForInstallments bit NULL` then update existing legacy rows (recommend `PaymentTermsKind = FullUpfront, GrantsBonusForInstallments = 1` as the conservative default for legacy rows; document this in the closure).
8. Tests: payment-terms-on-Offer/Contract; legacy backfill default; cross-currency.

### Task B — Benefit eligibility rule update

1. Extend `IBenefitEligibilityService.DetermineEligibilityStatus` signature with `paymentTerms` and `grantsBonusForInstallments`.
2. Update `BenefitEligibilityService` to apply the rule from §8.
3. Update `CheckBenefitEligibilityCommand` to load `Contract.PaymentTerms` and pass it through.
4. **Add the demotion path** (Bug-class #10 in §28): when a benefit is `Eligible` but the rule is currently violated, transition to `NotEligible`.
5. Tests: scenarios 1, 2, 3, 4 — full upfront + bonus, installment no-bonus, installment with-bonus, early settlement.

### Task C — Wire installment schedule creation into the activation flow

1. After `Contract.Activate(utcNow)`, if `Contract.PaymentTerms.Kind == Installments`, invoke a new `ActivateContractCommand` (or extend `CreateContractFromOfferCommand` / `RenewSubscriptionOfferCommand`) that:
   * Calls `CreateInstallmentScheduleCommand` with the schedule derived from `PaymentTerms.InstallmentCount`.
   * Each installment gets `DueDateUtc = startsAt + (k × monthsBetween)`, `CoveredPeriodStartUtc = startsAt + ((k-1) × monthsBetween)`, `CoveredPeriodEndUtc = startsAt + (k × monthsBetween)` (k from 1..N).
2. Tests: schedule creation is automatic; the schedule respects `Contract.EffectiveAtUtc/EndsAtUtc`; sum of installment amounts equals `Contract.ContractedAmount`.

### Task D — Referral workflow (out of financial scope, but tracked)

1. Add `QualifyReferralCommand` and `ApplyReferralRewardCommand` (handlers).
2. Wire qualification to the settlement path: when `completedPaymentTotal >= ContractedAmount` for the referred tenant's contract AND `now >= contract.EffectiveAtUtc + 90 days`, emit a domain event consumed by a handler that calls `Qualify()`.
3. Reward application: when the referrer's next renewal happens, apply the reward (Discount → renewal Offer discount; Credit → TenantCredit; ExtendedDays → additional bonus months).

### Task E — Cleanup

1. Delete `PlatformPayment` entity and `_platformPayments` collection.
2. Delete the legacy `Platform.Commands.CancelSubscriptionCommand` (or rename and consolidate).
3. Add `IX_ContractBenefit_TenantId` index (denormalized) for direct queries.

### Task ordering rationale

* **Task A** is the actual gap. Without it, **none of the new requirements can be satisfied**.
* **Task B** is meaningless without Task A.
* **Task C** is independent of Tasks A/B (installment schedule creation existed before; the gap is that it is not wired to the lifecycle).
* **Task D** is a separate stream — not on the installment critical path.
* **Task E** is cleanup; defer until Tasks A–C are stable.

---

## 32. What NOT to Implement

### DO NOT

* **DO NOT introduce a new `PaymentObligation` aggregate.** The existing `Installment` already is one. Adding a parallel model will create duplicate sources of truth and confuse the eligibility reconciliation.
* **DO NOT add a `bool IsFullUpfront` to `Contract`.** It conflates commitment with settlement state. Use `PaymentTerms` value object.
* **DO NOT promote an installment customer to "full upfront" on early settlement.** The bonus is a commercial inducement for upfront commitment; retroactively unlocking it on settlement breaks the inducement.
* **DO NOT add an automated monthly BillingCycle job in this round.** Greenfield, controlled renewal is acceptable. A scheduled job is a separate initiative.
* **DO NOT introduce event sourcing.** The current append-only ledger (`CustomerLedgerEntry`) is sufficient for audit. Event sourcing adds operational complexity without benefit.
* **DO NOT introduce microservices or split the billing module.** The monolith is the right shape for this scale.
* **DO NOT add a generic financial-engine abstraction.** The handlers are already cohesive per aggregate.
* **DO NOT change the decimal precision.** `decimal(18,2)` for invoices and `decimal(18,6)` for snapshot values is intentional and tested.
* **DO NOT use `PlatformPayment`.** It is dead code; delete it instead of integrating.
* **DO NOT add new repositories.** The DbContext is the only abstraction needed.

---

## 33. Final Recommendation

### Direct answers

1. **Is the current Installment model sufficient?**
   **YES.** `Installment` is a fully-fledged payment obligation + entitlement period model with hardened creation, allocation, reversal, cancellation, and overdue detection. No additional Installment entity or PaymentObligation aggregate is required.

2. **Is a Payment Obligation engine required?**
   **NO.** The engine exists. What is missing is the **lifecycle wiring** that creates installments from an Offer/Contract, plus the **`PaymentTerms` model** that lets the new bonus rule be expressed. These are smaller, additive changes.

3. **Is the Bonus eligibility model sufficient?**
   **NO.** It must accept `PaymentTerms` and `GrantsBonusForInstallments`, and it must **demote** an already-Eligible benefit when the rule is currently violated.

4. **Does the new full-payment-vs-installment rule require domain changes?**
   **YES.** Three additions:
   * `PaymentTerms` on `Offer` and `Contract`.
   * `GrantsBonusForInstallments` on `Offer`.
   * `IBenefitEligibilityService` extension plus demotion path.

5. **Are there financial correctness issues that must be fixed before new features?**
   * The demotion bug (`BenefitEligibilityService` returns `true` for an already-Eligible benefit even when the current rule is violated — see §28 #10). **HIGH** — must be fixed.
   * `PlatformPayment` dead code. **MEDIUM** — should be deleted to prevent future confusion.
   * The cancel-overpaid-installment gap. **MEDIUM** — confirm with product whether installment-level refunds are in or out of scope.

6. **What should be implemented next?**
   **Task A — `PaymentTerms` model + snapshot on Offer and Contract.** This is the actual missing piece. It is the foundation for Task B (bonus rule update).

7. **Why?**
   * The premise "build a Payment Obligation engine" is rejected because the engine already exists.
   * The actual gap exposed by the new requirement is the absence of a payment-terms representation.
   * Task A is small, additive, and unblocks the bonus rule.
   * Task A's migration can safely default legacy rows to `FullUpfront` because legacy contracts were all paid upfront (the installment workflow was never production-wired).

---

## 34. Evidence Standard Compliance

* Every claim above cites a file path and (where applicable) a line range.
* Uncertainties are flagged explicitly (e.g., "the audit could not fully establish which controllers wire which" for legacy vs new cancel command).
* Test execution is **not** claimed. The Task 21 closure document reports recorded execution results (1600/1600/0, 176/177 SQL Server) at commit `6c6ed34`. The audit re-reads the production code paths and confirms the implementation matches the documented invariants but does **not** re-run the test suite.
* The audit does not introduce new code, migrations, or commands.

---

## 35. Appendix — File Index of Key Evidence

* Commercial aggregate (Contract): [Contract.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/Contract.cs)
* Offer: [Offer.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/Offer.cs)
* Subscription: [TenantPlan.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Subscriptions/TenantPlan.cs)
* BillingCycle: [BillingCycle.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/BillingCycles/BillingCycle.cs)
* Invoice: [Invoice.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Invoicing/Invoice.cs)
* Payment: [Payment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/Payment.cs)
* PaymentAllocation: [PaymentAllocation.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Payments/PaymentAllocation.cs)
* **Installment** (the obligation engine): [Installment.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Installments/Installment.cs)
* ContractBenefit: [ContractBenefit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Contracts/ContractBenefit.cs)
* TenantCredit: [TenantCredit.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Credits/TenantCredit.cs)
* CreditApplication: [CreditApplication.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Credits/CreditApplication.cs)
* Refund: [Refund.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Billing/Refunds/Refund.cs)
* RefundCalculationService: [RefundCalculationService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Refunds/RefundCalculationService.cs)
* SubscriptionReconciliationService: [SubscriptionReconciliationService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/SubscriptionReconciliationService.cs)
* BenefitEligibilityService: [BenefitEligibilityService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Platform/Services/BenefitEligibilityService.cs)
* CreateInstallmentScheduleCommand: [CreateInstallmentScheduleCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Installments/Commands/CreateInstallmentScheduleCommand.cs)
* AllocatePaymentCommand: [AllocatePaymentCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/AllocatePaymentCommand.cs)
* ApplyCreditToInvoiceHandler: [CreateTenantCreditCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateTenantCreditCommand.cs)
* CancelSubscriptionCommand: [CancelSubscriptionCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CancelSubscriptionCommand.cs)
* ChangeSubscriptionPlanCommand: [ChangeSubscriptionPlanCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs)
* RenewSubscriptionOfferCommand: [RenewSubscriptionOfferCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Commands/RenewSubscriptionOfferCommand.cs)
* CreateInvoiceFromBillingCycleCommand: [CreateInvoiceFromBillingCycleCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs)
* CheckBenefitEligibilityHandler: [CheckBenefitEligibilityCommand.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Application/Platform/Contracts/Commands/CheckBenefitEligibilityCommand.cs)
* PromotionCalculationService: [PromotionCalculationService.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs)
* TenantReferral (no handlers wired): [TenantReferral.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Domain/Platform/Referrals/TenantReferral.cs)
* Module Inventory (referral gap acknowledgment): [MODULE-INVENTORY-20260903.md](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/docs/MODULE-INVENTORY-20260903.md)
* Task 21 closure baseline: [TASK-21-FINAL-INVOICE-COMMERCIAL-INTEGRITY-CLOSURE.md](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/docs/TASK-21-FINAL-INVOICE-COMMERCIAL-INTEGRITY-CLOSURE.md)
* Installment DB configuration: [InstallmentConfiguration.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Configurations/InstallmentConfiguration.cs)
* Contract DB configuration: [ContractConfiguration.cs](file:///d:/New%20folder/Center%20Managements%20V1/Centerix/src/Centerix.Infrastructure/Data/Configurations/ContractConfiguration.cs)

---

**END OF REPORT.**