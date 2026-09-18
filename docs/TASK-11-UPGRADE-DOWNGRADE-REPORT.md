# TASK 11.1 — Subscription Upgrade/Downgrade Financial & Historical Correction Report

**Commit SHA:** `95da34633fd65dd69a016ac15f7ed1ee9899493c`

---

## Current Implementation

### Command & Handler

| Artifact | File | Class |
|---|---|---|
| Command record | `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs:45` | `ChangeSubscriptionPlanCommand` |
| FluentValidation | `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs:49` | `ChangeSubscriptionPlanValidator` |
| MediatR handler | `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs:58` | `ChangeSubscriptionPlanHandler` |
| API endpoint | `src/Centerix.API/Controllers/TenantPlansController.cs:96` | `POST {id}/change-plan` |

### Flow (Handler ExecuteChangePlanCoreAsync)

```
1. PlatformAdminGuard.EnsurePlatformAdmin()           [authorization]
2. Load old subscription (IgnoreQueryFilters)          [eligibility]
3. ValidateChangePlanEligibility → Active only         [lifecycle guard]
4. SERIALIZABLE transaction begin                      [concurrency]
5. Re-load old subscription inside transaction          [fresh read under lock]
6. Load target Plan + PricingTiers + PlanFeatures      [catalog]
7. Temporal overlap guard                              [overlap protection]
8. PromotionCalculationService.Calculate()             [authoritative Offer]
9. Offer.Create + Offer.Accept                         [immutable Offer snapshot]
10. Contract.Create + LinkToPreviousSubscription       [commercial snapshot]
11. ContractPricingTier.Create (per tier)              [tier snapshot]
12. Offer.MarkConverted                                [Offer → Contract link]
13. SubscriptionFactory.CreateFromSnapshotAsync        [TenantPlan creation]
14. BillingCycle.Create                                [billing period]
15. Invoice.Create (from Offer amounts)                [financial chain]
16. BillingCycle.MarkInvoiced                          [lifecycle]
17. oldSubscription.Cancel(now)                        [end old subscription]
18. Tenant.SetValidUpTo                                [tenant lifecycle]
19. SaveChanges + Commit                               [atomic persist]
20. AuditWriter.WriteAsync                             [post-commit audit]
```

---

## Financial Model

### Offer → Contract → Invoice Chain

**FACT:** The `PromotionCalculationService` (at `src/Centerix.Domain/Platform/Promotions/PromotionCalculationService.cs:15`) is the single source of truth for all commercial amounts.

```
Plan PricingTier (or MonthlyPrice × Duration)
        ↓
PromotionCalculationService.Calculate()
        ↓
CalculatedOfferDto { BaseAmount, DiscountAmount, FinalAmount, ChargedMonths }
        ↓
Offer.Create(baseAmount, discountAmount, finalAmount, ...)
        ↓
Contract.Create(contractedAmount = calc.FinalAmount, discountAmount = calc.DiscountAmount, ...)
        ↓
Invoice.Create(subtotal = calc.BaseAmount, discountAmount = calc.DiscountAmount, totalAmount = calc.FinalAmount)
```

**CRITICAL FIX (this correction):**

**BEFORE (incorrect):**
```csharp
var subtotal = calc.MonthlyListPrice * cycleDurationMonths;  // WRONG for PricingTier
var totalAmount = subtotal - discountAmount + taxAmount;     // independent recalculation
```

**AFTER (correct):**
```csharp
var subtotal = calc.BaseAmount;       // authoritative: tier price or Monthly×Duration
var totalAmount = calc.FinalAmount;   // authoritative: BaseAmount - DiscountAmount
```

**INFERENCE:** When a PricingTier applies (e.g., 12-month tier = 10,000), the old code produced `Invoice.TotalAmount = 1000 × 12 = 12,000` instead of the correct `10,000`. This violated the financial invariant `Invoice.TotalAmount == Contract.ContractedAmount`.

### PricingTier Authority

| Scenario | MonthlyPrice | Duration | PricingTier | BaseAmount | Old Invoice | Correct Invoice |
|---|---|---|---|---|---|---|
| 12-month tier | 1,000 | 12 | 10,000 | 10,000 | **12,000** | **10,000** |
| 6-month tier | 1,000 | 6 | 5,220 | 5,220 | 5,220 | 5,220 |
| No tier | 1,000 | 12 | (none) | 12,000 | 12,000 | 12,000 |

### Promotion Type Coverage

