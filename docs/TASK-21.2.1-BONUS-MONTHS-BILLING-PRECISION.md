# TASK 21.2.1: Bonus Months & Billing Cycle Commercial Integrity

**Task Status:** CLOSED

**Date:** September 26, 2026

---

## Executive Summary

This task resolves two critical commercial integrity issues in the Centerix billing system:

1. **BonusMonths vs BillingCycle billing period**: Ensures bonus months (free entitlement) do not increase the billed amount.
2. **SnapshotMonthlyCharge decimal precision**: Fixes rounding drift that caused invoice totals to not match contracted amounts.

---

## 1. Bonus Months — Business Semantics

### Definition

**BonusMonths** represent free entitlement months granted as part of a promotion or contract. They are **NOT billable** — customers receive these months at no additional cost.

### Commercial Invariant

```
Invoice.TotalAmount
    =
Contract.ContractedAmount

(Even when BonusMonths > 0)
```

### Example

| Field | Value |
|-------|-------|
| Monthly list price | 1,000 |
| DurationMonths | 12 |
| BonusMonths | 2 |
| Discount | 1,200 |
| ContractedAmount | 10,800 |

**Expected behavior:**
- **Paid billing period:** 12 months
- **Free entitlement period:** 2 months
- **Invoice subtotal:** 12,000 (12 months × 1,000)
- **Invoice discount:** 1,200
- **Invoice total:** 10,800 (NOT 14,000)

---

## 2. BillingCycle Period Semantics

### Key Distinction

| Property | Definition | Purpose |
|----------|------------|---------|
| `BaseEndsAtUtc` | StartsAt + DurationMonths | Paid billing period end |
| `EffectiveEndsAtUtc` | BaseEndsAtUtc + BonusMonths | Total entitlement end |

### Commercial Rule

**BillingCycle.PeriodEnd** must represent the **paid billing period** (BaseEndsAtUtc), NOT the total entitlement period (EffectiveEndsAtUtc).

### Period Semantics

```
Paid Term              = Contract.DurationMonths          (billed)
Free Bonus Entitlement = Contract.BonusMonths             (NOT billed)
Subscription Effective  = Paid Term + Bonus Entitlement    (access period)
Invoiceable Period     = Paid Term                       (excludes bonus)
```

---

## 3. Precision Design Decision

### Problem

Previous implementation used `decimal(10,2)` for `SnapshotMonthlyCharge`, causing:

```csharp
ContractedAmount = 10,000
DurationMonths = 12
MonthlyCharge = 10,000 / 12 = 833.333...

// With decimal(10,2):
// Stored as 833.33
// Invoice total = 833.33 × 12 = 9,999.96
// Drift = 0.04 ❌
```

### Solution

**Option B** was selected: Use high precision internally AND settle final invoice amounts from authoritative Contract values.

- **SnapshotMonthlyCharge:** Increased from `decimal(10,2)` to `decimal(18,6)`
- **Full-term invoices:** Use `Contract.ContractedAmount` as authoritative total
- **Partial-term invoices:** Use calculated `SnapshotMonthlyCharge × duration` (high precision minimizes drift)

### Why Not Option A or C?

- **Option A (increase precision only):** With `decimal(18,6)`, 10,000/12 = 833.333333, and 833.333333 × 12 = 9,999.999996, which is still ~0.000004 drift.
- **Option B (authoritative Contract values):** Eliminates drift entirely for full-term invoices by using the pre-calculated `ContractedAmount`.
- **Option C (high precision + rounding settlement):** Adds complexity without benefit since we already have authoritative Contract values.

---

## 4. Implementation Changes

### 4.1 TenantPlanConfiguration.cs

**File:** `src/Centerix.Infrastructure/Data/Configurations/TenantPlanConfiguration.cs`

**Change:** Increased precision from `decimal(10,2)` to `decimal(18,6)`

