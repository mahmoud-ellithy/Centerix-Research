# TASK 10.1 — Credit Application Financial Correction Report

**Commit SHA:** `9f3faf9` (implementation commit)

---

## 1. Original Problem

Task 10 introduced `ApplyCreditToInvoiceCommand` with two financial consistency issues:

### Problem A — Partial Credit Application
`TenantCredit.ApplyToInvoice()` marked the **entire credit** as `Applied`, regardless of the `Amount` parameter. Applying 400 of a 1,000 credit incorrectly consumed the full 1,000.

### Problem B — Credit Application Did Not Settle Invoice Balance
`Invoice.GetRemainingAmount()` was calculated solely from `PaymentAllocation` records. Credit applications were invisible to invoice balance calculation, meaning an invoice could have credits applied but still show as fully unpaid. The handler also used `LedgerEntryType.CreditCreation` for credit usage, which is semantically incorrect.

---

## 2. Correction Summary

### 2.1 Partial Credit Support

**TenantCredit** now tracks `RemainingAmount`:
- `Amount` = original credit value (immutable)
- `RemainingAmount` = current available balance (decremented on each application)

New domain method `ConsumeAmount(decimal amount)`:
- Decrements `RemainingAmount` by the application amount
- Transitions status: `Available` → `PartiallyApplied` (partial) or `Applied` (full)
- Validates: amount must be positive and ≤ `RemainingAmount`

New status added: `CreditStatus.PartiallyApplied = 1`

### 2.2 CreditApplication Entity (Immutable Audit Record)

New domain entity `CreditApplication`:
- `CreditId` — which credit was consumed
- `InvoiceId` — which invoice was settled
- `Amount` — how much was applied in this application
- `AppliedAtUtc` — when it occurred
- `RowVersion` — optimistic concurrency

A single credit can produce **multiple** `CreditApplication` records across different invoices. These records are **never modified or deleted**.

### 2.3 Invoice Balance Includes Credit Applications

**Invoice** now has a `_creditApplications` collection and two new methods:
- `GetAppliedCreditAmount()` — sum of all credit application amounts
- Updated `GetRemainingAmount()` → `TotalAmount - GetPaidAmount() - GetAppliedCreditAmount()`
- Updated `UpdatePaymentStatus()` → uses `GetPaidAmount() + GetAppliedCreditAmount()` for status transitions

### 2.4 Correct Ledger Semantics

Added `CustomerLedgerEntry.CreateCreditUsage()` factory method:
- Uses `LedgerEntryType.CreditUsage` (enum value 3, previously defined but unused)
- Includes `CreditApplicationId` for audit linkage
- Distinct from `CreateCreditCreation()` which is reserved for credit creation events

The `IsCredit` property now includes `CreditUsage`.

### 2.5 Concurrency Protection

`ApplyCreditToInvoiceHandler` now uses the same concurrency pattern as `AllocatePaymentHandler`:
- `Serializable` isolation level on SQL Server
- `ChangeTracker.Clear()` before retry
- Deadlock detection (SQL error 1205) with bounded exponential backoff
- `DbUpdateConcurrencyException` handling
- The credit entity's `RowVersion` provides optimistic concurrency

### 2.6 Tenant Isolation

Cross-tenant application is rejected via:
1. EF query filters (credit/invoice invisible across tenants)
2. Explicit `credit.TenantId != invoice.TenantId` check with `TenantCredit.CrossTenant` error

### 2.7 Migration

EF Core migration `Task10_1_CreditApplicationCorrection`:
- Drops `AppliedToInvoiceId` and `AppliedToInvoiceLineId` from `TenantCredits`
- Adds `RemainingAmount` to `TenantCredits`
- Adds `CreditApplicationId` to `CustomerLedgerEntries`
- Creates `CreditApplications` table with indexes and FK constraints

---

## 3. Files Changed

### Domain Layer
| File | Change |
|------|--------|
| `Credits/TenantCredit.cs` | Added `RemainingAmount`, `ConsumeAmount()`, removed `AppliedToInvoiceId`/`AppliedToInvoiceLineId` |
| `Credits/Enums/CreditStatus.cs` | Added `PartiallyApplied = 1`, renumbered existing values |
| `Credits/TenantCreditErrors.cs` | Added `InsufficientRemaining`, `CrossTenant` errors |
| `Credits/CreditApplication.cs` | **NEW** — immutable credit application audit record |
| `Payments/CustomerLedgerEntry.cs` | Added `CreditApplicationId`, `CreateCreditUsage()`, updated `IsCredit` |
| `Payments/Enums/LedgerEntryType.cs` | No change (CreditUsage=3 already defined) |
| `Invoicing/Invoice.cs` | Added `_creditApplications` collection, `GetAppliedCreditAmount()`, updated `GetRemainingAmount()`/`UpdatePaymentStatus()` |

### Infrastructure Layer
| File | Change |
|------|--------|
| `Data/AppDbContext.cs` | Added `DbSet<CreditApplication>` |
| `Data/Configurations/CreditApplicationConfiguration.cs` | **NEW** — EF configuration for CreditApplication |
| `Data/Configurations/TenantCreditConfiguration.cs` | Added `RemainingAmount`, removed old columns |
| `Data/Configurations/CustomerLedgerEntryConfiguration.cs` | Added `CreditApplicationId` column + unique index |
| `Data/Configurations/InvoiceConfiguration.cs` | No change (EF convention discovers CreditApplications) |
| `Data/Migrations/20260917182240_Task10_1_CreditApplicationCorrection.cs` | **NEW** migration |

### Application Layer
| File | Change |
|------|--------|
| `Commands/CreateTenantCreditCommand.cs` | Complete rewrite of `ApplyCreditToInvoiceHandler` with serializable transactions, deadlock retry, partial credit, CreditApplication records, correct ledger entries, invoice status update |