All five promotion types are tested to verify the financial invariant:

| Promotion Type | BaseAmount | Discount | FinalAmount | Offer=Contract=Invoice |
|---|---|---|---|---|
| No promotion | 12,000 | 0 | 12,000 | Test05, Test10 |
| PercentageDiscount (20%) | 12,000 | 2,400 | 9,600 | Test06, Test11 |
| FixedAmountDiscount (3,000) | 12,000 | 3,000 | 9,000 | Test07 |
| PayForXMonths (10) | 12,000 | 2,000 | 10,000 | Test08, Test02, Test03 |
| PromotionalPrice (8,000) | 10,000 | 2,000 | 8,000 | Test09, Test04 |

---

## Upgrade vs Downgrade

**FACT:** The `ChangeSubscriptionPlanCommand` handles both upgrade and downgrade identically. The commercial transaction is the same: a fresh Offer → Contract → Subscription chain is created using the target plan's current terms.

**INFERENCE:** The distinction between upgrade and downgrade is purely commercial (new price > old price or vice versa). The handler does not need separate logic because:
- The Offer engine calculates the correct amount for any plan
- The old subscription is cancelled regardless of direction
- No automatic refund/credit is triggered for either direction

| Test | Old Plan | New Plan | Direction | Verified |
|---|---|---|---|---|
| Test25 | 1,000/mo | 2,000/mo | Upgrade | NewOffer > OldOffer |
| Test26 | 2,000/mo | 1,000/mo | Downgrade | NewOffer < OldOffer |
| Test27 | — | 2,000/mo | Upgrade | ContractAmount = 24,000 |
| Test28 | — | 500/mo | Downgrade | ContractAmount = 6,000 |

---

## Historical Integrity

**FACT:** The old subscription's commercial snapshot is NEVER modified. The only lifecycle change is the `Cancel(now)` transition from Active → Cancelled.

### What Remains Unchanged

| Entity | Field | Verified By |
|---|---|---|
| Old Contract | ContractNumber, PlanId, ContractedAmount, DiscountAmount, PricingTiers, Benefits | Test15, Test21 |
| Old Subscription | PlanId, SnapshotPrice, DurationMonths, BonusMonths, SnapshotCurrency | Test16, Test22, Test23 |
| Old Invoice | InvoiceNumber, Subtotal, DiscountAmount, TotalAmount | Test17 |
| Old Benefits | ContractualValue, Name, CurrencyCode | Test20 |

### Lifecycle Change (Expected)

| Entity | Change | Reason |
|---|---|---|
| Old Subscription | Active → Cancelled | Required by the change-plan operation to free the non-terminal unique index slot |

**GAP:** The `TenantPlan.Cancel()` method fires a `TenantPlanCancelledEvent` domain event. This event is not consumed by any handler that would trigger a refund, so no financial side effects occur. However, if a future handler subscribes to this event and implements refund logic, upgrade/downgrade could inadvertently trigger refunds.

**PROPOSAL:** Consider adding a `CancelForPlanChange()` method or a `CancelledForUpgrade` status to distinguish administrative cancellation from financial cancellation, preventing future accidental refund triggers.

---

## Financial Transition

### Option A: New Invoice Is a Completely New Obligation

**FACT:** The current implementation creates a completely independent commercial transaction. The new Invoice, Contract, and Subscription have no financial link to the old Invoice or old payments.

**Evidence:**
- The new `Invoice.Id` is generated fresh (`Guid.NewGuid()`)
- The new `Contract.LinkToPreviousSubscription(oldSub.Id)` establishes traceability but NOT financial linkage
- Old payments remain allocated to the old invoice via their existing `PaymentAllocation` records
- No `TenantCredit` is created or applied
- No refund is triggered
- The old subscription's `Cancel(now)` does NOT interact with the billing system

**INFERENCE:** This is Option A. The new subscription creates a fresh financial obligation. The old payments stay with the old invoice. The tenant pays the new invoice amount independently.

**GAP:** If the tenant has already paid for a full year on the old plan and upgrades mid-term, they lose the remaining paid value. The old subscription is cancelled (not refunded), and a new invoice is created for the full new plan amount. This is a business decision, not a technical gap.

**CONCLUSION:** Option A is the correct current behavior. The old subscription's payments remain attached to the old invoice. No financial adjustment is made for the unused portion.

---

## Concurrency

### SERIALIZABLE Transaction

**FACT:** The handler wraps the entire commercial operation in a SERIALIZABLE transaction (`src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs:116-118`).