```csharp
// Before
builder.Property(tp => tp.SnapshotPrice).HasPrecision(10, 2);
builder.Property(tp => tp.SnapshotMonthlyCharge).HasPrecision(10, 2);

// After
builder.Property(tp => tp.SnapshotPrice).HasPrecision(18, 6);
builder.Property(tp => tp.SnapshotMonthlyCharge).HasPrecision(18, 6);
```

### 4.2 RenewSubscriptionOfferCommand.cs

**File:** `src/Centerix.Application/Platform/Commands/RenewSubscriptionOfferCommand.cs`

**Change:** BillingCycle period uses `BaseEndsAtUtc` instead of `EffectiveEndsAtUtc`

```csharp
// Before
periodEnd: subscription.EffectiveEndsAtUtc

// After
periodEnd: subscription.BaseEndsAtUtc  // Paid term only
```

### 4.3 ChangeSubscriptionPlanCommand.cs

**File:** `src/Centerix.Application/Platform/Commands/ChangeSubscriptionPlanCommand.cs`

**Change:** Same as RenewSubscriptionOfferCommand — uses `BaseEndsAtUtc`

### 4.4 CreateInvoiceFromBillingCycleCommand.cs

**File:** `src/Centerix.Application/Platform/Billing/Commands/CreateInvoiceFromBillingCycleCommand.cs`

**Change:** For full-term billing cycles, use Contract values as authoritative source:

```csharp
if (contract != null && cycleDurationMonths == subscription.DurationMonths)
{
    // Full-term: use Contract as authoritative source
    subtotal = contract.GrossAmount;
    discountAmount = contract.DiscountAmount;
    totalAmount = contract.ContractedAmount;  // No rounding drift
}
else
{
    // Partial-term: calculate from snapshot
    subtotal = subscription.SnapshotPrice * cycleDurationMonths;
    discountAmount = (subscription.SnapshotPrice - subscription.SnapshotMonthlyCharge) * cycleDurationMonths;
    totalAmount = subscription.SnapshotMonthlyCharge * cycleDurationMonths;
}
```

---

## 5. PayForXMonths Behavior

### Scenario: 12 months entitlement, 10 months charged

| Field | Value |
|-------|-------|
| Monthly price | 1,000 |
| DurationMonths | 12 |
| ChargedMonths | 10 |
| BonusMonths | 0 |

**Calculation:**
- GrossAmount = 12,000 (12 × 1,000)
- DiscountAmount = 2,000 (free 2 months)
- ContractedAmount = 10,000

**Invoice:**
- Subtotal: 12,000
- Discount: 2,000
- Total: 10,000 ✅

**No rounding drift** because we use `Contract.ContractedAmount` as authoritative.

---

## 6. Bonus + Discount Combined Scenario

### Scenario: 12 paid months + 2 bonus months + 10% discount

| Field | Value |
|-------|-------|
| Monthly price | 1,000 |
| DurationMonths | 12 |
| BonusMonths | 2 |
| Discount | 10% |

**Calculation:**
- GrossAmount = 12,000 (12 × 1,000)
- DiscountAmount = 1,200 (10% of 12,000)
- ContractedAmount = 10,800

**BillingCycle Period:**
- PeriodStart: Jan 1, 2026
- PeriodEnd: Jan 1, 2027 (12 months, NOT 14)

**Invoice:**
- Subtotal: 12,000
- Discount: 1,200
- Total: 10,800 ✅

**Bonus months excluded from billing period.**

---

## 7. EF Core Migration

**Migration Name:** `Task_21_2_1_BillingPrecision`

**Changes:**
- `TenantPlans.SnapshotPrice`: decimal(10,2) → decimal(18,6)
- `TenantPlans.SnapshotMonthlyCharge`: decimal(10,2) → decimal(18,6)

**Note:** Centerix is a greenfield system. No legacy data repair is required.

---

## 8. Test Coverage

### Test File
`tests/Centerix.SecurityTests/TASK_21_2_1_BonusMonthsBillingPrecisionTests.cs`