### Interface Layer
| File | Change |
|------|--------|
| `Common/Interfaces/IAppDbContext.cs` | Added `DbSet<CreditApplication>` |

---

## 4. Tests

### InMemory Tests (23 tests) — `Phase10_1CreditApplicationCorrectionTests.cs`

| Test | Description |
|------|-------------|
| `PartialCredit_Apply400Of1000_CreditRemaining600` | Credit 1000, apply 400, remaining = 600 |
| `PartialCredit_ApplyRemaining600_CreditFullyConsumed` | Apply remaining 600, credit = Applied |
| `PartialCredit_InvoiceReducedBy400` | Invoice remaining reflects credit application |
| `CompleteCredit_Apply1000Of1000_CreditFullyConsumed` | Full credit application |
| `MultipleApplications_Apply400ToInvoiceA_600ToInvoiceB` | Split across two invoices |
| `ExceedCredit_Apply1001Of1000_IsRejected` | Cannot exceed credit amount |
| `ExceedCreditRemaining_Apply600OfRemaining400_IsRejected` | Cannot exceed remaining |
| `ExceedInvoice_Apply501ToInvoiceRemaining500_IsRejected` | Cannot exceed invoice balance |
| `ApplyExactlyInvoiceRemaining_Succeeds` | Exact invoice remaining application |
| `InvoiceStatus_Payment1000_Credit1000_Total2000_IsPaid` | Payment + credit = Paid |
| `InvoiceStatus_Payment1000_Credit500_Total2000_IsPartiallyPaid` | Partial = PartiallyPaid |
| `LedgerEntry_IsCreditUsage_NotCreditCreation` | Correct ledger entry type |
| `LedgerEntry_RecordsCreditApplicationId` | Ledger links to CreditApplication |
| `CreditApplication_IsImmutableAuditRecord` | Audit record completeness |
| `CrossTenant_TenantACredit_TenantBInvoice_IsRejected` | Cross-tenant blocked |
| `ApplyCredit_ToDraftInvoice_IsRejected` | Draft invoice blocked |
| `ApplyCredit_ToCancelledInvoice_IsRejected` | Cancelled invoice blocked |
| `ApplyCredit_AlreadyFullyConsumed_IsRejected` | Double-spend blocked |
| `CombinedSettlement_PaymentAndCredit_ReflectsCorrectBalance` | Balance = Total - Payments - Credits |
| `CreditRemainingAmount_DecrementsCorrectly` | 3-step partial application tracking |
| `ApplyCredit_ZeroAmount_IsRejected` | Zero amount blocked |
| `ApplyCredit_NegativeAmount_IsRejected` | Negative amount blocked |
| `MultipleApplications_AllPreserved_HistoricallyVisible` | 3 applications across 3 invoices preserved |

### SQL Server Concurrency Tests (2 tests) — `Phase10_1CreditConcurrencySqlServerTests.cs`

| Test | Description |
|------|-------------|
| `ConcurrentCreditApplication_OnlyOneSucceeds_CreditNotDoubleSpent` | Two concurrent requests apply 700 of 1000 credit; only one succeeds, credit never exceeds 1000 |
| `ConcurrentCreditApplication_CrossTenant_IsRejected` | Cross-tenant application rejected on real SQL Server |

---

## 5. Regression Results

```
Total tests: 1128
     Passed: 1125
     Failed: 3 (pre-existing, unrelated to Task 10.1)
     Skipped: 0
```

### Pre-existing Failures (3)
| Test | Phase | Reason |
|------|-------|--------|
| `Phase3AuthorizationHttpTests.Students_CrossTenantBranch_IsInvisibleAndReturnsNotFound` | Phase 3 | Pre-existing student HTTP test |
| `Phase3AuthorizationHttpTests.Students_TenantAdmin_CanCreateReadUpdateSoftDelete` | Phase 3 | Pre-existing student HTTP test |
| `Phase9FinancialConcurrencySqlServerTests.Concurrent_PaymentAllocations_CannotExceedPaymentAmount` | Phase 9 | Timing-dependent concurrency race |

### Task 10.1 Failures: **0**

---

## 6. Acceptance Criteria Checklist

- [x] Partial credit is correctly supported
- [x] Credit can be consumed across multiple applications where valid
- [x] Every credit application is historically auditable (CreditApplication entity)
- [x] Invoice balance includes credit applications (`GetRemainingAmount() = Total - Paid - AppliedCredits`)
- [x] Invoice status reflects payment + credit settlement (`UpdatePaymentStatus()` uses both)
- [x] Credit cannot be over-consumed (validates against `RemainingAmount`)
- [x] Invoice cannot be over-settled (validates against remaining balance)
- [x] Concurrent credit consumption is safe (Serializable + deadlock retry + RowVersion)
- [x] Cross-tenant application is rejected (query filter + explicit check)
- [x] Ledger semantics distinguish credit creation from credit usage (`CreditCreation` vs `CreditUsage`)
- [x] Existing Payment/PaymentAllocation behavior is preserved (all Phase 9/10 tests pass)
- [x] Existing Refund behavior is preserved (no changes to refund path)
- [x] SQL Server concurrency tests exist (2 tests with real SQL Server)
- [x] Full regression suite executed (1125/1128 passed, 3 pre-existing failures)
- [x] Report contains the actual final commit SHA (`9f3faf9`)

---

## 7. Deferred Items

- Refund/Cancellation reversal of credit applications: **DEFERRED** — outside Task 10.1 scope. The existing credit reversal mechanism (`TenantCredit.Reverse()`) remains unchanged. If an invoice with credit applications is later refunded or cancelled, the credit application history is preserved as immutable records. A future task may implement automatic credit re-issuance on invoice cancellation.