```csharp
await using var transaction = dbContext.IsRelational
    ? await dbContext.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
    : null;
```

### Deadlock Retry

The handler retries up to 3 times on SQL Server deadlock (error 1205) and `DbUpdateConcurrencyException` (`ChangeSubscriptionPlanCommand.cs:77-94`).

### SQL Server Concurrency Test

**Test:** `ChangePlan_ConcurrentRequests_CannotBothSucceed`
- Two concurrent requests for the same subscription
- Uses `Barrier(2)` to ensure simultaneous execution
- **Expected:** Exactly 1 success, exactly 1 failure
- **No duplicate** Contract, Subscription, BillingCycle, or Invoice

---

## Idempotency

**FACT:** The implementation uses concurrency REJECTION, not idempotent retry.

**Evidence:**
- Two identical concurrent requests will both attempt to create new entities
- The SERIALIZABLE transaction + unique index `UX_TenantPlans_TenantId_NonTerminalStatus` ensures only one can succeed
- The losing request receives `TenantPlanErrors.ConcurrentRenewalConflict`
- There is no idempotency key mechanism for the change-plan operation

**INFERENCE:** This is by design. Each change-plan request creates a distinct commercial transaction. There is no mechanism to "resume" a failed request. The client must retry if they receive a concurrency conflict.

**GAP:** If a client sends the same request twice (not concurrent, but sequential), two separate commercial transactions will be created. This is the expected behavior for a "change plan" operation — each request represents a distinct business intent.

---

## Benefits / Gifts

**FACT:** Old contract benefits are NOT copied to the new contract. The `Contract.Create()` call in the handler does not reference any benefits from the old subscription's contract.

**FACT:** The current `PromotionCalculationService` does not generate benefits as part of the Offer calculation. Benefits are added separately via `Contract.AddBenefit()` after contract creation.

**INFERENCE:** Task 11 does not grant new benefits because no trusted commercial source exists in the Offer engine for benefit generation. The new contract starts with zero benefits. If benefits are needed, they must be added through a separate workflow (e.g., the existing `ApproveTenant` flow).

---

## Audit Atomicity

**FACT:** The audit write (`AuditWriter.WriteAsync`) occurs AFTER `transaction.CommitAsync()` (`ChangeSubscriptionPlanCommand.cs:343-381`).

```
await dbContext.SaveChangesAsync(cancellationToken);  // line 343
await transaction.CommitAsync(cancellationToken);     // line 346
await auditWriter.WriteAsync(...);                     // line 348
```

**INFERENCE:** This is the existing architectural pattern across the entire codebase. Every command handler that uses transactions writes audit records post-commit. Evidence from `AllocatePaymentCommand.cs:194,380`, `ExecuteRefundCommand.cs:112,239`, `CancelSubscriptionCommand.cs:302,314`, etc.

**PROPOSAL:** No change needed. The post-commit audit pattern is intentional. If the commit succeeds but the audit write fails, the commercial change is still valid. The audit log is a non-essential secondary record. If this becomes a compliance requirement, the entire audit architecture would need redesign — which is outside Task 11 scope.

---

## TimeProvider

**FACT (FIXED):** `GenerateContractNumber()` now uses the handler's `now` parameter (derived from `TimeProvider.GetUtcNow()`) instead of `DateTime.UtcNow`.

**BEFORE:**
```csharp
private static string GenerateContractNumber()
{
    return $"CTR-CHANGE-{DateTime.UtcNow:yyyyMMdd}-{...}";
}
```

**AFTER:**
```csharp
private static string GenerateContractNumber(DateTime now)
{
    return $"CTR-CHANGE-{now:yyyyMMdd}-{...}";
}
```

All other timestamps in the handler already use the `now` parameter:
- `Offer.Create(calculatedAtUtc: now, expiresAtUtc: now.AddHours(24))` — line 196-197
- `offer.Accept(now)` — line 204
- `offer.MarkConverted(contract.Id, now)` — line 257
- `oldSubscription.Cancel(now)` — line 323

---

## Database

### Unique Index

The non-terminal subscription unique index `UX_TenantPlans_TenantId_NonTerminalStatus` (filter: `Status IN (1, 4, 5)`) enforces at most one non-terminal subscription per tenant. The change-plan operation works because:
1. New subscription is created as Active (in filter)
2. Old subscription is cancelled to Cancelled (not in filter, 3 ∉ {1,4,5})

### Migrations