### Test Scenarios

| # | Scenario | Status |
|---|----------|--------|
| 1 | 12 paid + 0 bonus | ✅ |
| 2 | 12 paid + 2 bonus | ✅ |
| 3 | 12 paid + 2 bonus + 10% discount | ✅ |
| 4 | 12 paid + 2 bonus + PayForXMonths | ✅ |
| 5 | PayForXMonths 12/10 with non-divisible charge | ✅ |
| 6 | Full-term invoice equals ContractedAmount | ✅ |
| 7 | BillingCycle period excludes bonus months | ✅ |
| 8 | EffectiveEndsAtUtc includes bonus months | ✅ |
| 9 | Plan mutation does not affect snapshot | ✅ |
| 10 | Promotion mutation does not affect snapshot | ✅ |
| 11 | Renewal command billing cycle excludes bonus | ✅ |
| 12 | Change plan command billing cycle excludes bonus | ✅ |
| 13 | Invoice arithmetic identity with bonus months | ✅ |
| 14 | Bonus + non-divisible charge no drift | ✅ |

### Test Results

```
Test summary: total: 14, failed: 0, succeeded: 14, skipped: 0
```

### Regression Tests

All existing TASK21.2 and Phase 8 billing tests continue to pass:
- TASK21.2 tests: 18/18 passed
- Phase 8 tests: 164/164 passed

---

## 9. SQL Server Verification

**Status:** NOT EXECUTED — SQL Server infrastructure unavailable

**Test commands that would be run:**

```sql
-- Verify precision in database
SELECT COLUMN_NAME, DATA_TYPE, NUMERIC_PRECISION, NUMERIC_SCALE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'TenantPlans'
  AND COLUMN_NAME IN ('SnapshotPrice', 'SnapshotMonthlyCharge');

-- Expected: NUMERIC_PRECISION = 18, NUMERIC_SCALE = 6
```

---

## 10. Scope Boundaries

### Modified
- `RenewSubscriptionOfferCommand.cs` — BillingCycle period end
- `ChangeSubscriptionPlanCommand.cs` — BillingCycle period end
- `CreateInvoiceFromBillingCycleCommand.cs` — Invoice amount calculation
- `TenantPlanConfiguration.cs` — EF Core precision

### NOT Modified (no regressions)
- Refund calculation
- Customer Credit
- Payment allocation
- Payment processing
- Promotion engine
- Contract lifecycle
- Subscription lifecycle (except period end for BillingCycle creation)
- Tenant authorization
- Identity

---

## 11. Closure Criteria Verification

| Criteria | Status |
|----------|--------|
| BonusMonths cannot increase billed months | ✅ Verified |
| Full-term BillingCycle invoice equals ContractedAmount | ✅ Verified |
| PayForXMonths has no rounding drift | ✅ Verified |
| SnapshotMonthlyCharge precision is financially safe | ✅ decimal(18,6) |
| Subscription EffectiveEndsAtUtc still includes bonus months | ✅ Verified |
| EF migration created | ✅ Task_21_2_1_BillingPrecision |
| Full regression passes | ✅ 182 tests passed |
| Documentation matches implementation | ✅ This document |

---

## 12. Related Documents

- [TASK-21.2-BILLING-CYCLE-INVOICE-LIFECYCLE-INTEGRITY.md](./TASK-21.2-BILLING-CYCLE-INVOICE-LIFECYCLE-INTEGRITY.md)
- [TASK-21.1.1-INVOICE-COMMERCIAL-AMOUNT-SEMANTICS.md](./TASK-21.1.1-INVOICE-COMMERCIAL-AMOUNT-SEMANTICS.md)
- [TASK-21.1.2-LEGACY-CONTRACT-SNAPSHOT-BILLING-CYCLE-CONSISTENCY.md](./TASK-21.1.2-LEGACY-CONTRACT-SNAPSHOT-BILLING-CYCLE-CONSISTENCY.md)