**Test:** `Migrations_NoPendingMigrations` — passes. No new migration was required because the change-plan operation uses existing entities and columns.

---

## Tests

### Summary

| Category | Count | Location |
|---|---|---|
| InMemory (Phase11PlanChangeTests) | 41 | `tests/Centerix.SecurityTests/Phase11PlanChangeTests.cs` |
| SQL Server (Phase11PlanChangeSqlServerTests) | 8 | Same file |
| **Total** | **49** | |

### Full Regression

| Suite | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Non-SqlServer | 1087 | 0 | 0 | 1087 |
| SQL Server (Phase 11) | 8 | 0 | 0 | 8 |
| **Total** | **1095** | **0** | **0** | **1095** |

### Test Coverage

#### Financial Invariant (Offer = Contract = Invoice)

| Test | Promotion Type | PricingTier | Verified |
|---|---|---|---|
| Test01 | None | Yes (12mo=10,000) | Invoice=10,000 ≠ 12,000 |
| Test05 | None | No | 12,000 = 12,000 = 12,000 |
| Test06 | PercentageDiscount | No | Offer=Contract=Invoice |
| Test07 | FixedAmountDiscount | No | Offer=Contract=Invoice |
| Test08 | PayForXMonths | No | Offer=Contract=Invoice, ChargedMonths=10 |
| Test09 | PromotionalPrice | No | Offer=Contract=Invoice |
| Test10 | None | Yes (12mo=10,000) | 10,000 = 10,000 = 10,000 |
| Test11 | PercentageDiscount | Yes | 9,000 = 9,000 = 9,000 |

#### Historical Integrity

| Test | Verified |
|---|---|
| Test15 | Old Contract unchanged (ContractNumber, Amount, Discount, PricingTiers, Benefits) |
| Test16 | Old Subscription commercial snapshot unchanged |
| Test17 | Old Invoice unchanged |
| Test18 | Contract.LinkToPreviousSubscription traceability |
| Test19 | New Subscription links to new Contract |
| Test20 | Old Contract benefits NOT copied |
| Test21 | Old Contract pricing tiers NOT modified |
| Test22 | Old Subscription features NOT modified |
| Test23 | Old Subscription limits NOT modified |
| Test24 | Old Subscription Cancelled (expected lifecycle change) |

#### Upgrade/Downgrade

| Test | Direction | Verified |
|---|---|---|
| Test25 | Upgrade | NewOffer > OldOffer |
| Test26 | Downgrade | NewOffer < OldOffer |
| Test27 | Upgrade | Contract amounts correct |
| Test28 | Downgrade | Contract amounts correct |

#### SQL Server Integration

| Test | Verified |
|---|---|
| Migrations_NoPendingMigrations | No pending migrations |
| ChangePlan_EndToEnd | Full chain: Contract + Subscription + old Cancelled |
| ChangePlan_CreatesBillingCycleAndInvoice | BillingCycle + Invoice created |
| ChangePlan_InvoiceAmount_EqualsContractAmount | **PricingTier regression: Invoice=10,000 ≠ 12,000** |
| ChangePlan_OldSubscription_Cancelled | Old sub status = Cancelled |
| ChangePlan_NonActiveSubscription_Rejected | Pending sub rejected |
| ChangePlan_ConcurrentRequests_CannotBothSucceed | Exactly 1 success, 1 failure |
| ChangePlan_TenantValidUpTo_UpdatedToNewSubscription | Tenant.ValidUpTo updated |

---

## Known Limitations

1. **No automatic refund for unused portion:** When a tenant upgrades mid-term, the old subscription's remaining paid value is not refunded or credited. The old invoice remains as-is, and a new invoice is created for the full new plan amount.

2. **No benefit inheritance:** Old contract benefits are not carried to the new contract. The new contract starts with zero benefits because the Offer engine does not currently generate benefits.

3. **Cancel event could trigger future side effects:** `TenantPlan.Cancel()` fires `TenantPlanCancelledEvent`. If a future handler subscribes to this event and implements refund logic, upgrade/downgrade could inadvertently trigger refunds.

4. **Sequential duplicate requests create separate transactions:** Two sequential (non-concurrent) change-plan requests with the same parameters will create two separate commercial transactions. This is by design but could be confusing to operators.

5. **No tax/fee mechanism:** Tax is hardcoded to 0. When a tax mechanism is introduced, the invoice calculation would need to account for it. The current `Invoice.Create` accepts `taxAmount` but the handler always passes 0.
